using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Core.Timeline;

namespace BertCut.Core.Tests;

/// <summary>
/// Ripple delete is the operation the whole editor is built around and the one that
/// rewrites the most state, so it gets the heaviest coverage.
/// </summary>
public class RippleDeleteTests
{
    [Theory]
    [InlineData(0, 100)]      // from the very start
    [InlineData(100, 300)]    // from the middle
    [InlineData(900, 1000)]   // to the very end
    [InlineData(0, 1000)]     // everything
    public void Duration_shrinks_by_exactly_the_cut_length(long start, long end)
    {
        var p = TestProjects.Single(1000);
        var before = p.DurationFrames;

        var after = TimelineEdits.RippleDelete(p, new FrameRange(start, end));

        Assert.Equal(before - (end - start), after.DurationFrames);
        ProjectInvariants.Check(after);
    }

    /// <summary>
    /// The defining property: after removing [a,b), everything that used to be at p+(b-a)
    /// is now at p. If this holds for every frame, the cut landed in exactly the right
    /// place and nothing downstream slipped.
    /// </summary>
    [Fact]
    public void Frames_after_the_cut_shift_left_by_the_cut_length()
    {
        var p = TestProjects.Single(1000);
        var cut = new FrameRange(250, 400);

        var beforeResolver = new TimelineResolver(p);
        var after = TimelineEdits.RippleDelete(p, cut);
        var afterResolver = new TimelineResolver(after);

        for (var t = 0L; t < after.DurationFrames; t++)
        {
            var expectedOriginal = t < cut.Start ? t : t + cut.Length;

            var actual = afterResolver.Resolve(t);
            var expected = beforeResolver.Resolve(expectedOriginal);

            Assert.NotNull(actual);
            Assert.NotNull(expected);
            Assert.Equal(expected!.Value.SourceFrame, actual!.Value.SourceFrame);
            Assert.Equal(expected.Value.SourceId, actual.Value.SourceId);
        }
    }

    [Fact]
    public void Cutting_everything_leaves_an_empty_but_valid_project()
    {
        var p = TestProjects.Single(500);

        var after = TimelineEdits.RippleDelete(p, new FrameRange(0, 500));

        Assert.Equal(0, after.DurationFrames);
        Assert.Empty(after.Base);
        ProjectInvariants.Check(after);
    }

    [Fact]
    public void An_empty_cut_returns_the_project_unchanged()
    {
        var p = TestProjects.Single(500);

        Assert.Equal(p, TimelineEdits.RippleDelete(p, new FrameRange(200, 200)));
        Assert.Equal(p, TimelineEdits.RippleDelete(p, new FrameRange(600, 900)));
    }

    [Fact]
    public void A_cut_past_the_end_is_clamped_to_the_timeline()
    {
        var p = TestProjects.Single(500);

        var after = TimelineEdits.RippleDelete(p, new FrameRange(400, 9999));

        Assert.Equal(400, after.DurationFrames);
        ProjectInvariants.Check(after);
    }

    [Fact]
    public void Successive_cuts_compose()
    {
        var p = TestProjects.Single(1000);

        // Each cut is expressed against the timeline as it stands after the previous one,
        // which is what the UI does — the user marks in/out on what they can see.
        var after = TimelineEdits.RippleDelete(p, new FrameRange(100, 200));   // 1000 -> 900
        after = TimelineEdits.RippleDelete(after, new FrameRange(100, 200));   // 900  -> 800
        after = TimelineEdits.RippleDelete(after, new FrameRange(0, 50));      // 800  -> 750

        Assert.Equal(750, after.DurationFrames);
        ProjectInvariants.Check(after);

        var resolver = new TimelineResolver(after);

        // The final cut took the first 50 frames of the timeline, which were still source
        // frames 0-49 — the two earlier cuts only removed content further along. So frame
        // 0 now shows source frame 50.
        Assert.Equal(50, resolver.Resolve(0)!.Value.SourceFrame);

        // Frame 50 sits just past where the two earlier cuts landed (old timeline 100,
        // which had already lost source frames 100-299), so it shows source frame 300.
        Assert.Equal(300, resolver.Resolve(50)!.Value.SourceFrame);
    }

    [Fact]
    public void Base_track_stays_gapless_across_many_cuts()
    {
        var p = TestProjects.Single(2000);

        for (var i = 0; i < 20; i++)
        {
            p = TimelineEdits.RippleDelete(p, new FrameRange(10, 40));
            ProjectInvariants.Check(p);
        }

        Assert.Equal(2000 - (20 * 30), p.DurationFrames);
    }
}
