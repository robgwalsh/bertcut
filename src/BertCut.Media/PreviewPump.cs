using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Core.Timeline;
using BertCut.Media.Decode;

namespace BertCut.Media;

/// <summary>
/// Runs a <see cref="PreviewEngine"/> on a thread of its own, and reads ahead of the playhead.
/// </summary>
/// <remarks>
/// <para>
/// Decoding used to happen inline in the playhead's setter, on the UI thread. Advancing one
/// frame is cheap enough to get away with, but a genuine seek is not — 115 ms at 1280x768,
/// 195 ms at 1080p — and it was spent on the thread that also has to repaint, so clicking
/// the ruler froze the window and dragging along it froze the window per pointer move.
/// </para>
/// <para>
/// <b>Requests are latest-wins.</b> There is one slot, and a new request overwrites whatever
/// was in it. A scrub therefore costs one seek per place the pointer actually rested rather
/// than one per pixel it passed through, and the UI thread never waits for either.
/// </para>
/// <para>
/// <b>The ring is what makes a stall invisible.</b> Having served the frame it was asked for,
/// the pump keeps decoding ahead until it runs out of buffers or a new request arrives. A GC
/// pause, a layout pass, or the seek at a cut boundary is then absorbed by frames that were
/// decoded before anyone needed them. Reverse is the same mechanism read the other way:
/// there is no route to an earlier frame but the preceding keyframe, so rather than pay that
/// per frame, the pump fills the whole window behind the playhead in one forward run and
/// serves it backwards.
/// </para>
/// <para>
/// Everything that touches the engine — rendering, resizing, resetting, disposing — happens
/// on the pump thread. The decoders under it are not thread-safe, and freeing one from the UI
/// thread while <c>sws_scale</c> is reading it would be a use-after-free rather than a race
/// with a wrong answer.
/// </para>
/// </remarks>
public sealed class PreviewPump : IDisposable
{
    /// <summary>
    /// How much memory the read-ahead ring may occupy.
    /// </summary>
    /// <remarks>
    /// A count would not do: a frame is 1 MB at 640x384 and 24 MB at 4K, so thirty-two of
    /// them is either trivial or three quarters of a gigabyte. Budgeting bytes makes the depth
    /// fall out of the resolution on its own — and the resolution is the displayed size now,
    /// not the project's, so the usual case is well under the ceiling.
    /// </remarks>
    private const int RingBudgetBytes = 128 * 1024 * 1024;

    /// <summary>
    /// Deep enough at the top end to hold two reverse windows, which is what lets one be
    /// filled while the playhead is still walking through the other.
    /// </summary>
    private const int MaxSlots = 32;

    private const int MinSlots = 4;

    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _work = new(false);
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly Queue<Action> _commands = new();
    private readonly Thread _thread;

    // Owned by the pump thread. Nothing else may touch either.
    private readonly PreviewEngine _engine;
    private TimelineResolver? _resolver;

    private Slot[] _ring = [];

    private Project? _wantProject;
    private long _wantFrame;
    private int _wantDirection;
    private bool _wantPending;
    private bool _stopping;
    private bool _disposed;

    public PreviewPump(OutputFormat output, Func<int, SourceIndex> indexOf, Func<int, string> pathOf)
    {
        _engine = new PreviewEngine(output, indexOf, pathOf);
        RebuildRing();

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "BertCut preview",
        };

