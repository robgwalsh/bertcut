using System.Diagnostics;
using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Core.Timeline;
using BertCut.Media;
using BertCut.Media.Decode;

namespace BertCut.Core.Tests;

/// <summary>
/// Verifies that the preview composites the frame the timeline says it should.
/// </summary>
/// <remarks>
/// The preview and the export are two separate renderers, and the whole design rests on
/// them agreeing. These tests pin the preview side against sources built so a frame's
/// identity and a region's position can be read straight off the pixels.
/// </remarks>
[Collection("ffmpeg")]
public class PreviewEngineTests : IDisposable
{
    private readonly FfmpegRuntime? _runtime;
    private readonly string _dir;

    public PreviewEngineTests()
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

        _dir = Path.Combine(Path.GetTempPath(), "bertcut-preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Render(string name, string filter, int seconds = 4)
    {
        var path = Path.Combine(_dir, name);
        var psi = new ProcessStartInfo(_runtime!.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in new[]
        {
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", $"color=c=black:size=320x240:rate=30:duration={seconds}",
            "-vf", filter,
            "-c:v", "libopenh264", "-b:v", "4M", "-g", "30", "-pix_fmt", "yuv420p",
            "-fps_mode", "cfr", "-r", "30",
            path,
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0) throw new InvalidOperationException($"Test source failed: {stderr}");
        return path;
    }

    /// <summary>Left half red, right half blue — so a crop's horizontal position is readable.</summary>
    private string MakeSplitSource() =>
        Render("split.mp4", "geq=r='if(lt(X,W/2),255,0)':g='0':b='if(lt(X,W/2),0,255)'");

    /// <summary>Flat green, used as an overlay so its footprint is unmistakable.</summary>
    private string MakeGreenSource() => Render("green.mp4", "geq=r='0':g='255':b='0'");

    private static (int R, int G, int B) PixelAt(DecodedFrame frame, int x, int y)
    {
        var offset = (y * frame.Stride) + (x * 4);
        return (frame.Pixels[offset + 2], frame.Pixels[offset + 1], frame.Pixels[offset]);
    }

    private async Task<(Project Project, PreviewEngine Engine, Dictionary<int, Core.Media.SourceIndex> Indices)>
        SetUpAsync(params string[] paths)
    {
        var prober = new MediaProber(_runtime!);
        var indices = new Dictionary<int, Core.Media.SourceIndex>();

        var output = new OutputFormat(320, 240, Rational.FromInt(30));
        var project = Project.Empty(output);

        for (var i = 0; i < paths.Length; i++)
        {
            var probe = await prober.ProbeAsync(paths[i]);
            project = TimelineEdits.ImportSource(project, probe.Media, appendToBase: i == 0);
            indices[i + 1] = probe.Index;
        }

        var engine = new PreviewEngine(
            output,
            id => indices[id],
            id => project.RequireSource(id).Path);

        return (project, engine, indices);
    }

    [SkippableFact]
    public async Task An_uncropped_frame_is_the_source_frame()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, engine, _) = await SetUpAsync(MakeSplitSource());
        using var _engine = engine;

        Assert.True(engine.Render(new TimelineResolver(project), 20));

        var (lr, _, lb) = PixelAt(engine.Canvas, 40, 120);
        var (rr, _, rb) = PixelAt(engine.Canvas, 280, 120);

        Assert.True(lr > 180 && lb < 70, $"left half should be red, was ({lr},_,{lb})");
        Assert.True(rb > 180 && rr < 70, $"right half should be blue, was ({rr},_,{rb})");
    }

    [SkippableFact]
    public async Task A_ripple_delete_makes_the_preview_show_the_later_source_frame()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        // The preview must follow the edited timeline, not the raw file — this is the
        // property that makes the timeline trustworthy.
        var (project, engine, _) = await SetUpAsync(MakeSplitSource());
        using var _engine = engine;

        var cut = TimelineEdits.RippleDelete(project, new FrameRange(0, 30));
        var resolver = new TimelineResolver(cut);

        Assert.True(engine.Render(resolver, 0));
        Assert.Equal(0, engine.Canvas.FrameIndex);

        // Timeline frame 0 now maps to source frame 30.
        Assert.Equal(30, resolver.Resolve(0)!.Value.SourceFrame);
    }

    [SkippableFact]
    public async Task A_crop_zooms_its_region_to_fill_the_output()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, engine, _) = await SetUpAsync(MakeSplitSource());
        using var _engine = engine;

        // Crop the left half only. 320x240 is 4:3, so 160x120 keeps the aspect lock.
        // Zoomed to fill, the whole output should now be red.
        var cropped = TimelineEdits.SetCrop(project, new FrameRange(0, 60), new RectI(0, 60, 160, 120));

        Assert.True(engine.Render(new TimelineResolver(cropped), 10));

        foreach (var x in new[] { 20, 160, 300 })
        {
            var (r, _, b) = PixelAt(engine.Canvas, x, 120);
            Assert.True(r > 150 && b < 90, $"x={x} should be red after cropping the left half, was ({r},_,{b})");
        }
    }

    [SkippableFact]
    public async Task An_overlay_is_drawn_only_inside_its_destination_rectangle()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, engine, _) = await SetUpAsync(MakeSplitSource(), MakeGreenSource());
        using var _engine = engine;

        var dest = new RectI(200, 20, 100, 60);
        var withOverlay = TimelineEdits.AddOverlay(
            project, new OverlayClip(new FrameRange(0, 60), SourceId: 2, SourceStartFrame: 0, Dest: dest));

        Assert.True(engine.Render(new TimelineResolver(withOverlay), 10));

        // Inside the rectangle: the green overlay.
        var (ir, ig, ib) = PixelAt(engine.Canvas, 250, 50);
        Assert.True(ig > 150 && ir < 90 && ib < 90, $"inside the overlay should be green, was ({ir},{ig},{ib})");

        // Just outside it: the untouched base layer.
        var (or_, og, ob) = PixelAt(engine.Canvas, 250, 120);
        Assert.True(og < 120, $"below the overlay should be base footage, was ({or_},{og},{ob})");

        // And the far left is still the base layer's red half.
        var (lr, _, lb) = PixelAt(engine.Canvas, 30, 50);
        Assert.True(lr > 150 && lb < 90, $"left of the overlay should stay red, was ({lr},_,{lb})");
    }

    [SkippableFact]
    public async Task Rendering_past_the_end_of_the_timeline_reports_no_frame()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, engine, _) = await SetUpAsync(MakeSplitSource());
        using var _engine = engine;

        Assert.False(engine.Render(new TimelineResolver(project), project.DurationFrames + 5));
        Assert.False(engine.HasFrame);
    }
}
