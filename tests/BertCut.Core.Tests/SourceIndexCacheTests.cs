using System.Diagnostics;
using BertCut.Core.Session;
using BertCut.Media;

namespace BertCut.Core.Tests;

/// <summary>
/// The timestamp index survives a restart, so a file is only ever indexed once.
/// </summary>
/// <remarks>
/// Building one is three ffprobe launches, the last of which walks every packet in the container.
/// It was paid again on every open, append and session restore of a file already indexed — and
/// <c>SourceIndex</c>'s own remarks had claimed it was cached on disk since before anything wrote
/// it. Launches are counted rather than timed, for the reason <c>VideoDecoder.SeekCount</c> gives.
/// </remarks>
[Collection("ffmpeg")]
public class SourceIndexCacheTests : IDisposable
{
    private readonly FfmpegRuntime? _runtime;
    private readonly string _dir;

    private readonly string? _previousOverride =
        Environment.GetEnvironmentVariable(AppPaths.OverrideVariable);

    public SourceIndexCacheTests()
    {
        try { _runtime = FfmpegRuntime.Locate(); }
        catch (FileNotFoundException) { _runtime = null; }

        _dir = Path.Combine(Path.GetTempPath(), "bertcut-index", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, Path.Combine(_dir, "state"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, _previousOverride);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>The rule the cache exists for: a second probe of the same file launches nothing.</summary>
    [SkippableFact]
    public async Task A_second_probe_launches_no_ffprobe()
    {
        Skip.If(_runtime is null, "No ffmpeg installed.");

        var path = MakeSource();
        var prober = new MediaProber(_runtime!);

        var cold = await prober.ProbeAsync(path);

        var before = MediaProber.ProbeLaunches;
        var warm = await prober.ProbeAsync(path);
        var launched = MediaProber.ProbeLaunches - before;

        Assert.Equal(0, launched);

        // And the answer is the same one, not merely a cheap one.
        Assert.Equal(cold.Media.ContentKey, warm.Media.ContentKey);
        Assert.Equal(cold.Media.FrameCount, warm.Media.FrameCount);
        Assert.Equal(cold.Media.Width, warm.Media.Width);
        Assert.Equal(cold.Media.Height, warm.Media.Height);
        Assert.Equal(cold.Media.FrameRate, warm.Media.FrameRate);
        Assert.Equal(cold.Media.HasAudio, warm.Media.HasAudio);
        Assert.Equal(cold.Media.VideoCodec, warm.Media.VideoCodec);
        Assert.Equal(cold.Index.TimeBase, warm.Index.TimeBase);
        Assert.Equal(cold.Index.Pts, warm.Index.Pts);
        Assert.Equal(cold.Index.KeyFrames, warm.Index.KeyFrames);
    }

    /// <summary>
    /// Keyed by content, not by path — so reorganising a recordings folder does not throw the
    /// index away.
    /// </summary>
    [SkippableFact]
    public async Task A_renamed_file_still_hits_the_cache()
    {
        Skip.If(_runtime is null, "No ffmpeg installed.");

        var path = MakeSource();
        var prober = new MediaProber(_runtime!);
        await prober.ProbeAsync(path);

        var moved = Path.Combine(_dir, "renamed.mp4");
        File.Move(path, moved);

        var before = MediaProber.ProbeLaunches;
        var result = await prober.ProbeAsync(moved);

        Assert.Equal(0, MediaProber.ProbeLaunches - before);

        // The path is stamped from the caller, so the result describes where the file is now.
        Assert.Equal(moved, result.Media.Path);
    }

    private string MakeSource()
    {
        var path = Path.Combine(_dir, "clip.mp4");

        var psi = new ProcessStartInfo(_runtime!.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in new[]
        {
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=2",
            "-c:v", "libopenh264", "-b:v", "2M", "-g", "30", "-pix_fmt", "yuv420p",
            "-fps_mode", "cfr", "-r", "30", path,
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
}