        _thread.Start();
    }

    /// <summary>Raised on the pump thread when the requested frame is ready to lease.</summary>
    public event Action? FrameReady;

    /// <summary>Raised on the pump thread when a frame could not be decoded.</summary>
    public event Action<string>? Failed;

    /// <summary>The size frames are being composited at.</summary>
    public int RenderWidth => _engine.RenderWidth;

    public int RenderHeight => _engine.RenderHeight;

    /// <summary>How many frames the ring can hold.</summary>
    internal int Slots => _ring.Length;

    /// <summary>Frames rendered since construction — the measure a test counts work by.</summary>
    internal int RenderCount { get; private set; }

    /// <summary>Whether the ring holds a frame, without taking it. For tests.</summary>
    internal bool Holds(long frame)
    {
        lock (_gate)
        {
            foreach (var slot in _ring)
                if (slot.Index == frame) return true;

            return false;
        }
    }

    /// <summary>
    /// Asks for <paramref name="frame"/> of <paramref name="project"/>. Never blocks.
    /// </summary>
    /// <param name="direction">
    /// Which way the playhead is travelling, which is what the read-ahead follows. Zero — a
    /// seek, or a paused editor — reads ahead forwards, because that is the direction the
    /// next keystroke is most likely to go.
    /// </param>
    public void Request(Project project, long frame, int direction)
    {
        ArgumentNullException.ThrowIfNull(project);

        lock (_gate)
        {
            if (_stopping) return;

            _wantProject = project;
            _wantFrame = frame;
            _wantDirection = direction;
            _wantPending = true;

            _idle.Reset();
        }

        _work.Set();
    }

    /// <summary>
    /// Takes the buffer holding <paramref name="frame"/>, or null when the pump has not
    /// produced it — or when the caller is already holding it.
    /// </summary>
    /// <remarks>
    /// A lease, not a copy. The producer will not write over a slot that is out on loan, so
    /// the caller may read it — <c>WritePixels</c> straight out of it — until it
    /// <see cref="Return"/>s it. Exactly one frame is expected to be out at a time: the one
    /// on screen.
    /// </remarks>
    public DecodedFrame? Lease(long frame)
    {
        lock (_gate)
        {
            foreach (var slot in _ring)
            {
                if (slot.Index != frame || slot.InUse) continue;

                slot.InUse = true;
                return slot.Frame;
            }

            return null;
        }
    }

    /// <summary>
    /// Takes the buffer nearest <paramref name="frame"/>, or null when there is nothing
    /// closer to it than <paramref name="holding"/> already is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a scrub needs, and the difference between a drag that moves and one that does not.
    /// Insisting on the exact frame looks right — it is what playback wants, and during
    /// playback the ring holds it anyway — but a drag outruns the decoder by construction:
    /// every position is a seek, the playhead has moved again by the time one lands, and a
    /// frame that is no longer the exact answer gets thrown away. The picture then only
    /// changes when the pointer happens to rest.
    /// </para>
    /// <para>
    /// So the question is not "is this the frame" but "is this closer than what is on screen".
    /// A frame a few away from the playhead during a drag is worth far more than a correct one
    /// from wherever the pointer started.
    /// </para>
    /// </remarks>
    public DecodedFrame? LeaseNearest(long frame, long? holding)
    {
        lock (_gate)
        {
            Slot? best = null;
            var closest = holding is { } held ? Math.Abs(held - frame) : long.MaxValue;

            foreach (var slot in _ring)
            {
                if (slot.Index < 0 || slot.InUse) continue;

                var distance = Math.Abs(slot.Index - frame);
                if (distance >= closest) continue;

                closest = distance;
                best = slot;
            }

            if (best is null) return null;

            best.InUse = true;
            return best.Frame;
        }
    }

    /// <summary>Hands a leased buffer back. Ignores one the ring no longer owns.</summary>
    public void Return(DecodedFrame frame)
    {
        if (frame is null) return;

        lock (_gate)
        {
            foreach (var slot in _ring)
            {
                if (!ReferenceEquals(slot.Frame, frame)) continue;

                slot.InUse = false;
                return;
            }
        }
    }

    /// <summary>Re-points the engine at a changed output format.</summary>
    public void SetOutput(OutputFormat output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Post(() =>
        {
            var before = (_engine.RenderWidth, _engine.RenderHeight);
            _engine.SetOutput(output);

            if (before != (_engine.RenderWidth, _engine.RenderHeight)) RebuildRing();
            else InvalidateRing();
        });
    }

    /// <summary>Composites at this size rather than at the output size.</summary>
    public void SetRenderSize(int width, int height)
    {
        Post(() =>
        {
            if (width == _engine.RenderWidth && height == _engine.RenderHeight) return;

            _engine.SetRenderSize(width, height);
            RebuildRing();
        });
    }

    /// <summary>Closes every decoder and drops everything read ahead.</summary>
    public void Reset() => Post(() =>
    {
        _engine.Reset();
        _resolver = null;
        InvalidateRing();
    });

    /// <summary>
    /// Waits until the frame last requested has been rendered and announced.
    /// </summary>
    /// <remarks>
    /// Read-ahead deliberately does not hold this open. Idle means "what you asked for is
    /// ready", not "there is no work left" — otherwise every scripted command would wait out
    /// a dozen speculative frames nobody had asked about yet.
    /// </remarks>
    public bool WaitForIdle(TimeSpan timeout) => _idle.Wait(timeout);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _stopping = true;
            _wantPending = false;
        }

        _work.Set();

        // The engine is disposed by the loop on its way out, so the decoders are freed on the
        // thread that opened them and never underneath a render in flight.
        if (!_thread.Join(TimeSpan.FromSeconds(5))) _engine.Dispose();

        _work.Dispose();
        _idle.Dispose();
    }

    private void Post(Action command)
    {
        lock (_gate)
        {
            if (_stopping) return;

            _commands.Enqueue(command);
            _idle.Reset();
        }

        _work.Set();
    }

    // ---- the pump thread ---------------------------------------------------------------

    private void Run()
    {
        try
        {
            while (true)
            {
                _work.Wait();
                _work.Reset();

                if (!DrainCommands()) return;
                if (!Serve()) return;
            }
        }
        finally
        {
            _engine.Dispose();
        }
    }

    /// <summary>Runs queued engine commands. False when the pump is being disposed.</summary>
    private bool DrainCommands()
    {
        while (true)
        {
            Action command;

            lock (_gate)
            {
                if (_stopping) return false;
                if (_commands.Count == 0) return true;

                command = _commands.Dequeue();
            }

            try
            {
                command();
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or FfmpegDecodeException)
            {
                Failed?.Invoke(e.Message);
            }
        }
    }

    /// <summary>Renders the outstanding request, then reads ahead of it.</summary>
    private bool Serve()
    {
        Project project;
        long frame;
        int direction;

        lock (_gate)
        {
            if (_stopping) return false;

            if (!_wantPending || _wantProject is null)
            {
                if (_commands.Count == 0) _idle.Set();
                return true;
            }

            project = _wantProject;
            frame = _wantFrame;
            direction = _wantDirection;
            _wantPending = false;
        }

        if (_resolver is null || !ReferenceEquals(_resolver.Project, project))
            _resolver = new TimelineResolver(project);

        if (direction < 0)
        {
            ServeReverse(project, frame);
        }
        else
        {
            if (Fill(frame, frame, direction)) Announce();
            ReadAhead(project, frame, direction);
        }

        lock (_gate)
        {
            if (!_wantPending && _commands.Count == 0) _idle.Set();
        }

        return true;
    }

    /// <summary>
    /// Says the requested frame is leasable, then reports idle.
    /// </summary>
    /// <remarks>
    /// That order matters to the harness. The subscriber marshals a repaint onto the UI
    /// thread, and <see cref="WaitForIdle"/> is followed there by a dispatcher pump — so
    /// raising first is what guarantees the repaint is already queued by the time the wait
    /// returns, rather than landing after the capture.
    /// </remarks>
    private void Announce()
    {
        FrameReady?.Invoke();

        lock (_gate)
        {
            if (!_wantPending && _commands.Count == 0) _idle.Set();
        }
    }

    /// <summary>
    /// Decodes the frames after <paramref name="frame"/> that the ring does not already hold.
    /// </summary>
    /// <remarks>
    /// Each costs one packet, so this is nearly free and is abandoned the moment anything
    /// else is asked for. It is what a hiccup is paid out of: a GC pause, a layout pass or a
    /// seek at a cut boundary lands on frames that were decoded before anyone wanted them.
    /// </remarks>
    private void ReadAhead(Project project, long frame, int direction)
    {
        var duration = project.DurationFrames;
        if (duration <= 0) return;

        var depth = Depth;
        if (depth < 1) return;

        var end = Math.Min(duration - 1, frame + depth - 1);

        for (var f = frame + 1; f <= end; f++)
        {
            if (HasWork()) return;
            Fill(f, frame, direction);
        }
    }

    /// <summary>
    /// Serves a backwards-travelling playhead by filling a whole window at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no route to an earlier frame but the preceding keyframe, so a backwards step
    /// is a seek — ~85 ms on a 1280x768 recording with the 250-frame GOP a screen recorder
    /// writes. Served one frame at a time that is not merely slow, it never converges: the
    /// seek outlasts the frame period, so by the time it finishes the playhead has moved and
    /// asked for another, and the speculative fill behind it never gets a turn at all.
    /// </para>
    /// <para>
    /// So the window is the unit rather than the frame. Frames are quantised into fixed
    /// chunks the width of the ring and a chunk is filled in one <em>ascending</em> run —
    /// one seek, then a sequential decode — after which the playhead walks back through it
    /// for free. Quantising rather than centring on the playhead is what stops the window
    /// sliding one frame at a time and reintroducing the seek it exists to avoid.
    /// </para>
    /// <para>
    /// The run is not abandoned for a request that lands <em>inside</em> the window it is
    /// already filling, which would pay the seek again for a frame it was about to reach.
    /// One landing outside it — a scrub, a jump — still wins immediately.
    /// </para>
    /// </remarks>
    private void ServeReverse(Project project, long frame)
    {
        var window = Window;
        if (window < 1) return;

        var start = frame / window * window;

        if (Holds(frame))
        {
            Announce();
        }
        else
        {
            for (var f = start; f <= frame; f++)
            {
                if (Interrupted(start, frame)) return;
                Fill(f, frame, -1);
            }

            if (Holds(frame)) Announce();
        }

        // The window before this one, decoded while the playhead is still walking through
        // this one. Without it every boundary is a visible hold: the ring runs dry exactly
        // when the next seek has to happen, and the picture stops for the length of it. The
        // ring is sized to hold two windows precisely so this one has somewhere to go.
        var earlier = start - window;
        if (earlier < 0) return;

        for (var f = earlier; f < start; f++)
        {
            if (HasWork()) return;
            Fill(f, frame, -1);
        }
    }

    /// <summary>Frames per reverse window — half the ring, so two of them fit.</summary>
    internal int Window => Math.Max(1, Depth / 2);

    /// <summary>
    /// How far ahead the ring may run: one slot short of its length.
    /// </summary>
    /// <remarks>
    /// The last slot is kept back for the frame on screen, which is on loan out of the ring
    /// and is usually the one the playhead has just left. Filling into it instead would evict
    /// the frame about to be asked for, decode it again, and evict the read-ahead to make
    /// room — a treadmill that decodes everything twice and delivers nothing early.
    /// </remarks>
    private int Depth => _ring.Length - 1;

    private bool HasWork()
    {
        lock (_gate) return _wantPending || _commands.Count > 0 || _stopping;
    }

    /// <summary>Whether something has come up that a fill of this window should stop for.</summary>
    private bool Interrupted(long from, long to)
    {
        lock (_gate)
        {
            if (_stopping || _commands.Count > 0) return true;

            return _wantPending && (_wantFrame < from || _wantFrame > to);
        }
    }

    /// <summary>
    /// Ensures the ring holds <paramref name="frame"/>. True when it does, however it got there.
    /// </summary>
    /// <param name="playhead">
    /// Where the user is, which is what decides who gets evicted to make room. Not the same
    /// as <paramref name="frame"/> whenever this is speculative — and a reverse prefetch runs
    /// a whole window away from the playhead, so ranking against the frame being written would
    /// throw out precisely the frames about to be displayed.
    /// </param>
    private bool Fill(long frame, long playhead, int direction)
    {
        Slot? slot;

        lock (_gate)
        {
            if (_ring.Length == 0) return false;

            // Already read ahead to it — the whole point of the ring.
            foreach (var candidate in _ring)
                if (candidate.Index == frame) return true;

            slot = Claim(playhead, direction);
            if (slot is null) return false;

            // Marked occupied and indexless for the duration of the render, so a lease can
            // never hand out a buffer that is half-written.
            slot.Index = -1;
            slot.InUse = true;
        }

        var rendered = false;

        try
        {
            rendered = _engine.Render(_resolver!, frame, slot.Frame);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FfmpegDecodeException)
        {
            Failed?.Invoke(e.Message);
        }
        finally
        {
            RenderCount++;

            lock (_gate)
            {
                slot.Index = rendered ? frame : -1;
                slot.InUse = false;
            }
        }

        return rendered;
    }

    /// <summary>
    /// Picks the slot to overwrite: an empty one, else the least useful given where the
    /// playhead is and which way it is going. Null when every slot is out on loan.
    /// </summary>
    /// <remarks>
    /// Direction is what makes this correct rather than merely plausible. Ranking by plain
    /// distance evicts the far end of the read-ahead — which is the frame that will be wanted
    /// next — while leaving the ones already behind the playhead untouched, so the ring churns
    /// and never gets ahead of anything. Frames the playhead has passed go first,
    /// furthest-passed first; only then the ones ahead, least urgent first.
    /// </remarks>
    private Slot? Claim(long playhead, int direction)
    {
        var forward = direction >= 0;

        Slot? worst = null;
        var worstPassed = false;
        var worstDistance = -1L;

        foreach (var slot in _ring)
        {
            if (slot.InUse) continue;
            if (slot.Index < 0) return slot;

            var lookahead = forward ? slot.Index - playhead : playhead - slot.Index;
            var passed = lookahead < 0;
            var distance = Math.Abs(lookahead);

            var better = worst is null
                || (passed && !worstPassed)
                || (passed == worstPassed && distance > worstDistance);

            if (!better) continue;

            worst = slot;
            worstPassed = passed;
            worstDistance = distance;
        }

        return worst;
    }

    /// <summary>Drops what has been read ahead without reallocating it.</summary>
    private void InvalidateRing()
    {
        lock (_gate)
        {
            foreach (var slot in _ring)
                if (!slot.InUse)
                    slot.Index = -1;
        }
    }

    private void RebuildRing()
    {
        var bytes = (long)_engine.RenderWidth * _engine.RenderHeight * 4;
        var slots = (int)Math.Clamp(RingBudgetBytes / Math.Max(1, bytes), MinSlots, MaxSlots);

        var ring = new Slot[slots];
        for (var i = 0; i < slots; i++) ring[i] = new Slot(_engine.NewFrame());

        // The frame currently on screen was leased out of the old ring. Its buffer stays
        // valid — it is a managed array — so the display holds still until the first frame of
        // the new size arrives, and Return simply finds nothing to give back.
        lock (_gate) _ring = ring;
    }

    private sealed class Slot(DecodedFrame frame)
    {
        public DecodedFrame Frame { get; } = frame;

        /// <summary>Timeline frame held, or -1 when empty or mid-render.</summary>
        public long Index { get; set; } = -1;

        /// <summary>Set while the consumer is displaying it, or the producer is filling it.</summary>
        public bool InUse { get; set; }
    }
}
