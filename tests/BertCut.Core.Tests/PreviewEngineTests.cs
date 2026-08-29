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

    private string Render(string name, string filter, int seconds = 4, string size = "320x240")
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
            "-f", "lavfi", "-i", $"color=c=black:size={size}:rate=30:duration={seconds}",
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

    /// <summary>
    /// A 2:1 source — left half green, right half white — at a shape no output here has.
    /// </summary>
    /// <remarks>
    /// The shape is the whole point. Choosing a region out of a clip means seeing the clip's
    /// own frame, and on a source that is already the output's shape a canvas that quietly
    /// stayed the output's looks exactly like one that took the source's.
    /// </remarks>
    private string MakeWideSource() =>
        Render(
            "wide.mp4",
            "geq=r='if(lt(X,W/2),0,255)':g='255':b='if(lt(X,W/2),0,255)'",
            size: "400x200");

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
    public async Task A_whole_timeline_crop_is_composited_at_the_delivered_size()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, engine, _) = await SetUpAsync(MakeSplitSource());
        using var _engine = engine;

        // A tall strip out of the red half. The delivered size is the crop's own, so the
        // shell composites at that shape rather than at the output's.
        var crop = new RectI(0, 0, 80, 240);
        var cropped = TimelineEdits.SetCrop(project, new FrameRange(0, project.DurationFrames), crop);

        Assert.Equal((80, 240), cropped.DeliveredSize);
        engine.SetRenderSize(crop.W, crop.H);

        Assert.True(engine.Render(new TimelineResolver(cropped), 10));

        // Every column is red. Mapping the crop through the canvas rather than through the
        // output would read the ratio off the crop's own effect: 80 canvas columns stepping
        // by source/canvas would sweep the whole 320-pixel frame and bring the blue half in
        // at the right-hand edge.
        foreach (var x in new[] { 2, 40, 78 })
        {
            var (r, _, b) = PixelAt(engine.Canvas, x, 120);
            Assert.True(r > 150 && b < 90, $"x={x} should be red, was ({r},_,{b})");
        }
    }

    [SkippableFact]
    public async Task An_overlay_of_a_region_shows_that_region_rather_than_the_whole_source()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        // A piece of the base laid back over the base — the same source on both layers,
        // which is the case a cut-out region exists for.
        var (project, engine, _) = await SetUpAsync(MakeSplitSource());
        using var _engine = engine;

        var withRegion = TimelineEdits.AddOverlay(project, new OverlayClip(
            new FrameRange(0, 60), SourceId: 1, SourceStartFrame: 0,
            Dest: new RectI(20, 20, 100, 60),
            Src: new RectI(200, 0, 100, 240)));

        Assert.True(engine.Render(new TimelineResolver(withRegion), 10));

        // The region came out of the blue half, and it landed over the red one.
        var (ir, _, ib) = PixelAt(engine.Canvas, 70, 50);
        Assert.True(ib > 150 && ir < 90, $"inside the clip should be the blue region, was ({ir},_,{ib})");

        // Immediately below it, the base is untouched — a cut-out is laid over the picture,
        // not taken out of it.
        var (br, _, bb) = PixelAt(engine.Canvas, 70, 120);
        Assert.True(br > 150 && bb < 90, $"below the clip should still be red, was ({br},_,{bb})");
    }

    /// <summary>
    /// The document the preview stages while a clip's own picture is being cropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built here the way <c>EditorViewModel.ContentOnlyProject</c> builds it, because this is
    /// the half of that mode with any risk in it: a region names a piece of footage, so the
    /// footage has to be on screen at its own shape to choose one out of. The 2:1 source in a
    /// 4:3 project is what makes the difference visible — a canvas that stayed the output's
    /// shape would squeeze the picture, and a clip laid out against the output's delivered
    /// size would leave the base showing round it.
    /// </para>
    /// <para>
    /// The document's own delivered size is asserted unchanged either side. Only the preview's
    /// staged copy moves; a cut-out is laid over the picture, never taken out of it.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_clip_being_cropped_is_staged_at_its_own_shape_and_covers_the_frame()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, engine, _) = await SetUpAsync(MakeSplitSource(), MakeWideSource());
        using var _engine = engine;

        Assert.Equal((320, 240), project.DeliveredSize);

        var content = project.RequireSource(2);
        var staged = project with
        {
            Output = project.Output with { Width = content.Width, Height = content.Height },
            Crops = [],
            Overlays =
            [
                new OverlayClip(
                    new FrameRange(0, 60), content.Id, SourceStartFrame: 0,
                    Dest: new RectI(0, 0, content.Width, content.Height)),
            ],
        };

        Assert.Equal((400, 200), staged.DeliveredSize);

        engine.SetRenderSize(400, 200);
        Assert.True(engine.Render(new TimelineResolver(staged), 10));

        // The clip's own split lands halfway across, which it only can if the canvas is its
        // own frame rather than the output's stretched to hold it.
        var (lr, lg, lb) = PixelAt(engine.Canvas, 100, 100);
        Assert.True(lr < 90 && lg > 150 && lb < 90, $"the left half should be green, was ({lr},{lg},{lb})");

        var (rr, rg, rb) = PixelAt(engine.Canvas, 300, 100);
        Assert.True(rr > 150 && rg > 150 && rb > 150, $"the right half should be white, was ({rr},{rg},{rb})");

        // And the base is nowhere: there is no corner of the frame to draw a region out of
        // that is not the clip's own picture.
        // Both halves of the clip are fully green; neither half of the base has any green in
        // it at all, so one channel answers the whole question.
        foreach (var (x, y) in new[] { (4, 4), (396, 4), (4, 196), (396, 196), (210, 100) })
        {
            var (r, g, b) = PixelAt(engine.Canvas, x, y);
            Assert.True(
                g > 150,
                $"({x},{y}) should be the clip, not the base underneath, was ({r},{g},{b})");
        }

        // The edit itself is untouched.
        Assert.Equal((320, 240), project.DeliveredSize);
        Assert.Empty(project.Overlays);
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

    /// <summary>
    /// A still is written at the delivered size, not at whatever the preview happens to show.
    /// </summary>
    /// <remarks>
    /// This is the whole of the save-a-frame feature. The pump composites at the size the preview
    /// is <i>displayed</i> at — the delivered size over an integer divisor up to four — so saving
    /// the buffer that is on screen would hand back a quarter-resolution still of a 4K recording
    /// and say nothing about it. <c>MainWindow.SaveFrameAsync</c> renders again through a
    /// throwaway engine at <c>DeliveredSize</c> for exactly this reason, which is the path here.
    /// </remarks>
    [SkippableFact]
    public async Task A_saved_frame_is_written_at_the_delivered_size()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, engine, indices) = await SetUpAsync(MakeSplitSource());
        using var _engine = engine;

        // A crop over the whole timeline makes the delivered size the crop's own, so the
        // delivered size and the project output are deliberately different numbers here.
        project = TimelineEdits.SetCrop(
            project, new FrameRange(0, project.DurationFrames), new RectI(20, 10, 200, 120));

        var (width, height) = project.DeliveredSize;
        Assert.Equal((200, 120), (width, height));

        using var still = new PreviewEngine(
            project.Output with { Width = width, Height = height },
            id => indices[id],
            id => project.RequireSource(id).Path);

        Assert.True(still.Render(new TimelineResolver(project), 10));
        Assert.Equal(width, still.Canvas.Width);
        Assert.Equal(height, still.Canvas.Height);

        var path = Path.Combine(_dir, "still.png");
        Png.Save(still.Canvas, path);

        // The PNG says the same size it was rendered at: IHDR carries width and height first.
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(width, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(height, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }
}
