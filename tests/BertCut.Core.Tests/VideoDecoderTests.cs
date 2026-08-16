using System.Diagnostics;
using BertCut.Media;
using BertCut.Media.Decode;

namespace BertCut.Core.Tests;

/// <summary>
/// Exercises the in-process decoder against a real file.
/// </summary>
/// <remarks>
/// Frame accuracy is the property the whole editor rests on, so these tests check that a
/// requested frame index yields <em>that</em> frame — not one nearby — by decoding a
/// source whose every frame is visually distinct and comparing pixels.
/// </remarks>
[Collection("ffmpeg")]
public class VideoDecoderTests : IDisposable
{
    private readonly FfmpegRuntime? _runtime;
    private readonly string _dir;

    public VideoDecoderTests()
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

        _dir = Path.Combine(Path.GetTempPath(), "bertcut-decode", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A clip whose every frame is a distinct flat colour, so a decoded frame's identity
    /// can be read straight off any single pixel.
    /// </summary>
    private string MakeCountingSource(int frames = 120)
    {
        var path = Path.Combine(_dir, "counter.mp4");

        // Each frame's red channel steps by 2, giving 120 unambiguous frames. A long GOP
        // forces real seek-and-discard work rather than landing on a keyframe every time.
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
            "-vf", "geq=r='2*N':g='0':b='0'",
            "-c:v", "libopenh264", "-b:v", "4M", "-g", "60", "-pix_fmt", "yuv420p",
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
        return frame.Pixels[offset + 2];   // BGRA
    }

    [SkippableFact]
    public async Task Decoding_a_requested_frame_returns_that_exact_frame()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var path = MakeCountingSource();
        var probe = await new MediaProber(_runtime!).ProbeAsync(path);

        using var decoder = new VideoDecoder(path, probe.Index, 320, 240);
        var frame = new DecodedFrame(320, 240);

        // Deliberately out of order, and mostly not on keyframes, so each one exercises
        // the seek-flush-and-discard path.
        foreach (var index in new long[] { 0, 45, 12, 99, 61, 30, 1, 118 })
        {
            Assert.True(decoder.TryDecodeFrame(index, frame), $"failed to decode frame {index}");
            Assert.Equal(index, frame.FrameIndex);

            // The generator wrote red = 2*N. Allow a small tolerance for lossy encoding.
            Assert.InRange(RedAt(frame), (int)(2 * index) - 6, (int)(2 * index) + 6);
        }
    }

    [SkippableFact]
    public async Task Stepping_forward_one_frame_at_a_time_stays_in_order()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var path = MakeCountingSource();
        var probe = await new MediaProber(_runtime!).ProbeAsync(path);

        using var decoder = new VideoDecoder(path, probe.Index, 320, 240);
        var frame = new DecodedFrame(320, 240);

