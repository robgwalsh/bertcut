using BertCut.Core.Edits;
using BertCut.Core.Export;
using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Core.Timeline;

namespace BertCut.Core.Tests;

public class ExportPlannerTests
{
    private const string Temp = @"C:\temp\bertcut";

    /// <summary>Position of a flag in an argument list, or -1.</summary>
    private static int Find(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
            if (args[i] == flag) return i;
        return -1;
    }

    /// <summary>The value following a flag.</summary>
    private static string ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var i = Find(args, flag);
        Assert.True(i >= 0 && i + 1 < args.Count, $"{flag} not found with a value");
        return args[i + 1];
    }

    /// <summary>A 30 fps CFR index with a keyframe every 30 frames, as OBS would produce.</summary>
    private static SourceIndex Index(long frames = 1000, int gop = 30)
    {
        var pts = new long[frames];
        for (var i = 0L; i < frames; i++) pts[i] = i * 3000;      // 1/90000 time base at 30 fps

        var keys = new List<int>();
        for (var i = 0; i < frames; i += gop) keys.Add(i);

        return new SourceIndex(new Rational(1, 90000), pts, [.. keys]);
    }

    private static ExportSettings Settings(bool allowLossless = true) =>
        new(@"C:\out\demo.mp4", Quality.Balanced, allowLossless);

    private static ExportPlan PlanFor(Project p, ExportSettings? settings = null, SourceIndex? index = null)
    {
        var idx = index ?? Index();
        return ExportPlanner.Plan(p, settings ?? Settings(), EncoderCapabilities.NvencOnly, _ => idx, Temp);
    }

    // ---- path selection -----------------------------------------------------------

    [Fact]
    public void A_cut_only_edit_on_keyframe_boundaries_takes_the_lossless_path()
    {
        var p = TestProjects.Single(1000);
        p = TimelineEdits.RippleDelete(p, new FrameRange(300, 600));   // both land on keyframes

        var plan = PlanFor(p);

        Assert.Equal(ExportMode.LosslessVideo, plan.Mode);
        Assert.Equal(LosslessBlocker.None, plan.Blocker);
    }

    [Fact]
    public void A_cut_resuming_off_a_keyframe_falls_back_to_rendering()
    {
        // The cut ends at source frame 605, so the second segment would have to start
        // decoding mid-GOP — which a stream copy cannot do.
        var p = TestProjects.Single(1000);
        p = TimelineEdits.RippleDelete(p, new FrameRange(300, 605));

        var plan = PlanFor(p);

        Assert.Equal(ExportMode.Render, plan.Mode);
        Assert.Equal(LosslessBlocker.CutsMissKeyframes, plan.Blocker);
    }

    /// <summary>
    /// Only a segment's <em>start</em> needs to be a keyframe. Its end can fall anywhere,
    /// because truncating after a keyframe leaves a short but fully decodable GOP — so a
    /// cut beginning mid-GOP does not by itself cost the lossless path.
    /// </summary>
    [Fact]
    public void A_cut_beginning_mid_gop_still_allows_a_lossless_export()
    {
        var p = TestProjects.Single(1000);
        p = TimelineEdits.RippleDelete(p, new FrameRange(305, 600));

        var plan = PlanFor(p);

        Assert.Equal(ExportMode.LosslessVideo, plan.Mode);
    }

    [Fact]
    public void A_crop_forces_a_render()
    {
        var p = TestProjects.Single(600);
        p = TimelineEdits.SetCrop(p, new FrameRange(0, 300), TestProjects.HalfCrop());

        var plan = PlanFor(p);

        Assert.Equal(ExportMode.Render, plan.Mode);
        Assert.Equal(LosslessBlocker.HasCrop, plan.Blocker);
    }

    [Fact]
    public void An_overlay_forces_a_render()
    {
        var p = TestProjects.TwoSources();
        p = TimelineEdits.AddOverlay(p, new OverlayClip(new FrameRange(0, 200), 2, 0, new RectI(0, 0, 320, 192)));

        var plan = PlanFor(p);

        Assert.Equal(ExportMode.Render, plan.Mode);
        Assert.Equal(LosslessBlocker.HasOverlay, plan.Blocker);
    }

    [Fact]
    public void The_lossless_path_can_be_turned_off_for_an_exact_cut()
    {
        var p = TimelineEdits.RippleDelete(TestProjects.Single(1000), new FrameRange(300, 600));

        var plan = PlanFor(p, Settings(allowLossless: false));

        Assert.Equal(ExportMode.Render, plan.Mode);
        Assert.Equal(LosslessBlocker.Disabled, plan.Blocker);
    }

    // ---- argument correctness ------------------------------------------------------

    [Fact]
    public void Copy_segments_place_ss_and_to_before_the_input()
    {
        // As input options these are absolute source positions, which is what a cut list
        // describes. After -i, ffmpeg would read -to as an output-timeline duration.
        var args = FfmpegArgs.CopySegment(@"C:\media\a.mp4", 10, 20, @"C:\temp\seg.mp4");

        var ss = Find(args, "-ss");
        var to = Find(args, "-to");
        var i = Find(args, "-i");

        Assert.True(ss >= 0 && to >= 0 && i >= 0);
        Assert.True(ss < i, "-ss must precede -i");
        Assert.True(to < i, "-to must precede -i");
    }

    [Fact]
    public void Copy_segments_copy_video_but_never_audio()
    {
        // AAC frames are 1024 samples and never align to video frames, so a copied audio
        // track would sit up to 20 ms out at every cut, accumulating across cuts.
        var args = FfmpegArgs.CopySegment(@"C:\media\a.mp4", 0, 10, @"C:\temp\seg.mp4");

        Assert.Contains("0:v:0", args);
        Assert.DoesNotContain("0:a:0", args);
        Assert.DoesNotContain("-c:a", args);

        Assert.Equal("copy", ValueAfter(args, "-c:v"));
    }

    [Fact]
    public void Nvenc_always_pairs_cq_with_a_zero_bitrate_target()
    {
        // Without -b:v 0 the encoder's default 2 Mbps target silently overrides -cq and
        // every export comes out at 2 Mbps regardless of the quality setting.
        foreach (var quality in Enum.GetValues<Quality>())
        {
            var p = TimelineEdits.SetCrop(TestProjects.Single(300), new FrameRange(0, 300), TestProjects.HalfCrop());
            var plan = PlanFor(p, new ExportSettings(@"C:\out\demo.mp4", quality));
            var render = plan.Steps[0].Arguments;

            Assert.True(Find(render, "-cq") >= 0, $"{quality}: -cq missing");
            Assert.True(Find(render, "-b:v") >= 0, $"{quality}: -b:v missing");
            Assert.Equal("0", ValueAfter(render, "-b:v"));
        }
    }

    [Fact]
    public void The_audio_graph_restamps_every_span_before_concatenating()
    {
        // Omitting asetpts is the single most common cause of audio drifting after the
        // second cut: concat would see the original timestamps and leave gaps.
        var p = TimelineEdits.RippleDelete(TestProjects.Single(1000), new FrameRange(305, 600));
        var plan = PlanFor(p);

        var audio = plan.Steps.Single(s => s.Description == "Building audio").Arguments;
        var graph = ValueAfter(audio, "-filter_complex");

        var spans = graph.Split("atrim").Length - 1;
        var restamps = graph.Split("asetpts=PTS-STARTPTS").Length - 1;

        Assert.Equal(2, spans);
        Assert.Equal(spans, restamps);
        Assert.Contains("aresample=async=1:first_pts=0", graph);
    }

    [Fact]
    public void Audio_is_built_in_one_pass_over_the_whole_timeline()
    {
        var p = TimelineEdits.RippleDelete(TestProjects.Single(1000), new FrameRange(300, 600));

        var plan = PlanFor(p);

        Assert.Single(plan.Steps, s => s.Description == "Building audio");
    }

    [Fact]
    public void A_crop_boundary_splits_the_video_plan_but_not_the_audio()
    {
        // The crop changes pixels, not sound, so splitting audio there would add a join —
        // and every join is a potential click — for no reason.
        var p = TimelineEdits.SetCrop(TestProjects.Single(600), new FrameRange(200, 400), TestProjects.HalfCrop());
        var flat = RenderPlan.Build(p);

        var spans = ExportPlanner.BuildAudioSpans(p, flat, _ => Index(), out var inputs);

        Assert.Equal(3, flat.Length);
        Assert.Single(spans);
        Assert.Single(inputs);
    }

    [Fact]
    public void Crop_scales_back_to_the_output_size_with_no_pad_filter()
    {
        // Crop rects are aspect-locked to the output, so zoom-to-fill never letterboxes.
        var segment = new FlatSegment(
            new FrameRange(0, 100), SourceId: 1, SourceStartFrame: 0,
            Crop: new RectI(100, 60, 640, 384), Overlay: null, OverlaySourceStartFrame: 0);

        var graph = FfmpegArgs.BuildFilterGraph(segment, TestProjects.Output1280, hasOverlay: false);

        Assert.Contains("crop=640:384:100:60", graph);
        Assert.Contains("scale=1280:768", graph);
        Assert.DoesNotContain("pad=", graph);
    }

    [Fact]
    public void Overlay_passes_through_when_its_source_runs_out()
    {
        // The default eof_action repeats the overlay's last frame, which reads as a freeze.
        var segment = new FlatSegment(
            new FrameRange(0, 100), SourceId: 1, SourceStartFrame: 0,
            Crop: null,
            Overlay: new OverlayClip(new FrameRange(0, 100), 2, 0, new RectI(940, 540, 320, 192)),
            OverlaySourceStartFrame: 0);

        var graph = FfmpegArgs.BuildFilterGraph(segment, TestProjects.Output1280, hasOverlay: true);

        Assert.Contains("overlay=x=940:y=540:eof_action=pass", graph);
        Assert.Contains("scale=320:192", graph);
    }

    [Fact]
    public void Every_step_reports_progress_in_a_parseable_form()
    {
        var p = TimelineEdits.RippleDelete(TestProjects.Single(1000), new FrameRange(300, 600));
        var plan = PlanFor(p);

        foreach (var step in plan.Steps)
        {
            Assert.Contains("-progress", step.Arguments);
            Assert.Contains("-nostats", step.Arguments);

            // ffmpeg would otherwise consume the host's stdin.
            Assert.Contains("-nostdin", step.Arguments);
        }
    }

    [Fact]
    public void Source_times_come_from_the_frame_index_so_vfr_sources_stay_exact()
    {
        // A source whose frames are unevenly spaced: computing seconds as frame/fps here
        // would put the cut in the wrong place.
        var pts = new long[600];
        for (var i = 0; i < 600; i++) pts[i] = (i * 3000) + (i % 7 * 137);
        var vfr = new SourceIndex(new Rational(1, 90000), pts, [0, 300]);

        var p = TimelineEdits.RippleDelete(TestProjects.Single(600), new FrameRange(0, 300));
        var plan = PlanFor(p, index: vfr);

        var copy = plan.Steps[0].Arguments;
        var start = double.Parse(ValueAfter(copy, "-ss"), System.Globalization.CultureInfo.InvariantCulture);

        // Frame 300's actual timestamp, not 300/30 = 10.0.
        Assert.Equal(pts[300] / 90000.0, start, precision: 6);
    }

    [Fact]
    public void Concat_list_files_quote_paths_and_allow_absolute_windows_paths()
    {
        var list = FfmpegArgs.ConcatListFile([@"C:\temp\seg0000.mp4", @"C:\temp\seg0001.mp4"]);

        Assert.StartsWith("ffconcat version 1.0", list);
        Assert.Contains(@"file 'C:\temp\seg0000.mp4'", list);

        var args = FfmpegArgs.ConcatSegments(@"C:\temp\segments.txt", @"C:\temp\video.mp4");
        Assert.Equal("0", ValueAfter(args, "-safe"));
    }

    [Fact]
    public void Encoder_selection_prefers_hardware_and_falls_back_when_absent()
    {
        Assert.Equal(VideoEncoder.H264Nvenc, EncoderCapabilities.NvencOnly.SelectVideoEncoder());

        var cpuOnly = new EncoderCapabilities(
            new HashSet<string> { "libopenh264", "aac" }, new HashSet<string>(), HasCudaHwaccel: false);
        Assert.Equal(VideoEncoder.Libopenh264, cpuOnly.SelectVideoEncoder());

        var nothing = new EncoderCapabilities(new HashSet<string>(), new HashSet<string>(), false);
        Assert.Throws<InvalidOperationException>(() => nothing.SelectVideoEncoder());
    }

    [Fact]
    public void Snapped_boundaries_are_reported_so_the_ui_can_show_ghost_marks()
    {
        // The second segment wants source frame 605; a copy cut would actually resume at
        // keyframe 600, so the UI can warn that 5 extra frames would come along.
        var p = TimelineEdits.RippleDelete(TestProjects.Single(1000), new FrameRange(300, 605));

        var plan = PlanFor(p);

        Assert.Equal([0L, 600L], plan.KeyframeSnappedBoundaries);
    }
}
