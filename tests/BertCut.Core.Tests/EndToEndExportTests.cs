using System.Diagnostics;
using BertCut.Core.Edits;
using BertCut.Core.Export;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Media;

namespace BertCut.Core.Tests;

/// <summary>
/// Runs the real pipeline — ffprobe import, plan, ffmpeg export — against a generated
/// video file.
/// </summary>
/// <remarks>
/// These are the tests that catch what the unit tests structurally cannot: an argument
/// that is well-formed but that ffmpeg rejects, a filter graph with a typo, or a cut that
/// produces the wrong duration. They skip cleanly when no suitable ffmpeg is installed, so
/// the suite still runs on a machine that has not run tools/fetch-ffmpeg.ps1.
/// </remarks>
[Collection("ffmpeg")]
public class EndToEndExportTests : IDisposable
{
    private readonly FfmpegRuntime? _runtime;
    private readonly string _dir;

    public EndToEndExportTests()
    {
        try
        {
            _runtime = FfmpegRuntime.Locate();
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

    /// <summary>
    /// Renders a test clip: a visible frame counter, a tone, a fixed 30 fps, and a
    /// keyframe every 30 frames so keyframe-boundary behaviour is predictable.
    /// </summary>
    private string MakeSource(string name, int seconds = 10)
    {
        var path = Path.Combine(_dir, name);

        Run(_runtime!.FfmpegPath,
        [
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", $"testsrc=size=640x480:rate=30:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds}",
            "-c:v", "libopenh264", "-b:v", "2M", "-g", "30", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k",
            "-fps_mode", "cfr", "-r", "30",
            path,
        ]);

        return path;
    }

    private static string Run(string exe, string[] args)
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
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({process.ExitCode}): {stderr}");

        return stdout;
    }

    private double DurationOf(string path) =>
        double.Parse(
            Run(_runtime!.FfprobePath,
                ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path]).Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

    private string CodecOf(string path, string stream) =>
        Run(_runtime!.FfprobePath,
            ["-v", "error", "-select_streams", stream, "-show_entries", "stream=codec_name",
             "-of", "default=nw=1:nk=1", path]).Trim();

    [SkippableFact]
    public void The_installed_ffmpeg_is_located_and_recent_enough()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found. Run tools/fetch-ffmpeg.ps1.");

        Assert.True(File.Exists(_runtime!.FfmpegPath));
        Assert.True(File.Exists(_runtime.FfprobePath));
        Assert.Contains("ffmpeg version", _runtime.Version);

        // The probe must find real encoders and filters, not an empty capability set —
        // an empty set would silently disable the render path rather than fail loudly.
        Assert.True(_runtime.Capabilities.Has("aac"), "aac encoder not detected");

