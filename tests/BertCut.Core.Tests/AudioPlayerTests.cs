using System.Diagnostics;
using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Media;
using BertCut.Media.Audio;
using BertCut.Media.Decode;

namespace BertCut.Core.Tests;

/// <summary>
/// The playback clock, driven through <see cref="SilentAudioOutput"/>.
/// </summary>
/// <remarks>
/// The silent output is what the harness uses, so testing against it is testing the path a
/// scripted run actually takes — and it means these tests need no sound card and make no
/// sound. What is being pinned is the mapping the editor inverts: rendered audio seconds to
/// timeline frames. If that is wrong, the picture drifts against the sound and no amount of
/// correct correlation upstream will look right.
/// </remarks>
[Collection("ffmpeg")]
public class AudioPlayerTests : IDisposable
{
    private const int Fps = 30;
    private const int SampleRate = 48000;

    private readonly FfmpegRuntime? _runtime;
    private readonly string _dir;

    public AudioPlayerTests()
    {
        try
        {
            _runtime = FfmpegRuntime.Locate();
            if (_runtime is not null) FfmpegLoader.EnsureInitialized(_runtime);
        }
        catch (FileNotFoundException)
        {
            _runtime = null;
        }

        _dir = Path.Combine(Path.GetTempPath(), "bertcut-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [SkippableFact]
    public async Task The_position_advances_in_step_with_wall_clock()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, index) = await ProjectAsync("clock.mp4", seconds: 10);

        using var player = new AudioPlayer(() => new SilentAudioOutput());
        Assert.True(player.Start(project, _ => index, fromFrame: 0));

        Assert.Equal(0, player.PositionFrames);

        var watch = Stopwatch.StartNew();
        Thread.Sleep(600);
        watch.Stop();

        var expected = watch.Elapsed.TotalSeconds * Fps;

        // Bounded, not exact: this runs off a real clock, so the same rule applies as to the
        // harness's playback assertions.
        Assert.InRange(player.PositionFrames, expected - 6, expected + 6);
        Assert.True(player.IsRunning);
    }

    [SkippableFact]
    public async Task Playback_starts_from_the_frame_it_was_given()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, index) = await ProjectAsync("seek.mp4", seconds: 10);

        using var player = new AudioPlayer(() => new SilentAudioOutput());
        Assert.True(player.Start(project, _ => index, fromFrame: 150));

        Assert.InRange(player.PositionFrames, 150, 156);
    }

    [SkippableFact]
    public async Task A_timeline_with_no_audio_declines_to_start()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var path = Path.Combine(_dir, "silent.mp4");
        Run(_runtime!.FfmpegPath,
        [
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", $"testsrc2=size=160x120:rate={Fps}:duration=3",
            "-c:v", "libopenh264", "-b:v", "500k", "-pix_fmt", "yuv420p",
            "-fps_mode", "cfr", "-r", Fps.ToString(),
            path,
        ]);

        var probe = await new MediaProber(_runtime).ProbeAsync(path);
        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(160, 120, Rational.FromInt(Fps), SampleRate)), probe.Media);

        using var player = new AudioPlayer(() => new SilentAudioOutput());

        // The caller keeps its own clock in this case, so saying so matters.
        Assert.False(player.Start(project, _ => probe.Index, fromFrame: 0));
        Assert.False(player.IsRunning);
    }

    [SkippableFact]
    public async Task Stopping_releases_the_source_file()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, index) = await ProjectAsync("release.mp4", seconds: 5);
        var path = project.Sources[0].Path;

        var player = new AudioPlayer(() => new SilentAudioOutput());
        Assert.True(player.Start(project, _ => index, fromFrame: 0));

        Thread.Sleep(100);
        player.Dispose();

        // Exclusive open, so this really tests that the decoder's handle went away with the
        // device thread rather than outliving it.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [SkippableFact]
    public async Task Muting_does_not_stop_the_clock()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, index) = await ProjectAsync("mute.mp4", seconds: 10);

        using var player = new AudioPlayer(() => new SilentAudioOutput()) { Muted = true };
        Assert.True(player.Start(project, _ => index, fromFrame: 0));

        Thread.Sleep(400);

        // Mute is a monitoring control, not a transport one — pressing it mid-playback must
        // not make the picture jump.
        Assert.True(player.PositionFrames > 3, $"position was {player.PositionFrames}");
    }

    private async Task<(Project Project, Core.Media.SourceIndex Index)> ProjectAsync(
        string name, int seconds)
    {
        var path = Path.Combine(_dir, name);

        Run(_runtime!.FfmpegPath,
        [
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", $"testsrc2=size=160x120:rate={Fps}:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={SampleRate}:duration={seconds}",
            "-c:v", "libopenh264", "-b:v", "500k", "-g", "30", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "96k",
            "-fps_mode", "cfr", "-r", Fps.ToString(),
            path,
        ]);

        var probe = await new MediaProber(_runtime).ProbeAsync(path);

        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(160, 120, Rational.FromInt(Fps), SampleRate)), probe.Media);

        return (project, probe.Index);
    }

    private static void Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({process.ExitCode}): {stderr}");
    }
}
