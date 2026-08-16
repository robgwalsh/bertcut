using System.Diagnostics;
using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Media;
using BertCut.Media.Decode;

namespace BertCut.Core.Tests;

/// <summary>
/// Pins the rules the preview pump exists for: the frame you asked for is the one you get,
/// the ones after it are decoded before anybody asks, and a buffer on loan is not written to.
/// </summary>
/// <remarks>
/// Everything here is a question about pixels and scheduling rather than about the interface,
/// so it belongs in this tier and not the harness. The source counts in its own picture —
/// each frame's red channel is its own index — which is what makes "did the right frame
/// arrive" a comparison rather than an assumption.
/// </remarks>
[Collection("ffmpeg")]
public class PreviewPumpTests : IDisposable
{
    /// <summary>Long enough that a busy machine cannot fail it, short enough to notice a hang.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly FfmpegRuntime? _runtime;
    private readonly string _dir;

    public PreviewPumpTests()
    {
        try
        {
            _runtime = FfmpegRuntime.Locate();
            FfmpegLoader.EnsureInitialized(_runtime);
        }
        catch (Exception e) when (e is FileNotFoundException or DllNotFoundException)
        {
            _runtime = null;
        }

        _dir = Path.Combine(Path.GetTempPath(), "bertcut-pump", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A clip whose every frame is a distinct flat colour, with a long GOP so a backwards
    /// step is a real seek rather than a lucky landing on a keyframe.
    /// </summary>
    private string MakeCountingSource(int frames = 240)
    {
        var path = Path.Combine(_dir, "counter.mp4");

        var psi = new ProcessStartInfo(_runtime!.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in new[]
        {
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi",
            "-i", $"color=c=black:size=320x240:rate=30:duration={frames / 30.0}",
            "-vf", "geq=r='N':g='0':b='0'",
            "-c:v", "libopenh264", "-b:v", "4M", "-g", "120", "-pix_fmt", "yuv420p",
            "-fps_mode", "cfr", "-r", "30", "-frames:v", frames.ToString(),
            path,
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Failed to build the test source: {stderr}");

        return path;
    }

    /// <summary>Red channel of the frame's centre pixel — its identity marker.</summary>
    private static int RedAt(DecodedFrame frame)
    {
        var offset = ((frame.Height / 2) * frame.Stride) + ((frame.Width / 2) * 4);
        return frame.Pixels[offset + 2];
    }

    private static void AssertShows(long frame, DecodedFrame decoded)
    {
        Assert.Equal(frame, decoded.FrameIndex);
        Assert.InRange(RedAt(decoded), (int)frame - 6, (int)frame + 6);
    }

    private async Task<(Project Project, PreviewPump Pump)> SetUpAsync(int frames = 240)
    {
        var probe = await new MediaProber(_runtime!).ProbeAsync(MakeCountingSource(frames));

        var output = new OutputFormat(320, 240, Rational.FromInt(30));
        var project = TimelineEdits.ImportSource(Project.Empty(output), probe.Media);

        var pump = new PreviewPump(
            output,
            _ => probe.Index,
            id => project.RequireSource(id).Path);

        return (project, pump);
    }

    /// <summary>Waits for something the read-ahead will get to, which idle deliberately does not cover.</summary>
    private static void WaitUntil(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < Patience)
        {
            if (condition()) return;
            Thread.Sleep(5);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    [SkippableFact]
    public async Task The_frame_requested_is_the_frame_delivered()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, pump) = await SetUpAsync();
        using var scope = pump;

        pump.Request(project, 137, 0);
        Assert.True(pump.WaitForIdle(Patience));

        var frame = pump.Lease(137);
        Assert.NotNull(frame);
        AssertShows(137, frame);
    }

    /// <summary>
    /// The property the whole ring exists for: the frames after the one on screen are already
    /// decoded, so a stall on the UI thread costs a repaint rather than a decode.
    /// </summary>
    [SkippableFact]
    public async Task Playing_forward_decodes_ahead_of_the_playhead()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, pump) = await SetUpAsync();
        using var scope = pump;

        pump.Request(project, 60, 1);
        Assert.True(pump.WaitForIdle(Patience));

        // Every slot but the requested frame's and one kept back for the frame on screen.
        var ahead = pump.Slots - 2;
        WaitUntil(() => pump.Holds(60 + ahead), $"read-ahead to reach frame {60 + ahead}");

        for (var f = 60; f <= 60 + ahead; f++)
        {
            var frame = pump.Lease(f);
            Assert.True(frame is not null, $"frame {f} should have been read ahead");
            AssertShows(f, frame!);
            pump.Return(frame!);
        }
    }

    /// <summary>
    /// Reverse fills the whole window behind the playhead at once, so every frame in it is
    /// already decoded before it is asked for.
    /// </summary>
    /// <remarks>
    /// This is the rule that makes reverse playable at all. There is no route to an earlier
    /// frame but the preceding keyframe, so a backwards step is a seek — ~85 ms on a real
    /// recording, which is longer than a frame lasts. Served one at a time it never converges:
    /// the seek outlasts the frame period, so the playhead has moved on before it lands and
    /// asks for another, and the fill behind it never gets a turn. Filling the window in one
    /// ascending run makes it one seek per window instead.
    /// </remarks>
    [SkippableFact]
    public async Task Playing_backward_fills_a_whole_window_at_a_time()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, pump) = await SetUpAsync();
        using var scope = pump;