        foreach (var filter in new[] { "crop", "scale", "overlay", "atrim", "concat", "aresample" })
            Assert.True(_runtime.Capabilities.Filters.Contains(filter), $"{filter} filter not detected");
    }

    [SkippableFact]
    public async Task Probing_a_real_file_yields_an_exact_frame_index()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var source = MakeSource("probe.mp4", seconds: 5);
        var result = await new MediaProber(_runtime!).ProbeAsync(source);

        Assert.Equal(640, result.Media.Width);
        Assert.Equal(480, result.Media.Height);
        Assert.True(result.Media.HasAudio);
        Assert.Equal("h264", result.Media.VideoCodec);

        // 5 seconds at 30 fps.
        Assert.InRange(result.Index.FrameCount, 148, 152);

        // Constant rate in, so no VFR flag and evenly spaced timestamps.
        Assert.False(result.Media.IsVariableFrameRate);

        // -g 30 means a keyframe roughly every second.
        Assert.InRange(result.Index.KeyFrames.Length, 4, 8);
        Assert.Equal(0, result.Index.KeyFrames[0]);

        // Timestamps must be strictly ascending, which everything downstream assumes.
        for (var i = 1; i < result.Index.FrameCount; i++)
            Assert.True(result.Index.Pts[i] > result.Index.Pts[i - 1], $"pts not ascending at frame {i}");
    }

    [SkippableFact]
    public async Task A_cut_only_export_is_lossless_and_has_the_expected_duration()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var source = MakeSource("cut.mp4", seconds: 10);
        var probe = await new MediaProber(_runtime!).ProbeAsync(source);

        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(640, 480, Rational.FromInt(30))),
            probe.Media);

        // Remove frames 60-119, i.e. two seconds starting at the two-second mark. Both
        // boundaries are keyframes, so this should take the lossless path.
        project = TimelineEdits.RippleDelete(project, new FrameRange(60, 120));

        var output = Path.Combine(_dir, "out-cut.mp4");
        var plan = ExportPlanner.Plan(
            project,
            new ExportSettings(output),
            _runtime!.Capabilities,
            _ => probe.Index,
            _dir);

        Assert.Equal(ExportMode.LosslessVideo, plan.Mode);

        await new ExportRunner(_runtime).RunAsync(plan, _dir);

        Assert.True(File.Exists(output), "export produced no file");

        // 10 seconds minus the 2 removed.
        Assert.InRange(DurationOf(output), 7.8, 8.2);

        // Video was copied, so the codec is unchanged; audio was deliberately re-encoded.
        Assert.Equal("h264", CodecOf(output, "v:0"));
        Assert.Equal("aac", CodecOf(output, "a:0"));
    }

    [SkippableFact]
    public async Task A_cropped_export_renders_at_the_project_output_size()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var source = MakeSource("crop.mp4", seconds: 6);
        var probe = await new MediaProber(_runtime!).ProbeAsync(source);

        // 640x480 is 4:3, so a 320x240 crop keeps the aspect lock.
        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(640, 480, Rational.FromInt(30))),
            probe.Media);

        project = TimelineEdits.SetCrop(project, new FrameRange(30, 90), new RectI(100, 80, 320, 240));

        var output = Path.Combine(_dir, "out-crop.mp4");
        var plan = ExportPlanner.Plan(
            project, new ExportSettings(output), _runtime!.Capabilities, _ => probe.Index, _dir);

        Assert.Equal(ExportMode.Render, plan.Mode);
        Assert.Equal(LosslessBlocker.HasCrop, plan.Blocker);

        await new ExportRunner(_runtime).RunAsync(plan, _dir);

        Assert.True(File.Exists(output));

        // Zoom-to-fill: the cropped stretch is scaled back up, so the output keeps one
        // constant size throughout.
        var size = Run(_runtime.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
             "-of", "csv=p=0", output]).Trim();

        Assert.Equal("640,480", size);
        Assert.InRange(DurationOf(output), 5.7, 6.3);
    }

    [SkippableFact]
    public async Task An_overlaid_export_renders_and_keeps_the_output_size()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var basePath = MakeSource("base.mp4", seconds: 6);
        var pipPath = MakeSource("pip.mp4", seconds: 6);

        var prober = new MediaProber(_runtime!);
        var baseProbe = await prober.ProbeAsync(basePath);
        var pipProbe = await prober.ProbeAsync(pipPath);

        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(640, 480, Rational.FromInt(30))), baseProbe.Media);
        project = TimelineEdits.ImportSource(project, pipProbe.Media, appendToBase: false);

        // A picture-in-picture over the middle third of the timeline.
        project = TimelineEdits.AddOverlay(project, new OverlayClip(
            new FrameRange(60, 120), SourceId: 2, SourceStartFrame: 0, Dest: new RectI(400, 320, 200, 150)));

        var indices = new Dictionary<int, Core.Media.SourceIndex>
        {
            [1] = baseProbe.Index,
            [2] = pipProbe.Index,
        };

        var output = Path.Combine(_dir, "out-overlay.mp4");
        var plan = ExportPlanner.Plan(
            project, new ExportSettings(output), _runtime!.Capabilities, id => indices[id], _dir);

        Assert.Equal(ExportMode.Render, plan.Mode);
        Assert.Equal(LosslessBlocker.HasOverlay, plan.Blocker);

        // The overlay boundaries split the plan, so only the middle segment carries it.
        Assert.Equal(3, plan.Steps.Count(s => s.Description.StartsWith("Rendering", StringComparison.Ordinal)));

        await new ExportRunner(_runtime).RunAsync(plan, _dir);

        Assert.True(File.Exists(output), "overlay export produced no file");

        var size = Run(_runtime.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
             "-of", "csv=p=0", output]).Trim();

        Assert.Equal("640,480", size);
        Assert.InRange(DurationOf(output), 5.7, 6.3);
        Assert.Equal("aac", CodecOf(output, "a:0"));
    }

    [SkippableFact]
    public async Task A_crop_and_an_overlay_together_export_without_conflicting()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        // Both filters land in one segment's graph, which is where an ordering mistake
        // between crop-then-scale and the overlay would show up.
        var basePath = MakeSource("both-base.mp4", seconds: 5);
        var pipPath = MakeSource("both-pip.mp4", seconds: 5);

        var prober = new MediaProber(_runtime!);
        var baseProbe = await prober.ProbeAsync(basePath);
        var pipProbe = await prober.ProbeAsync(pipPath);

        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(640, 480, Rational.FromInt(30))), baseProbe.Media);
        project = TimelineEdits.ImportSource(project, pipProbe.Media, appendToBase: false);

        project = TimelineEdits.SetCrop(project, new FrameRange(30, 90), new RectI(80, 60, 320, 240));
        project = TimelineEdits.AddOverlay(project, new OverlayClip(
            new FrameRange(30, 90), SourceId: 2, SourceStartFrame: 0, Dest: new RectI(380, 300, 200, 150)));

        var indices = new Dictionary<int, Core.Media.SourceIndex>
        {
            [1] = baseProbe.Index,
            [2] = pipProbe.Index,
        };

        var output = Path.Combine(_dir, "out-both.mp4");
        var plan = ExportPlanner.Plan(
            project, new ExportSettings(output), _runtime!.Capabilities, id => indices[id], _dir);

        await new ExportRunner(_runtime).RunAsync(plan, _dir);

        Assert.True(File.Exists(output));

        var size = Run(_runtime.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
             "-of", "csv=p=0", output]).Trim();

        Assert.Equal("640,480", size);
    }

    [SkippableFact]
    public async Task Export_reports_progress_that_advances_and_completes()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var source = MakeSource("progress.mp4", seconds: 6);
        var probe = await new MediaProber(_runtime!).ProbeAsync(source);

        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(640, 480, Rational.FromInt(30))), probe.Media);
        project = TimelineEdits.RippleDelete(project, new FrameRange(30, 60));

        var output = Path.Combine(_dir, "out-progress.mp4");
        var plan = ExportPlanner.Plan(
            project, new ExportSettings(output), _runtime!.Capabilities, _ => probe.Index, _dir);

        var fractions = new List<double>();
        var progress = new Progress<ExportStatus>(s => fractions.Add(s.Fraction));

        await new ExportRunner(_runtime).RunAsync(plan, _dir, progress);

        Assert.NotEmpty(fractions);
        Assert.All(fractions, f => Assert.InRange(f, 0.0, 1.0));
        Assert.Equal(1.0, fractions[^1], precision: 3);
    }

    [SkippableFact]
    public async Task Temp_files_are_cleaned_up_after_an_export()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var source = MakeSource("temp.mp4", seconds: 4);
        var probe = await new MediaProber(_runtime!).ProbeAsync(source);

        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(640, 480, Rational.FromInt(30))), probe.Media);
        project = TimelineEdits.RippleDelete(project, new FrameRange(30, 60));

        var output = Path.Combine(_dir, "out-temp.mp4");
        var plan = ExportPlanner.Plan(
            project, new ExportSettings(output), _runtime!.Capabilities, _ => probe.Index, _dir);

        await new ExportRunner(_runtime).RunAsync(plan, _dir);

        foreach (var file in plan.TempFiles)
            Assert.False(File.Exists(file), $"left behind {file}");

        Assert.True(File.Exists(output));
    }

    [SkippableFact]
    public async Task The_content_key_is_stable_across_a_copy_but_differs_for_different_content()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var a = MakeSource("key-a.mp4", seconds: 3);
        var b = MakeSource("key-b.mp4", seconds: 5);

        var moved = Path.Combine(_dir, "moved", "renamed.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Copy(a, moved);

        var keyA = await MediaProber.ComputeContentKeyAsync(a);
        var keyMoved = await MediaProber.ComputeContentKeyAsync(moved);
        var keyB = await MediaProber.ComputeContentKeyAsync(b);

        Assert.Equal(keyA, keyMoved);
        Assert.NotEqual(keyA, keyB);
    }
}
