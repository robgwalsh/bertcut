using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using BertCut.Core.Audio;
using BertCut.Core.Edits;
using BertCut.Core.Export;
using BertCut.Core.Input;
using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Core.Session;
using BertCut.Core.Time;
using BertCut.Core.Timeline;
using BertCut.Media;
using BertCut.Media.Audio;
using BertCut.Media.Decode;

namespace BertCut.App;

/// <summary>
/// Editor state and the operations the key map dispatches to.
/// </summary>
/// <remarks>
/// Owned entirely by the UI thread. Playback advances from
/// <c>CompositionTarget.Rendering</c> rather than a timer, so frames are produced in step
/// with WPF's own compositor instead of racing it.
/// </remarks>
public sealed class EditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly FfmpegRuntime _runtime;
    private readonly Dictionary<int, SourceIndex> _indices = [];
    private readonly DispatcherTimer _autosave;
    private readonly Stopwatch _clock = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly AudioPlayer _player;

    /// <summary>
    /// Each source's audio envelope, once it has been built.
    /// </summary>
    /// <remarks>
    /// Concurrent because it is written by the background build and by the sync operation,
    /// and read by the timeline's waveform on the UI thread. The <see cref="Project"/> those
    /// threads read alongside it is immutable, so this dictionary is the only shared mutable
    /// state in the audio path.
    /// </remarks>
    private readonly ConcurrentDictionary<int, AudioPeaks?> _peaks = new();

    private EditorDocument _document = new(EmptyProject());
    private TimelineResolver _resolver;
    private PreviewPump? _pump;
    private DecodedFrame? _frame;
    private string? _sessionKey;
    private bool _isMuted;

    /// <summary>
    /// The render size the shell has asked for, kept so a newly opened video adopts it
    /// without waiting for the next window resize.
    /// </summary>
    private (int Width, int Height)? _renderSize;

    private EditorMode _mode = EditorMode.Normal;
    private RectI _pendingRect;
    private FrameRange _pendingRange;
    private OverlayContent? _pendingContent;
    private long _playhead;
    private long? _markIn;
    private long? _markOut;
    private int _shuttleRate;
    private long _playbackStartFrame;
    private string _status = "Press Ctrl+O to open a video.";
    private bool _isBusy;

    /// <param name="audioOutput">
    /// Where preview audio goes. Defaults to the sound card, falling back to silence when
    /// there is none. The harness passes a silent one, because a scripted run must not make
    /// noise on the user's machine any more than it should put a window on their screen.
    /// </param>
    public EditorViewModel(FfmpegRuntime runtime, Func<IAudioOutput>? audioOutput = null)
    {
        _runtime = runtime;
        _resolver = new TimelineResolver(_document.Current);
        _player = new AudioPlayer(audioOutput ?? AudioPlayer.DefaultOutput);

        _document.Changed += OnDocumentChanged;

        // Debounced so a burst of edits writes once. Autosave is the only persistence
        // this app has, so it must be frequent enough to trust and cheap enough to ignore.
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _autosave.Tick += (_, _) => { _autosave.Stop(); SaveSession(); };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the UI thread when a new composited frame is ready to present.</summary>
    /// <remarks>
    /// Carries the frame rather than letting the shell reach back through the pump for it.
    /// The buffer is on loan for as long as it is the one on screen, and only this class
    /// knows when that stops being true.
    /// </remarks>
    public event Action<DecodedFrame>? FrameChanged;

    /// <summary>
    /// Raised when a source's audio envelope became available.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PropertyChanged"/> because the waveform is expensive to
    /// rebuild and the timeline repaints on every property change — including the playhead's,
    /// sixty times a second during playback. This fires once per source, when there is
    /// genuinely new geometry to build.
    /// </remarks>
    public event Action? PeaksChanged;

    /// <summary>
    /// Raised when opening a video brought last session's edits back with it, carrying the
    /// video's file name.
    /// </summary>
    /// <remarks>
    /// An event rather than a status string because the shell says this twice, in two
    /// registers: once in the status bar, where it scrolls past, and once over the picture,
    /// where it has to be dismissed. The view model owns the fact; the window decides how
    /// loudly to say it.
    /// </remarks>
    public event Action<string>? SessionRestored;

    public Project Project => _document.Current;

    /// <summary>The composited frame currently on screen, or null before the first one.</summary>
    public DecodedFrame? CurrentFrame => _frame;

    /// <summary>
    /// The size the preview is being composited at, which is the displayed size rather than
    /// the project's. Null with nothing open.
    /// </summary>
    public (int Width, int Height)? PreviewRenderSize =>
        _pump is null ? null : (_pump.RenderWidth, _pump.RenderHeight);

    public EditorMode Mode
    {
        get => _mode;
        private set
        {
            if (_mode == value) return;

            _mode = value;
            Notify();
            Notify(nameof(IsPlacing));
            Notify(nameof(IsChoosingOverlaySource));
        }
    }

    /// <summary>True while a crop or overlay rectangle is being positioned.</summary>
    /// <remarks>
    /// Named for the rectangle rather than for the mode, and asked by name rather than as
    /// "not Normal": <see cref="EditorMode.OverlaySource"/> is a question about which clip,
    /// asked before there is a rectangle to put anywhere. <c>RectEditor</c> watches this, and
    /// a box appearing behind the card would be answering a question nobody had asked yet.
    /// </remarks>
    public bool IsPlacing => Mode is EditorMode.Crop or EditorMode.Overlay;

    /// <summary>True while the overlay source card is up.</summary>
    public bool IsChoosingOverlaySource => Mode == EditorMode.OverlaySource;

    /// <summary>The rectangle being positioned, in output-space pixels.</summary>
    public RectI PendingRect
    {
        get => _pendingRect;
        private set
        {
            _pendingRect = value;
            Notify();
            RenderCurrentFrame();
        }
    }

    /// <summary>The timeline range the pending rectangle will apply to.</summary>
    /// <remarks>
    /// A crop's range is the marks it was started from and does not move. An overlay's is
    /// worked out from the playhead every time the playhead moves, so that the faint band on
    /// the strip is the answer to "where would this go if I pressed Enter now" rather than a
    /// decision taken once when the source was chosen.
    /// </remarks>
    public FrameRange PendingRange
    {
        get => _pendingRange;
        private set
        {
            if (_pendingRange == value) return;

            _pendingRange = value;
            Notify();
        }
    }

    /// <summary>
    /// What a pending overlay shows: settled when its source is chosen, and never touched
    /// again while it is being positioned.
    /// </summary>
    /// <remarks>
    /// The two questions an overlay asks are deliberately separated. This one — which frames,
    /// and how many — is answered by the card. Where they go is answered by the playhead, and
    /// moving the clip along the timeline must never quietly change what is in it.
    /// </remarks>
    public OverlayContent? PendingOverlayContent => _pendingContent;

    /// <summary>Which source the pending overlay draws from.</summary>
    public int PendingOverlaySourceId => _pendingContent?.SourceId ?? 0;

    /// <summary>Where in that source the pending overlay starts.</summary>
    public long PendingOverlaySourceStart => _pendingContent?.SourceStartFrame ?? 0;

    // There used to be an OverlaySourceId here — "the most recently imported file, or the base
    // video itself" — which is what a new overlay was silently taken from. The source card asks
    // instead, so the guess and the name it was displayed under are both gone.

    public long DurationFrames => Project.DurationFrames;

    public bool HasMedia => !Project.Base.IsEmpty;

    public long Playhead
    {
        get => _playhead;
        private set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, DurationFrames - 1));
            if (_playhead == clamped) return;

            _playhead = clamped;

            // Before anything is told the playhead moved: a pending overlay starts at the
            // playhead, so the band and the composited frame both read this on the way out.
            UpdatePendingRange();

            Notify();
            Notify(nameof(TimecodeText));
            RenderCurrentFrame();
        }
    }

    public long? MarkIn { get => _markIn; private set { _markIn = value; Notify(); Notify(nameof(SelectionText)); } }

    public long? MarkOut { get => _markOut; private set { _markOut = value; Notify(); Notify(nameof(SelectionText)); } }

    /// <summary>Playback speed: 0 paused, negative for reverse.</summary>
    public int ShuttleRate
    {
        get => _shuttleRate;
        private set
        {
            _shuttleRate = value;

            // Everything that changes the transport comes through here, so this is the one
            // place the audio device has to be told about.
            SyncAudioTransport();

            Notify();
            Notify(nameof(TransportText));
            Notify(nameof(TransportGlyph));
        }
    }

    public string Status { get => _status; private set { _status = value; Notify(); } }

    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Notify(); } }

    public string TimecodeText => Format(Playhead) + "  /  " + Format(DurationFrames);

    public string TransportText => ShuttleRate switch
    {
        0 => "Paused",
        1 => "Playing",
        > 1 => $"Forward {ShuttleRate}x",
        -1 => "Reverse",
        _ => $"Reverse {-ShuttleRate}x",
    };

    /// <summary>
    /// Transport state as a glyph for the status bar.
    /// </summary>
    /// <remarks>
    /// A cross when stopped, otherwise a chevron pointing the way the playhead is moving —
    /// one per doubling, so 8x is four of them. The count is the readable part: at a glance
    /// the difference between one chevron and three is obvious in a way that "2x" and "8x"
    /// in the same small type is not.
    /// </remarks>
    public string TransportGlyph
    {
        get
        {
            if (ShuttleRate == 0) return "✕";

            var chevrons = BitOperations.Log2((uint)Math.Abs(ShuttleRate)) + 1;
            return new string(ShuttleRate > 0 ? '❯' : '❮', chevrons);
        }
    }

    /// <summary>True while the playhead is not moving, which is what colours the glyph.</summary>
    public bool IsStopped => ShuttleRate == 0;

    public string SelectionText
    {
        get
        {
            if (MarkIn is null && MarkOut is null) return "No selection";
            if (SelectedRange is { } range)
                return $"In {Format(range.Start)}  Out {Format(range.End)}  ({Format(range.Length)})";
            return MarkIn is not null ? $"In {Format(MarkIn.Value)}" : $"Out {Format(MarkOut!.Value)}";
        }
    }

    /// <summary>
    /// The marked range, defaulting the missing end to the playhead or the timeline end.
    /// </summary>
    /// <remarks>
    /// Marking only an in-point and pressing X is a common shortcut — it should delete
    /// from there to the playhead rather than refusing.
    /// </remarks>
    public FrameRange? SelectedRange
    {
        get
        {
            var start = MarkIn ?? (MarkOut is not null ? Playhead : null);
            var end = MarkOut ?? (MarkIn is not null ? Playhead : null);
            if (start is null || end is null) return null;

            var range = new FrameRange(Math.Min(start.Value, end.Value), Math.Max(start.Value, end.Value));
            return range.IsEmpty ? null : range.ClampTo(DurationFrames);
        }
    }

    public bool CanUndo => _document.CanUndo;

    public bool CanRedo => _document.CanRedo;

    public SourceIndex IndexOf(int sourceId) => _indices[sourceId];

    // ---- audio -------------------------------------------------------------------

    /// <summary>
    /// Silences the preview.
    /// </summary>
    /// <remarks>
    /// Monitoring only: it does not touch the document and does not change what an export
    /// contains. Nor does it stop the transport — the clock keeps running underneath, so
    /// pressing this mid-playback does not make the picture jump.
    /// </remarks>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;

            _isMuted = value;
            _player.Muted = value;

            Notify();
            Notify(nameof(MuteGlyph));
        }
    }

    /// <summary>Speaker or crossed-out speaker, for the transport row.</summary>
    public string MuteGlyph => IsMuted ? "🔇" : "🔊";

    /// <summary>A source's audio envelope, or null when it has none or it is still building.</summary>
    public AudioPeaks? PeaksFor(int sourceId) =>
        _peaks.TryGetValue(sourceId, out var peaks) ? peaks : null;

    /// <summary>
    /// Starts building a source's envelope, unless it is already built or under way.
    /// </summary>
    /// <remarks>
    /// Off the UI thread and without <see cref="IsBusy"/>: this decodes a whole audio track,
    /// which on a long recording is seconds, and nothing the user is doing needs to wait for
    /// it. The waveform appears when it appears. Pressing the sync key <em>does</em> wait —
    /// see <see cref="SyncOverlayAudio"/> — because at that point the answer is what was
    /// asked for.
    /// </remarks>
    private void BeginPeaks(SourceMedia source)
    {
        if (!source.HasAudio)
        {
            // Recorded as "known to have none", so the sync key can say so immediately
            // rather than waiting for a build that would find nothing.
            _peaks[source.Id] = null;
            return;
        }

        if (_peaks.ContainsKey(source.Id)) return;

        var id = source.Id;
        var path = source.Path;
        var key = source.ContentKey;
        var rate = Project.Output.SampleRate;

        _ = Task.Run(() =>
        {
            var peaks = LoadPeaks(id, path, key, rate);
            if (peaks is null) return;

            _dispatcher.BeginInvoke(() => PeaksChanged?.Invoke());
        });
    }

    /// <summary>
    /// Builds or loads one source's envelope. Safe to call from any thread.
    /// </summary>
    /// <remarks>
    /// A failure is cached as "no audio" rather than retried, so a file that cannot be
    /// decoded does not have its whole track re-attempted on every keystroke.
    /// </remarks>
    private AudioPeaks? LoadPeaks(int sourceId, string path, string contentKey, int sampleRate)
    {
        if (_peaks.TryGetValue(sourceId, out var existing) && existing is not null) return existing;

        AudioPeaks? peaks;

        try
        {
            peaks = AudioPeaksCache.GetOrBuild(path, contentKey, sampleRate);
        }
        catch (Exception e) when (e is IOException or FfmpegDecodeException or InvalidOperationException)
        {
            peaks = null;
        }

        _peaks[sourceId] = peaks;
        return peaks;
    }

    // ---- media -------------------------------------------------------------------

    public async Task OpenAsync(string path)
    {
        IsBusy = true;
        Status = $"Reading {Path.GetFileName(path)}...";

        try
        {
            var probe = await new MediaProber(_runtime).ProbeAsync(path);

            var output = new OutputFormat(probe.Media.Width, probe.Media.Height, probe.Media.FrameRate);
            var fresh = TimelineEdits.ImportSource(Project.Empty(output), probe.Media);

            _sessionKey = probe.Media.ContentKey;

            // Reopening a video silently restores the edits made to it last time. The
            // restore is pushed onto the undo stack, so Ctrl+Z discards it — which is why
            // this needs no "restore or start fresh?" prompt.
            var restored = SessionStore.TryLoad(_sessionKey);
            var usable = restored is not null && SourcesStillResolve(restored);

            _document.Changed -= OnDocumentChanged;
            _document = new EditorDocument(fresh, "Open video");
            _document.Changed += OnDocumentChanged;

            // Indices are keyed by source id and ids start again at 1 with every open, so
            // anything left from the last video is a keyframe table filed under a number
            // this project is about to reuse. The restore below only probes ids it does not
            // already have, which is exactly how a stale one gets adopted: an overlay that
            // was source 2 in the previous video would seek this video's source 2 by the
            // wrong timestamps, silently and with no error to show for it.
            //
            // Cleared here rather than beside the probe so that a probe that throws leaves
            // the video already open still working.
            _indices.Clear();
            _indices[1] = probe.Index;

            // Envelopes are keyed by source id for exactly the same reason the indices are,
            // and go stale in exactly the same way.
            _player.Stop();
            _peaks.Clear();

            DisposePump();
            _pump = CreatePump(output);

            if (usable)
            {
                foreach (var source in restored!.Sources)
                    if (!_indices.ContainsKey(source.Id))
                        _indices[source.Id] = (await new MediaProber(_runtime).ProbeAsync(source.Path)).Index;

                _document.Replace("Restore session", restored);
            }

            _playhead = 0;
            MarkIn = null;
            MarkOut = null;
            Mode = EditorMode.Normal;

            foreach (var source in _document.Current.Sources) BeginPeaks(source);

            OnDocumentChanged(_document.Current);

            Status = usable
                ? $"{Path.GetFileName(path)} — restored your previous edits (Ctrl+Z to discard)"
                : Describe(probe.Media);

            if (usable) SessionRestored?.Invoke(Path.GetFileName(path));
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FfmpegDecodeException)
        {
            Status = $"Could not open {Path.GetFileName(path)}: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Imports an additional video, available as an overlay source.
    /// </summary>
    /// <remarks>
    /// It is not appended to the base track — importing a webcam recording should not
    /// lengthen the timeline, it should give you something to put on top of it.
    /// </remarks>
    public async Task ImportAsync(string path)
    {
        if (!HasMedia)
        {
            await OpenAsync(path);
            return;
        }

        IsBusy = true;

        try
        {
            var probe = await new MediaProber(_runtime).ProbeAsync(path);

            var nextId = Project.Sources.Max(s => s.Id) + 1;
            _indices[nextId] = probe.Index;

            Apply($"Import {Path.GetFileName(path)}",
                p => TimelineEdits.ImportSource(p, probe.Media, appendToBase: false));

            BeginPeaks(Project.RequireSource(nextId));

            Status = $"{Path.GetFileName(path)} imported — press P to overlay it";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FfmpegDecodeException)
        {
            Status = $"Could not import {Path.GetFileName(path)}: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Adds a video to the end of the base track, as a new segment.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="ImportAsync"/>: importing gives you something to put
    /// on top of the timeline, appending makes it part of the timeline. Its length is
    /// converted into output frames on the way in, so joining a 60 fps capture onto a
    /// 30 fps recording cannot drift; its picture is scaled to the project's frame, by the
    /// preview and the export alike.
    /// </remarks>
    public async Task AppendAsync(string path)
    {
        if (!HasMedia)
        {
            await OpenAsync(path);
            return;
        }

        IsBusy = true;
        Status = $"Reading {Path.GetFileName(path)}...";

        try
        {
            var probe = await new MediaProber(_runtime).ProbeAsync(path);

            var nextId = Project.Sources.Max(s => s.Id) + 1;
            _indices[nextId] = probe.Index;

            var join = DurationFrames;

            Apply($"Add {Path.GetFileName(path)}",
                p => TimelineEdits.ImportSource(p, probe.Media, appendToBase: true));

            BeginPeaks(Project.RequireSource(nextId));

            if (DurationFrames == join)
            {
                Status = $"{Path.GetFileName(path)} has no frames to add.";
                return;
            }

            // Land on the join: that is the frame the user wants to look at, and the one
            // they are most likely to trim from.
            _playhead = join;
            MarkIn = null;
            MarkOut = null;

            Notify(nameof(Playhead));
            Notify(nameof(TimecodeText));
            RenderCurrentFrame();

            var stretched = probe.Media.Width * Project.Output.Height
                            != Project.Output.Width * probe.Media.Height
                ? $" — {probe.Media.Width}x{probe.Media.Height} stretched to the project's " +
                  $"{Project.Output.Width}x{Project.Output.Height} frame"
                : "";

            Status = $"{Path.GetFileName(path)} added to the end{stretched} — Ctrl+Z to undo";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FfmpegDecodeException)
        {
            Status = $"Could not add {Path.GetFileName(path)}: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Throws away every edit and returns to the video as it was opened.
    /// </summary>
    /// <remarks>
    /// Goes through the undo stack like any other edit, so the confirmation prompt in front
    /// of it is about intent rather than about anything being unrecoverable — Ctrl+Z brings
    /// the whole edit back.
    /// </remarks>
    public void ResetAll()
    {
        if (!HasMedia)
        {
            Status = "Nothing to reset.";
            return;
        }

        // The first source is the video the session is keyed to; everything else was
        // imported or appended on top of it and goes away with the edits.
        var original = Project.Sources[0];
        var output = new OutputFormat(
            original.Width, original.Height, original.FrameRate, Project.Output.SampleRate);

        var fresh = TimelineEdits.ImportSource(Project.Empty(output), original);

        // ImportSource renumbers from scratch, so the index has to follow the new id — and
        // so does the envelope, for the same reason: a stale one filed under a number this
        // project is about to reuse would draw the wrong waveform and sync against it.
        var id = fresh.Sources[0].Id;
        if (!_indices.ContainsKey(id)) _indices[id] = _indices[original.Id];

        var keptPeaks = PeaksFor(original.Id);
        _player.Stop();
        _peaks.Clear();
        if (keptPeaks is not null) _peaks[id] = keptPeaks;

        _document.Replace("Reset everything", fresh);

        _playhead = 0;
        MarkIn = null;
        MarkOut = null;
        Mode = EditorMode.Normal;
        Notify(nameof(Playhead));
        Notify(nameof(TimecodeText));

        // Decoders are cached per source, and the sources that were dropped here will
        // never be asked for again.
        _pump?.Reset();
        RenderCurrentFrame();

        Status = "Reset to the original video — Ctrl+Z to undo";
    }

    /// <summary>
    /// Empties the editor and lets go of every file it had open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A different thing from <see cref="ResetAll"/>, which is an edit: that one goes on the
    /// undo stack and leaves the video open in front of you. This ends the session. The
    /// document, the probe indices, the decoders and the handles behind them all go, and
    /// the window is back to where it was before anything was opened — so there is nothing
    /// left to undo it in, and the prompt in front of this one is doing real work.
    /// </para>
    /// <para>
    /// Disposing the preview is the part that actually releases the files. It keeps a
    /// decoder per source and each of those holds its video open for as long as it lives,
    /// which is what stops the user renaming or deleting a file they have finished with.
    /// Dropping the indices matters as much: they are keyed by a source id that the next
    /// video will reuse from 1, so a stale one would quietly hand the new project the old
    /// video's keyframe table.
    /// </para>
    /// <para>
    /// The session is flushed on the way out rather than thrown away. Edits autosave
    /// against the video's content key and that is this app's entire persistence story;
    /// closing a video says you are done looking at it, not that the afternoon's cuts
    /// should evaporate. Open the same file again and they come back.
    /// </para>
    /// </remarks>
    public void CloseAll()
    {
        // Before anything else: SaveSession gives up the moment the project is empty, so a
        // flush after the teardown would silently write nothing.
        FlushSession();

        // Stop the transport before the decoders go, so no tick is mid-render when they do,
        // and leave placement mode so the rectangle editor lets go of the mouse. The audio
        // player holds decoders of its own on a device thread, and stopping it joins that
        // thread — without which the file would stay open after the editor said it was closed.
        ShuttleRate = 0;
        _player.Stop();
        _clock.Reset();
        Mode = EditorMode.Normal;

        // So the rebuild below is free to drop the selections: a drag left open would tell it
        // the index still means something, against a document that no longer exists.
        EndOverlayDrag();
        EndSegmentDrag();

        DisposePump();

        _peaks.Clear();

        _indices.Clear();
        _sessionKey = null;

        _document.Changed -= OnDocumentChanged;
        _document = new EditorDocument(EmptyProject());
        _document.Changed += OnDocumentChanged;

        _playhead = 0;
        MarkIn = null;
        MarkOut = null;

        // The field rather than the property: the setter renders, and there is nothing left
        // to render with.
        _pendingRect = default;
        PendingRange = default;
        _pendingContent = null;

        // Rebuilds the resolver and tells everything bound to this that the project is
        // empty — which is also what puts the timeline and the placement layer back.
        OnDocumentChanged(_document.Current);

        // That last call armed the autosave, as every document change does. There is
        // nothing to save and no key to save it under, so disarm it rather than leave a
        // timer to fire into a null session key three quarters of a second from now.
        _autosave.Stop();
        Notify(nameof(Playhead));

        Status = "Closed — press Ctrl+O to open a video.";
    }

    /// <summary>
    /// The project an editor with nothing open holds.
    /// </summary>
    /// <remarks>
    /// 720p30 is a placeholder and is replaced wholesale by the first video's own format
    /// the moment one is opened. It exists so that an empty editor still has a frame rate
    /// to format a timecode against.
    /// </remarks>
    private static Project EmptyProject() =>
        Project.Empty(new OutputFormat(1280, 720, Rational.FromInt(30)));

    private static string Describe(SourceMedia media)
    {
        var vfr = media.IsVariableFrameRate ? "  ⚠ variable frame rate" : "";
        return $"{Path.GetFileName(media.Path)} — {media.Width}x{media.Height} " +
               $"{media.FrameRate.Approx:0.##} fps, {media.FrameCount} frames{vfr}";
    }

    private bool SourcesStillResolve(Project project) =>
        project.Sources.All(s => File.Exists(s.Path));

    // ---- intents -----------------------------------------------------------------

    public void Dispatch(EditorIntent intent)
    {
        switch (intent)
        {
            case EditorIntent.PlayPause: ShuttleRate = ShuttleRate == 0 ? 1 : 0; RestartClock(); break;
            case EditorIntent.Stop: ShuttleRate = 0; break;
            case EditorIntent.ShuttleForward: ShuttleRate = ShuttleRate < 1 ? 1 : Math.Min(8, ShuttleRate * 2); RestartClock(); break;
            case EditorIntent.ShuttleReverse: ShuttleRate = ShuttleRate > -1 ? -1 : Math.Max(-8, ShuttleRate * 2); RestartClock(); break;

            case EditorIntent.StepBack: ShuttleRate = 0; Playhead--; break;
            case EditorIntent.StepForward: ShuttleRate = 0; Playhead++; break;
            case EditorIntent.StepBackSecond: ShuttleRate = 0; Playhead -= FramesPerSecond; break;
            case EditorIntent.StepForwardSecond: ShuttleRate = 0; Playhead += FramesPerSecond; break;
            case EditorIntent.GoToStart: ShuttleRate = 0; Playhead = 0; break;
            case EditorIntent.GoToEnd: ShuttleRate = 0; Playhead = DurationFrames - 1; break;
            case EditorIntent.PreviousEdit: ShuttleRate = 0; Playhead = AdjacentBoundary(-1); break;
            case EditorIntent.NextEdit: ShuttleRate = 0; Playhead = AdjacentBoundary(1); break;

            case EditorIntent.MarkIn: MarkIn = Playhead; break;
            case EditorIntent.MarkOut: MarkOut = Playhead; break;
            // Escape means "never mind", so it lets go of whatever is picked out on the
            // strip as well as of the marks. Having to learn which of the editor's
            // selections a key drops would be a distinction without a difference.
            case EditorIntent.ClearMarks:
                MarkIn = null;
                MarkOut = null;
                ClearOverlaySelection();
                ClearSegmentSelection();
                break;

            case EditorIntent.RippleDelete: RippleDelete(); break;
            case EditorIntent.SplitAtPlayhead: Apply($"Split at {Format(Playhead)}", p => TimelineEdits.SplitAt(p, Playhead)); break;
            case EditorIntent.ClearCropAtPlayhead: ClearCropAtPlayhead(); break;
            case EditorIntent.RemoveOverlayAtPlayhead: RemoveOverlay(); break;
            case EditorIntent.DeleteSelection: DeleteSelection(); break;

            case EditorIntent.BeginCrop: BeginCrop(); break;
            case EditorIntent.BeginOverlay: BeginOverlay(); break;
            case EditorIntent.Commit: CommitPlacement(); break;
            case EditorIntent.Cancel: CancelPlacement(); break;

            case EditorIntent.ChooseOverlayMarkedRange: ChooseOverlayMarkedRange(); break;
            case EditorIntent.ChooseOverlaySegment: ChooseOverlaySegment(); break;

            case EditorIntent.NudgeLeft: Nudge(-NudgeStep, 0); break;
            case EditorIntent.NudgeRight: Nudge(NudgeStep, 0); break;
            case EditorIntent.NudgeUp: Nudge(0, -NudgeStep); break;
            case EditorIntent.NudgeDown: Nudge(0, NudgeStep); break;
            case EditorIntent.Grow: Resize(1.1); break;
            case EditorIntent.Shrink: Resize(1 / 1.1); break;

            case EditorIntent.SnapTopLeft: SnapTo(Anchor.TopLeft); break;
            case EditorIntent.SnapTopRight: SnapTo(Anchor.TopRight); break;
            case EditorIntent.SnapBottomLeft: SnapTo(Anchor.BottomLeft); break;
            case EditorIntent.SnapBottomRight: SnapTo(Anchor.BottomRight); break;
            case EditorIntent.SnapCenter: SnapTo(Anchor.Centre); break;

            case EditorIntent.SyncOverlayAudio: SyncOverlayAudio(); break;

            case EditorIntent.ToggleMute: IsMuted = !IsMuted; Status = IsMuted ? "Muted" : "Unmuted"; break;

            case EditorIntent.ToggleOverlayMute: ToggleOverlayMute(); break;

            case EditorIntent.Undo: if (_document.Undo()) Status = "Undone"; break;
            case EditorIntent.Redo: if (_document.Redo()) Status = "Redone"; break;
        }
    }

    /// <summary>Pixels a single arrow press moves the pending rectangle.</summary>
    private const int NudgeStep = 8;

    // ---- placing a crop or an overlay ---------------------------------------------

    private void BeginCrop()
    {
        if (!HasMedia)
        {
            Status = "Press Ctrl+O to open a video.";
            return;
        }

        // With nothing marked, the crop covers the whole video. Reframing an entire
        // recording is common enough that it should not first require marking it end to
        // end, and unlike a ripple delete there is nothing destructive about the default.
        var selection = SelectedRange;
        var range = selection ?? new FrameRange(0, DurationFrames);

        if (range.IsEmpty)
        {
            Status = "Nothing to crop.";
            return;
        }

        ShuttleRate = 0;
        PendingRange = range;

        // Locked to the output ratio, so zoom-to-fill never has to letterbox.
        var existing = Project.Crops.FirstOrDefault(c => c.Range.Overlaps(range));
        PendingRect = existing.Rect.W > 0
            ? existing.Rect
            : RectPlacement.Initial(
                Project.Output.Width, Project.Output.Height,
                Project.Output.Width, Project.Output.Height,
                fraction: 0.6, Anchor.Centre);

        Mode = EditorMode.Crop;
        Status = (selection is null ? "Cropping the whole video · " : "")
                 + "Drag the box or its corners · arrows move · Shift+↑/↓ resize · "
                 + "1-5 snap · Enter applies · Esc cancels";
    }

    /// <summary>Asks what to overlay, before asking where it goes.</summary>
    /// <remarks>
    /// One keypress used to place a clip outright, taking its content from whichever file had
    /// been imported most recently and its length from however much of that file was left.
    /// Neither was ever stated, so the only way to find out what you were going to get was to
    /// press the key and look at what arrived. The card states both.
    /// </remarks>
    private void BeginOverlay()
    {
        if (!HasMedia)
        {
            Status = "Press Ctrl+O to open a video.";
            return;
        }

        ShuttleRate = 0;
        Mode = EditorMode.OverlaySource;
        Status = "Overlay what? · 1 the marked range · 2 the selected segment · "
                 + "3 a video file · Esc cancels";
    }

    /// <summary>The three ways of saying what a new overlay shows.</summary>
    /// <remarks>
    /// Each resolves to the same three numbers — which source, from which of its frames, for
    /// how long — after which the kind is forgotten. See <see cref="OverlayPlacement"/>, which
    /// holds the arithmetic so that it can be tested without a window.
    /// </remarks>
    private void ChooseOverlayMarkedRange()
    {
        if (SelectedRange is not { } range)
        {
            Status = "Nothing marked — press I and O to mark a range first.";
            return;
        }

        if (OverlayPlacement.FromTimelineRange(Project, range) is not { } content)
        {
            Status = "Nothing to overlay in the marked range.";
            return;
        }

        // Capped at the cut it starts in: one clip reads one contiguous run of one source.
        var clipped = content.LengthFrames < range.Length
            ? $" (the marked range crosses a cut — taking its first {content.LengthFrames} frames)"
            : "";

        EnterOverlayPlacement(content, $"the marked range{clipped}");
    }

    private void ChooseOverlaySegment()
    {
        if (SelectedSegment is not { } index)
        {
            Status = "No segment selected — click one on the track first.";
            return;
        }

        if (OverlayPlacement.FromSegment(Project, index) is not { } content)
        {
            Status = "Nothing to overlay in that segment.";
            return;
        }

        EnterOverlayPlacement(content, $"segment {index + 1}");
    }

    /// <summary>
    /// Brings a file in and takes it straight into placement, whole.
    /// </summary>
    /// <remarks>
    /// The card's third row. Split from the file dialog that normally supplies the path so a
    /// scripted run can drive everything except the picker itself — the same seam
    /// <c>ImportAsync</c> already has, and for the same reason: a common dialog belongs to the
    /// desktop and would appear on the user's screen wherever the window was parked.
    /// </remarks>
    public async Task ImportAndOverlayAsync(string path)
    {
        var before = Project.Sources.Length;

        await ImportAsync(path);

        // A failed import has already said so in the status; saying it again in other words
        // would only cover that up.
        if (Project.Sources.Length == before) return;

        ChooseOverlayFile(Project.Sources[^1].Id);
    }

    /// <summary>Overlays an already-imported file, whole.</summary>
    public void ChooseOverlayFile(int sourceId)
    {
        if (OverlayPlacement.FromWholeSource(Project, sourceId) is not { } content)
        {
            Status = "That file has no frames to overlay.";
            return;
        }

        EnterOverlayPlacement(content, Path.GetFileName(Project.RequireSource(sourceId).Path));
    }

    /// <summary>
    /// Takes a chosen clip into placement, where the playhead decides where it goes.
    /// </summary>
    private void EnterOverlayPlacement(OverlayContent content, string what)
    {
        var source = Project.RequireSource(content.SourceId);

        ShuttleRate = 0;
        _pendingContent = content;

        // Not through UpdatePendingRange, which is guarded on the mode this is about to enter.
        PendingRange = OverlayPlacement.RangeAt(Project, content, Playhead);

        if (PendingRange.IsEmpty)
        {
            _pendingContent = null;
            Mode = EditorMode.Normal;
            Status = "No room for it here — there is already an overlay across this stretch.";
            return;
        }

        // Before the rectangle, whose setter renders: PreviewResolver only composites the
        // pending clip in this mode, so setting it afterwards would leave the first frame of
        // the placement showing no overlay at all — and nothing else would redraw until the
        // user moved something.
        Mode = EditorMode.Overlay;

        // Locked to the overlay source's own ratio, so its picture is never stretched.
        PendingRect = RectPlacement.Initial(
            Project.Output.Width, Project.Output.Height,
            source.Width, source.Height,
            fraction: 0.3, Anchor.BottomRight);

        PendingRect = RectPlacement.Snap(
            PendingRect, Project.Output.Width, Project.Output.Height, Anchor.BottomRight,
            margin: Project.Output.Width / 50);

        Status = $"Placing {what} · move the playhead to aim it · A syncs it by sound · "
                 + "arrows move the box · Enter places · Esc cancels";
    }

    /// <summary>
    /// Works out where the pending clip would land, from the playhead it follows.
    /// </summary>
    /// <remarks>
    /// Called from the <see cref="Playhead"/> setter, so that moving the playhead by any
    /// means — a key, the ruler, an audio sync — carries the clip with it, and from
    /// <see cref="OnDocumentChanged"/>, because an undo can shorten the timeline out from
    /// under a clip that is still being positioned.
    /// </remarks>
    private void UpdatePendingRange()
    {
        if (Mode != EditorMode.Overlay || _pendingContent is not { } content) return;

        PendingRange = OverlayPlacement.RangeAt(Project, content, Playhead);
    }

    private void CommitPlacement()
    {
        switch (Mode)
        {
            case EditorMode.Crop:
                var range = PendingRange;
                var rect = PendingRect;
                Apply($"Crop {rect.W}x{rect.H}", p => TimelineEdits.SetCrop(p, range, rect));

                // The crop is the range the marks named, so they have been spent. An overlay
                // never reads them, and dropping marks it did not use would lose work the
                // user is still holding.
                MarkIn = null;
                MarkOut = null;
                break;

            case EditorMode.Overlay:
                var clip = new OverlayClip(
                    PendingRange, PendingOverlaySourceId, PendingOverlaySourceStart, PendingRect);
                Apply("Place overlay", p => TimelineEdits.AddOverlay(p, clip));

                // Marks are not spent. A crop *is* the range they named, but an overlay
                // borrowed them to say what to show and left where they are alone — and
                // dropping them would lose a range the user may still be about to cut.
                break;

            default:
                return;
        }

        _pendingContent = null;
        Mode = EditorMode.Normal;
        RenderCurrentFrame();
    }

    /// <summary>Backs out of an overlay, from either half of it.</summary>
    /// <remarks>
    /// Not <see cref="IsPlacing"/>: Escape has to put the source card away too, and that is
    /// the one mode where there is no rectangle in play.
    /// </remarks>
    private void CancelPlacement()
    {
        if (Mode == EditorMode.Normal) return;

        _pendingContent = null;
        Mode = EditorMode.Normal;
        Status = "Cancelled";
        RenderCurrentFrame();
    }

    /// <summary>Positions the pending rectangle from a mouse drag, in output-space pixels.</summary>
    public void SetPendingRect(RectI rect)
    {
        if (!IsPlacing) return;

        PendingRect = RectPlacement.Clamp(rect, Project.Output.Width, Project.Output.Height);
    }

    /// <summary>Builds an aspect-locked rectangle from a freehand drag.</summary>
    public void DragPendingRect(int x0, int y0, int x1, int y1)
    {
        if (!IsPlacing) return;

        var (aspectW, aspectH) = PendingAspect();
        PendingRect = RectPlacement.FromDrag(
            x0, y0, x1, y1, aspectW, aspectH, Project.Output.Width, Project.Output.Height);
    }

    /// <summary>Resizes the pending rectangle from a dragged corner handle.</summary>
    public void ResizePendingRect(int anchorX, int anchorY, int x, int y)
    {
        if (!IsPlacing) return;

        var (aspectW, aspectH) = PendingAspect();
        PendingRect = RectPlacement.FromCorner(
            anchorX, anchorY, x, y, aspectW, aspectH, Project.Output.Width, Project.Output.Height);
    }

    private (int Width, int Height) PendingAspect() =>
        Mode == EditorMode.Crop
            ? (Project.Output.Width, Project.Output.Height)
            : Project.FindSource(PendingOverlaySourceId) is { } source
                ? (source.Width, source.Height)
                : (Project.Output.Width, Project.Output.Height);

    private void Nudge(int dx, int dy)
    {
        if (!IsPlacing) return;
        PendingRect = RectPlacement.Move(PendingRect, dx, dy, Project.Output.Width, Project.Output.Height);
    }

    private void Resize(double factor)
    {
        if (!IsPlacing) return;
        PendingRect = RectPlacement.Resize(PendingRect, factor, Project.Output.Width, Project.Output.Height);
    }

    private void SnapTo(Anchor anchor)
    {
        if (!IsPlacing) return;

        var margin = anchor == Anchor.Centre ? 0 : Project.Output.Width / 50;
        PendingRect = RectPlacement.Snap(
            PendingRect, Project.Output.Width, Project.Output.Height, anchor, margin);
    }

    /// <summary>
    /// Lines an overlay up with the base track by its sound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case it exists for is one recording holding the same event from two angles: take
    /// the second angle as the overlay, put it over the first, and this finds the alignment
    /// by correlating what the two of them heard.
    /// </para>
    /// <para>
    /// <b>Which of the two things moves depends on which is still free.</b> While a clip is
    /// being placed its content is what the user just chose and must not change, so what moves
    /// is the clip — along the timeline, by way of the playhead it follows. Once it is
    /// committed the reverse is true: the clip is where they put it, and the free variable is
    /// which of its source's frames it reads. Both directions are
    /// <see cref="OverlaySync"/>'s, and both refuse the identity match the same way.
    /// </para>
    /// <para>
    /// Runs off the UI thread — it may have to decode a whole audio track to build an
    /// envelope — and reports through <see cref="IsBusy"/>, which is also how a harness run
    /// knows to wait for it. Unlike the background build that starts on import, this one
    /// waits: the user pressed a key to get an answer.
    /// </para>
    /// <para>
    /// A low-confidence result changes nothing. Silently jumping an overlay to the wrong
    /// place is worse than saying so, because the user's next move is to check the alignment
    /// by eye either way — and if it moved, they have lost where it was.
    /// </para>
    /// </remarks>
    private void SyncOverlayAudio()
    {
        if (Mode == EditorMode.Overlay) SyncPendingOverlay();
        else SyncCommittedOverlay();
    }

    /// <summary>Moves the clip being placed to where its sound fits the base track.</summary>
    private void SyncPendingOverlay()
    {
        if (_pendingContent is not { } content) return;

        if (PendingRange.IsEmpty)
        {
            Status = "Nothing to sync.";
            return;
        }

        ShuttleRate = 0;
        IsBusy = true;
        Status = "Matching the audio...";

        var project = Project;
        var indices = new Dictionary<int, SourceIndex>(_indices);
        var rate = project.Output.SampleRate;
        var from = PendingRange.Start;

        _ = Task.Run(() =>
        {
            foreach (var source in project.Sources)
                LoadPeaks(source.Id, source.Path, source.ContentKey, rate);

            return OverlaySync.SolveTimelinePosition(
                project, content.SourceId, content.SourceStartFrame, content.LengthFrames,
                from, id => indices[id], PeaksFor);
        })
        .ContinueWith(
            task => ApplyPendingSync(task, from),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Slides a placed clip's content until it matches the base track under it.</summary>
    private void SyncCommittedOverlay()
    {
        var range = default(FrameRange);
        var sourceId = 0;
        var current = 0L;
        var index = -1;

        for (var i = 0; i < Project.Overlays.Length; i++)
            if (Project.Overlays[i].Range.Contains(Playhead))
            {
                index = i;
                range = Project.Overlays[i].Range;
                sourceId = Project.Overlays[i].SourceId;
                current = Project.Overlays[i].SourceStartFrame;
                break;
            }

        if (index < 0)
        {
            Status = "No overlay under the playhead to sync.";
            return;
        }

        if (range.IsEmpty)
        {
            Status = "Nothing to sync.";
            return;
        }

        ShuttleRate = 0;
        IsBusy = true;
        Status = "Matching the audio...";

        // Snapshotted because the background task reads them: Project is immutable and safe
        // to hand across, and the index table is not written again until the next open.
        var project = Project;
        var indices = new Dictionary<int, SourceIndex>(_indices);
        var rate = project.Output.SampleRate;

        _ = Task.Run(() =>
        {
            foreach (var source in project.Sources)
                LoadPeaks(source.Id, source.Path, source.ContentKey, rate);

            return OverlaySync.Solve(
                project, range, sourceId, current,
                id => indices[id],
                PeaksFor);
        })
        .ContinueWith(
            task => ApplySync(task, index, current),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Puts the clip being placed where the correlation said it belongs.</summary>
    /// <remarks>
    /// Through the playhead rather than by writing <see cref="PendingRange"/>, which is derived
    /// from it — a direct write would be undone by the next keystroke that moved the playhead.
    /// Taking the playhead along also leaves the user looking at the frame the match was made
    /// against, which is the one they want to check.
    /// </remarks>
    private void ApplyPendingSync(Task<TimelineSyncOutcome> task, long from)
    {
        IsBusy = false;

        if (task.IsFaulted)
        {
            Status = $"Could not match the audio: {task.Exception?.GetBaseException().Message}";
            return;
        }

        var outcome = task.Result;

        if (!outcome.Succeeded)
        {
            Status = Describe(outcome.Failure, placing: true);
            return;
        }

        Playhead = outcome.TimelineStartFrame;

        // Reported from where it actually landed: the clamp may have stopped it against a
        // clip or the end of the timeline on the way.
        var moved = PendingRange.Start - from;
        Status = Summarise(moved, outcome.Confidence, outcome.Runner);
    }

    private void ApplySync(Task<SyncOutcome> task, int index, long current)
    {
        IsBusy = false;

        if (task.IsFaulted)
        {
            Status = $"Could not match the audio: {task.Exception?.GetBaseException().Message}";
            return;
        }

        var outcome = task.Result;

        if (!outcome.Succeeded)
        {
            Status = Describe(outcome.Failure, placing: false);
            return;
        }

        var target = outcome.SourceStartFrame;
        var at = index;
        Apply("Sync overlay", p => TimelineEdits.SetOverlaySourceStart(p, at, target));

        Status = Summarise(target - current, outcome.Confidence, outcome.Runner);
    }

    /// <summary>Why a sync produced nothing, in the words of whichever half asked.</summary>
    private static string Describe(SyncFailure failure, bool placing) => failure switch
    {
        SyncFailure.NoAudio => "Sync needs sound: one of these clips has no audio track.",
        SyncFailure.NotAnalysed => "Still analysing the audio — try again in a moment.",
        SyncFailure.RangeTooShort =>
            $"Too short to match — an overlay needs at least "
            + $"{OverlaySync.MinimumReferenceSeconds:0.##} seconds of sound to be recognised.",
        SyncFailure.MatchNotOnTimeline =>
            "Found the matching sound, but that part of the base track was cut out — "
            + "there is nowhere on the timeline to put it.",
        _ => placing
            ? "No confident audio match — move the playhead to place it by eye."
            : "No confident audio match — drag the clip's edges on the strip to sync by hand.",
    };

    /// <summary>How far it moved, and how much the answer is to be trusted.</summary>
    /// <remarks>
    /// The runner-up matters as much as the winner: two offsets that score alike mean the
    /// recording says the same thing twice, and the user should look before trusting it.
    /// </remarks>
    private string Summarise(long moved, double confidence, double runner)
    {
        var milliseconds = moved / Project.Output.FrameRate.Approx * 1000;

        var decisive = confidence - runner > 0.15
            ? ""
            : " — but another position matches nearly as well, so check it";

        return moved == 0
            ? $"Already in sync (confidence {confidence:0.00}){decisive}"
            : $"Synced — moved {Math.Abs(moved)} frames {(moved > 0 ? "later" : "earlier")} " +
              $"({Math.Abs(milliseconds):0} ms), confidence {confidence:0.00}{decisive}";
    }

    private void ToggleOverlayMute()
    {
        for (var i = 0; i < Project.Overlays.Length; i++)
            if (Project.Overlays[i].Range.Contains(Playhead))
            {
                var index = i;
                var muted = !Project.Overlays[i].Muted;
                Apply(muted ? "Mute overlay" : "Unmute overlay", p => TimelineEdits.ToggleOverlayMute(p, index));
                return;
            }

        Status = "No overlay under the playhead.";
    }

    // ---- selecting, moving and trimming an overlay ----------------------------------

    private int? _selectedOverlay;
    private int? _selectedSegment;
    private bool _draggingOverlay;
    private bool _draggingSegment;
    private OverlayGrip _grip;
    private long _grabOffset;
    private int _dragGeneration;

    /// <summary>
    /// The overlay picked out on the timeline, as an index into <see cref="Project"/>'s list.
    /// </summary>
    /// <remarks>
    /// An index rather than the clip itself, because the clip is a value: every edit rebuilds
    /// the list, so anything holding a copy would be pointing at a state of the document that
    /// no longer exists. The index is only meaningful against the current project, which is
    /// why <see cref="OnDocumentChanged"/> drops it whenever one edit lands — bar the drag
    /// that is doing the editing, which knows its own clip stayed where it was in the list.
    /// </remarks>
    public int? SelectedOverlay
    {
        get => _selectedOverlay;
        private set
        {
            if (_selectedOverlay == value) return;

            _selectedOverlay = value;
            Notify();

            // One selection at a time. Two things lit up at once and a delete key that has to
            // guess between them is worse than either.
            if (value is not null) SelectedSegment = null;
        }
    }

    /// <summary>The base segment picked out on the timeline, as an index into the track.</summary>
    /// <remarks>An index, and cleared on every edit, for the same reasons as the overlay's.</remarks>
    public int? SelectedSegment
    {
        get => _selectedSegment;
        private set
        {
            if (_selectedSegment == value) return;

            _selectedSegment = value;
            Notify();

            if (value is not null) SelectedOverlay = null;
        }
    }

    /// <summary>The selected clip, or null when nothing is selected.</summary>
    public OverlayClip? SelectedOverlayClip =>
        SelectedOverlay is { } index && index < Project.Overlays.Length ? Project.Overlays[index] : null;

    public void ClearOverlaySelection() => SelectedOverlay = null;

    /// <summary>
    /// Selects the overlay under <paramref name="frame"/> and begins dragging it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Selecting and dragging are one call because they are one gesture: a click that selects
    /// is a drag that went nowhere, and there is nothing for a separate "select" to do that
    /// pressing and releasing without moving does not already do.
    /// </para>
    /// <para>
    /// The grab point is remembered as an offset into the clip, so the clip moves with the
    /// pointer instead of snapping its start under it — grabbing the middle of an overlay and
    /// having it jump half its length left is the classic way this gesture is got wrong.
    /// </para>
    /// </remarks>
    /// <param name="index">The clip the strip's hit test landed on.</param>
    /// <param name="frame">Where along the timeline it was taken hold of.</param>
    /// <param name="grip">Which part of the clip that was.</param>
    /// <remarks>
    /// The index comes from the caller rather than being looked up from the frame, because
    /// the two disagree at an edge: ranges are half-open, so the last pixel column of a clip
    /// belongs to a frame the clip does not contain — and that column is exactly where the
    /// out-point is grabbed. One hit test, in the control that owns the pixels.
    /// </remarks>
    /// <returns>True when a drag started, false when there is nothing there to drag.</returns>
    public bool BeginOverlayDrag(int index, long frame, OverlayGrip grip)
    {
        // Not while a crop or overlay is being placed: the pending rectangle owns the
        // document's next edit, and the strip's job then is scrubbing to see it in context.
        if (IsPlacing || index < 0 || index >= Project.Overlays.Length) return false;

        ShuttleRate = 0;

        var clip = Project.Overlays[index];

        SelectedOverlay = index;
        _draggingOverlay = true;
        _grip = grip;
        _dragGeneration++;

        // Measured against whichever part is being pulled, so all three gestures are the
        // same arithmetic: the thing you grabbed ends up under the pointer.
        _grabOffset = frame - (grip == OverlayGrip.End ? clip.Range.End : clip.Range.Start);

        Status = DescribeOverlay(clip) + " · drag to move it, drag an end to trim it";
        return true;
    }

    /// <summary>Moves or trims the overlay being dragged, so the grabbed part follows the pointer.</summary>
    /// <remarks>
    /// Every move is a real edit, coalesced onto one undo step by the gesture id: the strip
    /// draws from the document, and a drag that only previewed itself would need a second
    /// source of truth for where the clip is.
    /// </remarks>
    public void DragOverlayTo(long frame)
    {
        if (!_draggingOverlay || SelectedOverlay is not { } index) return;
        if (index >= Project.Overlays.Length) return;

        var target = frame - _grabOffset;
        var gesture = $"overlay-drag-{_dragGeneration}";

        switch (_grip)
        {
            case OverlayGrip.Start:
                Apply("Trim overlay", p => TimelineEdits.TrimOverlayStart(p, index, target), gesture);
                break;

            case OverlayGrip.End:
                Apply("Trim overlay", p => TimelineEdits.TrimOverlayEnd(p, index, target), gesture);
                break;

            default:
                Apply("Move overlay", p => TimelineEdits.SetOverlayStart(p, index, target), gesture);
                break;
        }

        Status = DescribeOverlay(Project.Overlays[index]);
    }

    /// <summary>Ends the drag, so the next edit starts a fresh undo step.</summary>
    public void EndOverlayDrag()
    {
        if (!_draggingOverlay) return;

        _draggingOverlay = false;
        _document.EndGesture();
    }

    // ---- selecting, reordering and removing a base segment --------------------------

    /// <summary>Picks out one piece of the base track.</summary>
    public void SelectSegment(int index)
    {
        if (IsPlacing || index < 0 || index >= Project.Base.Length) return;

        SelectedSegment = index;
        Status = DescribeSegment(index) + " · drag it to move it in the running order";
    }

    public void ClearSegmentSelection() => SelectedSegment = null;

    /// <summary>Begins reordering the selected segment.</summary>
    /// <remarks>
    /// Called once the pointer has actually travelled, not on the press — see the drag
    /// threshold in <see cref="TimelineControl"/>. A click on the base track has always
    /// moved the playhead and still does, and it must not also start rearranging the film.
    /// </remarks>
    public bool BeginSegmentReorder()
    {
        if (IsPlacing || SelectedSegment is null || Project.Base.Length < 2) return false;

        ShuttleRate = 0;
        _draggingSegment = true;
        _dragGeneration++;
        return true;
    }

    /// <summary>
    /// Walks the dragged segment towards the pointer, a place at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It moves only once the pointer is past the <em>middle</em> of the neighbour it is
    /// invading. Swapping as soon as the pointer entered the neighbour would make two
    /// segments of different lengths trade places back and forth under a pointer that had
    /// stopped moving: the swap puts the other one under the pointer, which asks for the
    /// swap again.
    /// </para>
    /// <para>
    /// A loop rather than a single step, because the pointer can arrive several places away
    /// — a fast drag delivers few moves, and a scripted one delivers exactly as many as it
    /// was told to.
    /// </para>
    /// </remarks>
    public void DragSegmentTo(long frame)
    {
        if (!_draggingSegment || SelectedSegment is not { } index) return;

        while (true)
        {
            var track = Project.Base;
            if (index >= track.Length) return;

            int destination;

            if (index + 1 < track.Length && frame >= Middle(track[index + 1])) destination = index + 1;
            else if (index > 0 && frame < Middle(track[index - 1])) destination = index - 1;
            else break;

            var from = index;
            var to = destination;

            Apply("Move segment", p => TimelineEdits.MoveSegment(p, from, to), $"segment-drag-{_dragGeneration}");

            // The document is what says where the segment is now, and the selection has to
            // follow it or the next step would move somebody else.
            index = destination;
            SelectedSegment = index;
        }

        Status = DescribeSegment(index);
    }

    public void EndSegmentDrag()
    {
        if (!_draggingSegment) return;

        _draggingSegment = false;
        _document.EndGesture();
    }

    private static long Middle(BaseSegment segment) => segment.TimelineStart + (segment.LengthFrames / 2);

    /// <summary>
    /// Removes whatever is picked out: a segment ripples away, an overlay comes off the top.
    /// </summary>
    /// <remarks>
    /// With nothing selected this falls back to the overlay under the playhead, which is what
    /// the delete key did before there was anything to select. It deliberately does not fall
    /// back to the <em>segment</em> under the playhead: there is always one of those, so the
    /// key would never be a no-op, and an unaimed press would ripple away whatever the
    /// playhead happened to be parked on.
    /// </remarks>
    private void DeleteSelection()
    {
        if (SelectedSegment is { } segment && segment < Project.Base.Length)
        {
            var name = DescribeSegment(segment);

            Apply("Remove segment", p => TimelineEdits.RemoveSegment(p, segment));

            // The playhead follows the cut, as it does after a ripple delete — it is where
            // the join now is, and where the next question is.
            Playhead = Math.Min(Playhead, Math.Max(0, DurationFrames - 1));

            Status = $"Removed {name} — Ctrl+Z to undo";
            return;
        }

        RemoveOverlay();
    }

    private string DescribeSegment(int index)
    {
        var segment = Project.Base[index];

        var name = Project.FindSource(segment.SourceId) is { } source
            ? Path.GetFileName(source.Path)
            : "segment";

        return $"{name} · {Format(segment.Timeline.Start)} to {Format(segment.Timeline.End)} · " +
               $"segment {index + 1} of {Project.Base.Length}";
    }

    private int? IndexOfOverlayAt(long frame)
    {
        for (var i = 0; i < Project.Overlays.Length; i++)
            if (Project.Overlays[i].Range.Contains(frame))
                return i;

        return null;
    }

    private string DescribeOverlay(OverlayClip clip)
    {
        var name = Project.FindSource(clip.SourceId) is { } source
            ? Path.GetFileName(source.Path)
            : "overlay";

        // The length as well as the two ends, because during a trim it is the number that is
        // actually being chosen — the edge position is only how it is being said.
        return $"{name} · {Format(clip.Range.Start)} to {Format(clip.Range.End)} · " +
               $"{Format(clip.Range.Length)} long";
    }

    /// <summary>
    /// Removes the selected overlay, or the one under the playhead.
    /// </summary>
    /// <remarks>
    /// The selection wins: having pointed at a clip, the user has already said which one they
    /// mean, and the playhead is very often somewhere else entirely by then. With nothing
    /// selected this is what the key has always done.
    /// </remarks>
    private void RemoveOverlay()
    {
        if ((SelectedOverlay ?? IndexOfOverlayAt(Playhead)) is not { } index
            || index >= Project.Overlays.Length)
        {
            Status = "No overlay selected or under the playhead.";
            return;
        }

        var name = DescribeOverlay(Project.Overlays[index]);

        Apply("Remove overlay", p => TimelineEdits.RemoveOverlay(p, index));
        Status = $"Removed {name} — Ctrl+Z to undo";
    }

    private int FramesPerSecond => Math.Max(1, (int)Math.Round(Project.Output.FrameRate.Approx));

    private void RippleDelete()
    {
        if (SelectedRange is not { } range)
        {
            Status = "Mark a range with I and O first.";
            return;
        }

        ShuttleRate = 0;
        var label = $"Ripple delete {Format(range.Length)}";

        Apply(label, p => TimelineEdits.RippleDelete(p, range));

        // The playhead follows the cut point, which is where the user's attention is and
        // where they will most likely mark the next cut.
        _playhead = Math.Clamp(range.Start, 0, Math.Max(0, DurationFrames - 1));
        MarkIn = null;
        MarkOut = null;

        Notify(nameof(Playhead));
        Notify(nameof(TimecodeText));
        RenderCurrentFrame();

        Status = $"{label} — Ctrl+Z to undo";
    }

    private void ClearCropAtPlayhead()
    {
        var at = Playhead;
        foreach (var crop in Project.Crops)
            if (crop.Range.Contains(at))
            {
                Apply("Clear crop", p => TimelineEdits.ClearCrop(p, crop.Range));
                return;
            }

        Status = "No crop under the playhead.";
    }

    public void SetCrop(FrameRange range, RectI rect) =>
        Apply("Crop", p => TimelineEdits.SetCrop(p, range, rect));

    /// <param name="gestureId">
    /// Names a continuing gesture, so the drag of an overlay leaves one undo step rather than
    /// one per mouse-move. See <see cref="EditorDocument.Apply"/>.
    /// </param>
    public void Apply(string label, Func<Project, Project> edit, string? gestureId = null)
    {
        _document.Apply(label, edit, gestureId);
        Status = label;
    }

    /// <summary>The nearest segment boundary in <paramref name="direction"/>.</summary>
    private long AdjacentBoundary(int direction)
    {
        var boundaries = Project.Base
            .Select(b => b.TimelineStart)
            .Concat([DurationFrames])
            .Distinct()
            .OrderBy(b => b)
            .ToList();

        return direction < 0
            ? boundaries.LastOrDefault(b => b < Playhead, 0)
            : boundaries.FirstOrDefault(b => b > Playhead, Math.Max(0, DurationFrames - 1));
    }

    // ---- playback ----------------------------------------------------------------

    private void RestartClock()
    {
        _playbackStartFrame = Playhead;
        _clock.Restart();

        // Audio needs nothing here: every route into 1x forward goes through the ShuttleRate
        // setter, which starts the device at the playhead of the moment, and nothing moves
        // the playhead without first setting the rate to zero.
    }

    /// <summary>
    /// Starts or stops the audio device to match the transport.
    /// </summary>
    /// <remarks>
    /// Sound only at 1x forward. Reverse and 2x-8x leave the device stopped and fall back to
    /// the stopwatch below: pitch-preserving resampling for an 8x scrub is a lot of machinery
    /// for something nobody listens to, and the transport glyph already says you are off
    /// normal speed.
    /// </remarks>
    private void SyncAudioTransport()
    {
        var wanted = _shuttleRate == 1 && HasMedia;

        if (wanted == _player.IsRunning) return;

        if (!wanted)
        {
            _player.Stop();
            return;
        }

        StartAudio();
    }

    /// <summary>
    /// Re-points the device at the edited timeline after the document changed under it.
    /// </summary>
    /// <remarks>
    /// The player holds the <see cref="Project"/> it was started with, which is right —
    /// immutability is what lets the device thread read it without a lock — but it means an
    /// undo or a redo during playback would keep playing the old edit. Rare enough that
    /// re-opening the device is the cheap answer.
    /// </remarks>
    private void RestartAudioIfPlaying()
    {
        if (!_player.IsRunning) return;

        _player.Stop();
        StartAudio();
    }

    private void StartAudio()
    {
        _player.Muted = IsMuted;

        try
        {
            _player.Start(Project, IndexOf, _playhead);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FfmpegDecodeException)
        {
            // A device that will not open must not stop the timeline playing; the stopwatch
            // below takes over on its own, because the player simply is not running.
            Status = $"No sound: {e.Message}";
        }
    }

    /// <summary>
    /// Advances the playhead to match elapsed time. Called from the composition tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While audio is playing the position comes from the device rather than from a
    /// stopwatch, so the picture follows the sound. That is the only ordering that stays
    /// right over a long take: sound cannot be nudged a frame to catch up without an audible
    /// click, and a video frame can be dropped without anyone noticing.
    /// </para>
    /// <para>
    /// Otherwise — reverse, shuttle, or no audio at all — the position is computed from
    /// elapsed time rather than incremented per tick, so a slow frame causes a dropped frame
    /// instead of playback drifting behind wall clock.
    /// </para>
    /// </remarks>
    public void Tick()
    {
        // Whatever the transport is doing, a frame the pump finished since the last tick
        // belongs on screen. During playback this is the path that presents almost every
        // frame: the ring already holds it, so the request the playhead raises below finds
        // nothing to decode and there is no second event to wait for.
        PresentLatest();

        if (ShuttleRate == 0 || !HasMedia) return;

        var target = _player.IsRunning
            ? _player.PositionFrames
            : _playbackStartFrame + (long)(_clock.Elapsed.TotalSeconds * Project.Output.FrameRate.Approx * ShuttleRate);

        if (target >= DurationFrames || (_player.IsRunning && _player.HasFinished))
        {
            Playhead = DurationFrames - 1;
            ShuttleRate = 0;
            return;
        }

        if (target < 0)
        {
            Playhead = 0;
            ShuttleRate = 0;
            return;
        }

        Playhead = target;
    }

    /// <summary>
    /// Runs at normal speed for as long as a step key is held down.
    /// </summary>
    /// <remarks>
    /// Called from the keyboard's auto-repeat rather than from the first keystroke, so a
    /// tap on <c>&lt;</c> or <c>&gt;</c> is one frame and a hold turns into playback.
    /// Re-arming the clock on every repeat would pin the playhead a frame from where it
    /// started, so an already-running hold is left alone.
    /// </remarks>
    public void HoldShuttle(int direction)
    {
        // Not while a clip is being aimed. Holding a step key there is how the playhead is
        // walked to where the overlay should go, and a hold that broke into playback would
        // send the clip skating off down the timeline.
        if (IsPlacing) { Dispatch(direction > 0 ? EditorIntent.StepForward : EditorIntent.StepBack); return; }

        if (ShuttleRate == direction) return;

        ShuttleRate = direction;
        RestartClock();
    }

    /// <summary>Ends the run a held step key started.</summary>
    public void ReleaseShuttle() => ShuttleRate = 0;

    /// <summary>Moves the playhead during a scrub without stopping to decode exactly.</summary>
    public void ScrubTo(long frame)
    {
        ShuttleRate = 0;
        Playhead = frame;
    }

    /// <summary>
    /// Asks the pump for the frame under the playhead. Returns immediately.
    /// </summary>
    /// <remarks>
    /// This used to decode inline, which meant every seek — a scrub, a ruler click, an
    /// arrival at a cut boundary — stopped the UI thread for the length of a seek. The
    /// picture now arrives through <see cref="PresentLatest"/> whenever it is ready, which
    /// during playback is usually before it was asked for.
    /// </remarks>
    private void RenderCurrentFrame()
    {
        if (_pump is null || !HasMedia) return;

        _pump.Request(PreviewProject(), _playhead, ShuttleRate);
        PresentLatest();
    }

    /// <summary>
    /// Puts the frame under the playhead on screen, if the pump has it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent, and called from both directions: from the pump, when a frame it was asked
    /// for lands, and from the composition tick, where during playback the frame is usually
    /// already waiting in the ring.
    /// </para>
    /// <para>
    /// <b>The nearest frame, not the playhead's.</b> During playback they are the same thing.
    /// During a drag they are not: every position is a seek, the playhead has moved again by
    /// the time one lands, and demanding an exact match threw away every frame that arrived
    /// while the pointer was still moving — so the picture only changed when the drag paused.
    /// Whatever is closest than what is on screen is worth showing.
    /// </para>
    /// </remarks>
    private void PresentLatest()
    {
        if (_pump is null) return;
        if (_pump.LeaseNearest(_playhead, _frame?.FrameIndex) is not { } next) return;

        var previous = _frame;
        _frame = next;

        FrameChanged?.Invoke(next);

        // Only once the shell has copied out of it, and never the one just taken: the pump is
        // free to overwrite a returned buffer the moment it has it back.
        if (previous is not null) _pump.Return(previous);
    }

    /// <summary>
    /// The project the preview renders, including any in-progress placement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two placements want opposite things from the preview. A pending <b>overlay</b>
    /// is composited live, because the whole point of positioning it is seeing where its
    /// picture lands. A pending <b>crop</b> deliberately is not applied: while choosing the
    /// region you need to see the frame you are choosing from, not the zoomed result. The
    /// rectangle is drawn over the preview instead, and the zoom appears on commit.
    /// </para>
    /// <para>
    /// A project rather than a resolver, because the resolver is the one thing here that is
    /// <em>not</em> shareable across threads — its hint cache makes it stateful. The pump
    /// builds its own from this and keeps it until the reference changes, which is exactly
    /// what immutability makes safe.
    /// </para>
    /// </remarks>
    private Project PreviewProject()
    {
        if (Mode != EditorMode.Overlay) return Project;

        var clip = new OverlayClip(
            PendingRange, PendingOverlaySourceId, PendingOverlaySourceStart, PendingRect);

        return TimelineEdits.AddOverlay(Project, clip);
    }

    /// <summary>
    /// Composites at this size rather than at the project's output size.
    /// </summary>
    /// <remarks>
    /// The shell works it out from the area the picture actually occupies. Nothing else
    /// changes: crop and overlay geometry stays in output space and is mapped through on the
    /// way into the canvas.
    /// </remarks>
    public void SetRenderSize(int width, int height)
    {
        if (width < 1 || height < 1) return;
        if (_renderSize == (width, height)) return;

        _renderSize = (width, height);
        _pump?.SetRenderSize(width, height);

        // The ring was rebuilt at the new size, so the frame on screen is one the pump no
        // longer owns and the playhead's frame has to be asked for again.
        _frame = null;
        RenderCurrentFrame();
    }

    /// <summary>
    /// Blocks until the frame under the playhead has been composited and presented.
    /// </summary>
    /// <remarks>
    /// For the harness only. Rendering is asynchronous now, and a capture taken without this
    /// would photograph whatever happened to be on screen when the script got there.
    /// </remarks>
    public bool WaitForPreviewIdle(int timeoutMs)
    {
        if (_pump is null) return true;

        var idle = _pump.WaitForIdle(TimeSpan.FromMilliseconds(timeoutMs));
        PresentLatest();

        return idle;
    }

    /// <summary>
    /// Whether the picture on screen is the playhead's frame.
    /// </summary>
    /// <remarks>
    /// The harness's stopping condition, and not the same question as "is the pump idle".
    /// Presenting a frame can ask for another: the preview pane sizes itself to the picture,
    /// so the first frame of a newly opened video is what tells the shell how much detail is
    /// worth compositing, and the answer sends the frame back to be rendered again.
    /// </remarks>
    public bool PreviewSettled =>
        _pump is null || !HasMedia || _frame?.FrameIndex == _playhead;

    private void OnDocumentChanged(Project project)
    {
        // Each consumer builds its own resolver because the hint cache inside one is not
        // shareable; rebuilding is trivial.
        _resolver = new TimelineResolver(project);

        // A selection is an index into a list, and an edit is free to renumber it: a ripple
        // delete drops clips, an undo brings others back, and the index would then be
        // pointing at somebody else's. The two callers that know better are the drags, which
        // keep their own index in step with what they are rewriting.
        if (!_draggingOverlay) SelectedOverlay = null;
        if (!_draggingSegment) SelectedSegment = null;

        if (_playhead >= project.DurationFrames)
            _playhead = Math.Max(0, project.DurationFrames - 1);

        // An undo reached through placement mode can shorten the timeline, or bring back a
        // clip, under a band that is still being aimed.
        UpdatePendingRange();

        _pump?.SetOutput(project.Output);
        RestartAudioIfPlaying();

        Notify(nameof(Project));
        Notify(nameof(DurationFrames));
        Notify(nameof(HasMedia));
        Notify(nameof(TimecodeText));
        Notify(nameof(CanUndo));
        Notify(nameof(CanRedo));

        RenderCurrentFrame();

        _autosave.Stop();
        _autosave.Start();
    }

    private void SaveSession()
    {
        if (_sessionKey is null || !HasMedia) return;

        try
        {
            SessionStore.Save(_sessionKey, Project);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = "Autosave failed — check disk space.";
        }
    }

    /// <summary>Forces a save, for window close and before an export.</summary>
    public void FlushSession()
    {
        _autosave.Stop();
        SaveSession();
    }

    public string Format(long frames)
    {
        var rate = Project.Output.FrameRate;
        var (seconds, remainder) = RationalMath.SplitSeconds(frames, rate);
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}:{remainder:00}";
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _autosave.Stop();

        // Both of these own a thread, and both hold decoders that keep the source files open
        // until the thread they were opened on has been joined.
        _player.Dispose();
        DisposePump();
    }

    /// <summary>
    /// Builds the pump for a newly opened project and subscribes to it.
    /// </summary>
    /// <remarks>
    /// Recreated per open rather than re-pointed, because the lookups it decodes through are
    /// closed over the indices and the document, and an open replaces both. The render size
    /// the shell last asked for is carried across, so a second video does not spend its first
    /// frame at full resolution before the next resize corrects it.
    /// </remarks>
    private PreviewPump CreatePump(OutputFormat output)
    {
        var pump = new PreviewPump(
            output,
            id => _indices[id],
            id => _document.Current.RequireSource(id).Path);

        // Both fire on the pump thread, so both hop back. Render priority for the frame,
        // because it is a repaint and should not queue behind background work; the status
        // line can wait.
        pump.FrameReady += () => _dispatcher.BeginInvoke(PresentLatest, DispatcherPriority.Render);

        pump.Failed += message => _dispatcher.BeginInvoke(
            () => Status = $"Preview failed: {message}", DispatcherPriority.Background);

        if (_renderSize is { } size) pump.SetRenderSize(size.Width, size.Height);

        return pump;
    }

    private void DisposePump()
    {
        var pump = _pump;
        _pump = null;

        // Dropped first: it is on loan from a ring that is about to go, and holding a
        // reference to it past the dispose would keep a frame of a closed video on screen.
        _frame = null;

        pump?.Dispose();
    }
}
