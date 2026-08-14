using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Core.Timeline;

namespace BertCut.Core.Tests;

/// <summary>
/// The preview compositor asks <see cref="TimelineResolver"/> what a single frame shows;
/// the export argument builder asks <see cref="RenderPlan"/> what a whole span shows.
/// These tests assert the two answers agree for every frame.
/// </summary>
/// <remarks>
/// This is the WYSIWYG contract. Without it the two derivations drift at some rounding
/// boundary, the exported file stops matching the preview, and the editor becomes
/// untrustworthy in a way that is very hard to debug after the fact.
/// </remarks>
public class RenderPlanConformanceTests
{
    [Fact]
    public void Plan_and_resolver_agree_on_every_frame_of_generated_projects()
    {
        // Fixed seed: a failure must be reproducible, and a flaky conformance test would
        // be worse than no test at all.
        var rng = new Random(20260813);

        for (var iteration = 0; iteration < 300; iteration++)
        {
            var project = GenerateProject(rng);
            AssertConformance(project, iteration);
        }
    }

    [Fact]
    public void Plan_covers_the_timeline_exactly_once_with_no_gaps()
    {
        var rng = new Random(7);

        for (var iteration = 0; iteration < 200; iteration++)
        {
            var project = GenerateProject(rng);
            var plan = RenderPlan.Build(project);

            long expected = 0;
            foreach (var segment in plan)
            {
                Assert.Equal(expected, segment.Timeline.Start);
                Assert.True(segment.Timeline.Length > 0, "plan produced an empty segment");
                expected = segment.Timeline.End;
            }

            Assert.Equal(project.DurationFrames, expected);
        }
    }

    [Fact]
    public void Plan_splits_at_crop_boundaries_so_each_segment_has_one_constant_crop()
    {
        var p = TestProjects.Single(600);
        p = TimelineEdits.SetCrop(p, new FrameRange(200, 400), TestProjects.HalfCrop());

        var plan = RenderPlan.Build(p);

        Assert.Equal(3, plan.Length);
        Assert.Null(plan[0].Crop);
        Assert.Equal(TestProjects.HalfCrop(), plan[1].Crop);
        Assert.Equal(new FrameRange(200, 400), plan[1].Timeline);
        Assert.Null(plan[2].Crop);
    }

    private static void AssertConformance(Project project, int iteration)
    {
        var plan = RenderPlan.Build(project);
        var resolver = new TimelineResolver(project);

        foreach (var segment in plan)
        {
            for (var t = segment.Timeline.Start; t < segment.Timeline.End; t++)
            {
                var resolved = resolver.Resolve(t);
                Assert.True(resolved.HasValue, $"iteration {iteration}: frame {t} did not resolve");

                var r = resolved!.Value;
                var offset = t - segment.Timeline.Start;

                Assert.True(
                    segment.SourceId == r.SourceId,
                    $"iteration {iteration}, frame {t}: plan says source {segment.SourceId}, resolver says {r.SourceId}");

                Assert.True(
                    segment.SourceStartFrame + offset == r.SourceFrame,
                    $"iteration {iteration}, frame {t}: plan projects source frame " +
                    $"{segment.SourceStartFrame + offset}, resolver says {r.SourceFrame}");

                Assert.True(
                    segment.Crop == r.Crop,
                    $"iteration {iteration}, frame {t}: plan crop {segment.Crop}, resolver crop {r.Crop}");

                Assert.True(
                    segment.Overlay?.SourceId == r.Overlay?.SourceId,
                    $"iteration {iteration}, frame {t}: overlay source mismatch");

                if (segment.Overlay is not null)
                {
                    Assert.True(
                        segment.OverlaySourceStartFrame + offset == r.OverlaySourceFrame,
                        $"iteration {iteration}, frame {t}: plan projects overlay source frame " +
                        $"{segment.OverlaySourceStartFrame + offset}, resolver says {r.OverlaySourceFrame}");
                }
            }
        }
    }

    /// <summary>
    /// Builds a project by applying a random sequence of real edits, so the generated
    /// documents are ones the app can actually reach rather than hand-assembled shapes.
    /// </summary>
    private static Project GenerateProject(Random rng)
    {
        var p = TimelineEdits.ImportSource(Project.Empty(TestProjects.Output1280), TestProjects.Source(0, 400));
        p = TimelineEdits.ImportSource(p, TestProjects.Source(0, 300), appendToBase: false);

        var operations = rng.Next(1, 9);
        for (var i = 0; i < operations && p.DurationFrames > 8; i++)
        {
            var range = RandomRange(rng, p.DurationFrames);

            switch (rng.Next(4))
            {
                case 0:
                    p = TimelineEdits.RippleDelete(p, range);
                    break;

                case 1:
                    p = TimelineEdits.SetCrop(p, range, RandomAspectLockedCrop(rng, p.Output));
                    break;

                case 2:
                    var overlaySource = p.Sources[rng.Next(p.Sources.Length)];
                    var maxStart = Math.Max(0, overlaySource.FrameCount - range.Length);
                    p = TimelineEdits.AddOverlay(p, new OverlayClip(
                        Range: range,
                        SourceId: overlaySource.Id,
                        SourceStartFrame: maxStart == 0 ? 0 : rng.NextInt64(maxStart),
                        Dest: new RectI(rng.Next(0, 600), rng.Next(0, 400), 320, 192)));
                    break;

                case 3:
                    p = TimelineEdits.ClearCrop(p, range);
                    break;
            }

            ProjectInvariants.Check(p);
        }

        return p;
    }

    private static FrameRange RandomRange(Random rng, long duration)
    {
        var start = rng.NextInt64(0, Math.Max(1, duration - 2));
        var length = rng.NextInt64(1, Math.Max(2, Math.Min(120, duration - start)));
        return FrameRange.FromLength(start, length);
    }

    /// <summary>
    /// Produces a crop matching the output aspect exactly, as the aspect-locked drag
    /// handle in the UI will.
    /// </summary>
    private static RectI RandomAspectLockedCrop(Random rng, OutputFormat output)
    {
        var unitW = output.GcdWidth;
        var unitH = output.GcdHeight;
        var maxScale = Math.Min(output.Width / unitW, output.Height / unitH);
        var scale = rng.Next(Math.Max(1, maxScale / 4), maxScale + 1);

        var w = unitW * scale;
        var h = unitH * scale;
        return new RectI(rng.Next(0, output.Width - w + 1), rng.Next(0, output.Height - h + 1), w, h);
    }
}