        // A frame at the top of a window, which is where a reverse run enters one.
        var (start, top) = WindowAround(pump, 200);

        pump.Request(project, top, -1);
        Assert.True(pump.WaitForIdle(Patience));

        for (var f = top; f >= start; f--)
        {
            // Before asking, not after: the point is that the run already decoded it.
            Assert.True(pump.Holds(f), $"frame {f} should have been filled with the rest of its window");

            pump.Request(project, f, -1);
            Assert.True(pump.WaitForIdle(Patience));

            var frame = pump.Lease(f);
            Assert.True(frame is not null, $"frame {f} should be leasable");
            AssertShows(f, frame!);
            pump.Return(frame!);
        }
    }

    /// <summary>
    /// The window before the one being played is filled before the playhead reaches it.
    /// </summary>
    /// <remarks>
    /// One seek per window is still one visible hold per window without this: the ring runs
    /// dry at exactly the moment the next seek has to happen. Decoding the earlier window
    /// while the playhead is still walking through the current one is what the ring is sized
    /// to hold two windows for.
    /// </remarks>
    [SkippableFact]
    public async Task A_reverse_run_fills_the_next_window_before_reaching_it()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, pump) = await SetUpAsync();
        using var scope = pump;

        var (start, top) = WindowAround(pump, 200);

        pump.Request(project, top, -1);
        Assert.True(pump.WaitForIdle(Patience));

        // Never asked for, and a whole window away from anything that was.
        WaitUntil(() => pump.Holds(start - 1), $"the window before {start} to be read behind");

        var frame = pump.Lease(start - 1);
        Assert.True(frame is not null, $"frame {start - 1} should have been prefetched");
        AssertShows(start - 1, frame!);
    }

    /// <summary>The reverse window containing <paramref name="near"/>, as (start, top).</summary>
    private static (long Start, long Top) WindowAround(PreviewPump pump, long near)
    {
        var window = pump.Window;
        var start = near / window * window;

        return (start, start + window - 1);
    }

    /// <summary>
    /// A burst — a scrub, or a playhead moving faster than the decoder — ends on the last
    /// frame asked for, not on one from the middle of the run.
    /// </summary>
    [SkippableFact]
    public async Task A_burst_of_requests_settles_on_the_last_one()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, pump) = await SetUpAsync();
        using var scope = pump;

        // Deliberately not sequential and deliberately not settled between: this is the shape
        // of a drag along the ruler, where all but the last position is already stale.
        for (var f = 0; f < 200; f += 17) pump.Request(project, f, 0);

        pump.Request(project, 199, 0);
        Assert.True(pump.WaitForIdle(Patience));

        var frame = pump.Lease(199);
        Assert.NotNull(frame);
        AssertShows(199, frame);
    }

    /// <summary>
    /// A drag takes the nearest frame there is rather than holding out for its own.
    /// </summary>
    /// <remarks>
    /// This is what makes click-dragging the playhead move. Every scrub position is a seek,
    /// so the pointer outruns the decoder by construction and the playhead has moved on again
    /// by the time a frame lands. Demanding the exact match threw all of those away and the
    /// picture only changed when the drag paused — choppier than the synchronous decode it
    /// replaced, which at least always showed the frame it had just spent 115 ms on.
    /// </remarks>
    [SkippableFact]
    public async Task A_scrub_takes_the_nearest_frame_rather_than_waiting_for_the_exact_one()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, pump) = await SetUpAsync();
        using var scope = pump;

        pump.Request(project, 100, 0);
        Assert.True(pump.WaitForIdle(Patience));

        // Wanting a frame nothing has decoded yet, while showing one further away still.
        var frame = pump.LeaseNearest(220, holding: 20);

        Assert.True(frame is not null, "a drag should be given the closest frame there is");
        Assert.InRange(frame!.FrameIndex, 100, 219);
    }

    /// <summary>The other half of it: nothing on offer beats what is already on screen.</summary>
    /// <remarks>
    /// Without this the shell would swap buffers on every composition tick — sixty times a
    /// second, each one a full-frame copy into the bitmap — to display the picture it was
    /// already displaying.
    /// </remarks>
    [SkippableFact]
    public async Task Nothing_is_leased_when_the_frame_on_screen_is_already_the_nearest()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, pump) = await SetUpAsync();
        using var scope = pump;

        pump.Request(project, 100, 0);
        Assert.True(pump.WaitForIdle(Patience));

        Assert.Null(pump.LeaseNearest(100, holding: 100));
    }

    /// <summary>
    /// A leased buffer is the one on screen, and the producer must not write into it.
    /// </summary>
    /// <remarks>
    /// Without this the ring would tear: the pump would overwrite the frame WPF is copying
    /// out of, and half a picture would reach the surface. It is the reason a slot carries an
    /// in-use flag at all rather than just a frame index.
    /// </remarks>
    [SkippableFact]
    public async Task A_leased_frame_is_not_written_over()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, pump) = await SetUpAsync();
        using var scope = pump;

        pump.Request(project, 10, 1);
        Assert.True(pump.WaitForIdle(Patience));

        var held = pump.Lease(10);
        Assert.NotNull(held);

        // Enough traffic to cycle every slot several times over.
        for (var f = 100; f < 220; f++)
        {
            pump.Request(project, f, 1);
            Assert.True(pump.WaitForIdle(Patience));
        }

        AssertShows(10, held);
    }

    /// <summary>
    /// Rendering smaller is a resize of the ring, and the frames that come back are still the
    /// right ones — just fewer pixels of them.
    /// </summary>
    [SkippableFact]
    public async Task Rendering_at_a_smaller_size_still_delivers_the_right_frame()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, pump) = await SetUpAsync();
        using var scope = pump;

        pump.Request(project, 40, 0);
        Assert.True(pump.WaitForIdle(Patience));

        pump.SetRenderSize(160, 120);
        pump.Request(project, 40, 0);
        Assert.True(pump.WaitForIdle(Patience));

        Assert.Equal(160, pump.RenderWidth);
        Assert.Equal(120, pump.RenderHeight);

        var frame = pump.Lease(40);
        Assert.NotNull(frame);
        Assert.Equal(160, frame.Width);
        Assert.Equal(120, frame.Height);
        AssertShows(40, frame);
    }

    /// <summary>
    /// The ring is budgeted in bytes, so its depth falls out of the resolution rather than
    /// being a number that happens to suit one project size.
    /// </summary>
    /// <summary>
    /// The ring is budgeted in bytes, so its depth falls out of the resolution rather than
    /// being a number that happens to suit one project size.
    /// </summary>
    /// <remarks>
    /// A frame is a megabyte at 640x384 and twenty-four at 4K. A fixed count of buffers would
    /// be either trivial or most of a gigabyte, depending entirely on what was opened.
    /// </remarks>
    [SkippableFact]
    public async Task A_larger_render_size_buys_a_shallower_ring()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (_, pump) = await SetUpAsync(frames: 30);
        using var scope = pump;

        var before = pump.Slots;

        pump.SetRenderSize(1280, 960);
        Assert.True(pump.WaitForIdle(Patience));

        Assert.True(
            pump.Slots < before,
            $"a 1280x960 frame should buy fewer slots than a 320x240 one's {before}, got {pump.Slots}");
    }

    /// <summary>Resetting under a render in flight closes the decoders without tearing anything.</summary>
    [SkippableFact]
    public async Task Resetting_while_busy_leaves_the_pump_usable()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, pump) = await SetUpAsync();
        using var scope = pump;

        for (var f = 0; f < 200; f += 13)
        {
            pump.Request(project, f, 1);
            pump.Reset();
        }

        pump.Request(project, 88, 0);
        Assert.True(pump.WaitForIdle(Patience));

        var frame = pump.Lease(88);
        Assert.NotNull(frame);
        AssertShows(88, frame);
    }
}