        // This is the playback path: sequential advance must not re-seek.
        for (long i = 0; i < 60; i++)
        {
            Assert.True(decoder.TryDecodeFrame(i, frame));
            Assert.Equal(i, frame.FrameIndex);
            Assert.InRange(RedAt(frame), (int)(2 * i) - 6, (int)(2 * i) + 6);
        }
    }

    [SkippableFact]
    public async Task Stepping_backward_one_frame_returns_the_previous_image()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        // Left-arrow after right-arrow must land on the frame the user just left, which
        // requires a full re-seek rather than reusing the decoder's position.
        var path = MakeCountingSource();
        var probe = await new MediaProber(_runtime!).ProbeAsync(path);

        using var decoder = new VideoDecoder(path, probe.Index, 320, 240);
        var frame = new DecodedFrame(320, 240);

        decoder.TryDecodeFrame(50, frame);
        var atFifty = RedAt(frame);

        decoder.TryDecodeFrame(51, frame);
        decoder.TryDecodeFrame(50, frame);

        Assert.Equal(50, frame.FrameIndex);
        Assert.InRange(RedAt(frame), atFifty - 2, atFifty + 2);
    }

    [SkippableFact]
    public async Task Decoding_scales_to_the_requested_output_size()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var path = MakeCountingSource(frames: 30);
        var probe = await new MediaProber(_runtime!).ProbeAsync(path);

        using var decoder = new VideoDecoder(path, probe.Index, 160, 120);
        var frame = new DecodedFrame(160, 120);

        Assert.True(decoder.TryDecodeFrame(10, frame));
        Assert.Equal(320, decoder.SourceWidth);
        Assert.Equal(160, decoder.OutputWidth);

        // Every row must be filled, which catches a stride mismatch between sws's padded
        // output and the packed buffer WPF expects.
        Assert.Contains(frame.Pixels, b => b != 0);
        var lastRow = (frame.Height - 1) * frame.Stride;
        Assert.Equal(255, frame.Pixels[lastRow + 3]);   // alpha
    }

    /// <summary>
    /// The playback recovery path: a frame arrives late, the playhead has moved on, and the
    /// frame after next is asked for.
    /// </summary>
    /// <remarks>
    /// Seeking to serve that costs half a GOP of decoding — on a real 1280x768 recording
    /// with a 250-frame GOP, 115 ms against 1.8 ms for a sequential frame. Since the
    /// playhead follows wall-clock time, skipping is how a late frame is recovered, so a
    /// recovery 60x dearer than the frame it recovered from left the decoder further behind
    /// than it started and playback never caught up. Counting seeks rather than timing them,
    /// because the cost is real but a stopwatch here would fail on a busy machine.
    /// </remarks>
    [SkippableFact]
    public async Task Skipping_a_frame_decodes_on_rather_than_seeking_back()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var path = MakeCountingSource();
        var probe = await new MediaProber(_runtime!).ProbeAsync(path);

        using var decoder = new VideoDecoder(path, probe.Index, 320, 240);
        var frame = new DecodedFrame(320, 240);

        // Mid-GOP — the source has keyframes every 60 frames, so seeking to serve 42 would
        // decode and discard 42 frames to deliver the two this is actually ahead of.
        decoder.TryDecodeFrame(40, frame);
        var seeks = decoder.SeekCount;

        Assert.True(decoder.TryDecodeFrame(42, frame));
        Assert.Equal(42, frame.FrameIndex);
        Assert.InRange(RedAt(frame), (2 * 42) - 6, (2 * 42) + 6);
        Assert.Equal(seeks, decoder.SeekCount);
    }

    /// <summary>The other half of that rule: far enough ahead, the keyframe is the nearer start.</summary>
    /// <remarks>
    /// Without this the decoder would grind forward through the whole file rather than seek,
    /// which is the same mistake in the opposite direction. Both routes decode and discard
    /// to the target, so whichever is fewer frames away wins.
    /// </remarks>
    [SkippableFact]
    public async Task Jumping_past_a_keyframe_still_seeks()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var path = MakeCountingSource();
        var probe = await new MediaProber(_runtime!).ProbeAsync(path);

        using var decoder = new VideoDecoder(path, probe.Index, 320, 240);
        var frame = new DecodedFrame(320, 240);

        // From 10 to 110: 100 frames of decoding on, against 50 from the keyframe at 60.
        decoder.TryDecodeFrame(10, frame);
        var seeks = decoder.SeekCount;

        Assert.True(decoder.TryDecodeFrame(110, frame));
        Assert.Equal(110, frame.FrameIndex);
        Assert.InRange(RedAt(frame), (2 * 110) - 6, (2 * 110) + 6);
        Assert.Equal(seeks + 1, decoder.SeekCount);
    }

    [SkippableFact]
    public async Task Requesting_a_frame_outside_the_source_fails_cleanly()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var path = MakeCountingSource(frames: 30);
        var probe = await new MediaProber(_runtime!).ProbeAsync(path);

        using var decoder = new VideoDecoder(path, probe.Index, 320, 240);
        var frame = new DecodedFrame(320, 240);

        Assert.False(decoder.TryDecodeFrame(-1, frame));
        Assert.False(decoder.TryDecodeFrame(probe.Index.FrameCount + 10, frame));
    }
}
