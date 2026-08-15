using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Core.Timeline;

namespace BertCut.Core.Tests;

/// <summary>
/// Rearranging and removing whole base segments — what the strip's pointer does to a piece
/// of the film itself.
/// </summary>
/// <remarks>
/// The base track is a gapless prefix sum, so a reorder is not a swap of two fields: every
/// position after the moved block changes, and everything addressed by timeline range —
/// crops, overlays — has to be carried across with the picture it was put on. These tests are
/// mostly about that second part, because it is the part that can be silently wrong: nothing
/// about a project with a crop in the wrong place fails to open, it just zooms into somebody
/// else's face.
/// </remarks>
public class SegmentEditTests
{
    /// <summary>Three 100-frame segments of one source, in order.</summary>
    private static Project ThreeSegments()
    {
        var p = TimelineEdits.ImportSource(Project.Empty(TestProjects.Output1280), TestProjects.Source(0, 300));

        return p.WithBase(
            new BaseSegment(0, 100, 1, 0),
            new BaseSegment(100, 100, 1, 100),
            new BaseSegment(200, 100, 1, 200));
    }

    private static long SourceFrameAt(Project p, long frame) =>
        new TimelineResolver(p).Resolve(frame)!.Value.SourceFrame;

    [Fact]
    public void Moving_a_segment_later_puts_the_others_in_front_of_it()
    {
        var after = TimelineEdits.MoveSegment(ThreeSegments(), 0, 2);

        // The old middle and last segments now lead, and the old first one plays last.
        Assert.Equal(100, SourceFrameAt(after, 0));
        Assert.Equal(200, SourceFrameAt(after, 100));
        Assert.Equal(0, SourceFrameAt(after, 200));

        Assert.Equal(300, after.DurationFrames);
        ProjectInvariants.Check(after);
    }

    [Fact]
    public void Moving_a_segment_earlier_is_the_same_thing_backwards()
    {
        var after = TimelineEdits.MoveSegment(ThreeSegments(), 2, 0);

        Assert.Equal(200, SourceFrameAt(after, 0));
        Assert.Equal(0, SourceFrameAt(after, 100));
        Assert.Equal(100, SourceFrameAt(after, 200));
        ProjectInvariants.Check(after);
    }

    /// <summary>
    /// The defining property: a reorder is a permutation of the timeline, so every frame that
    /// was somewhere before is somewhere now, and none of them changed what they show.
    /// </summary>
    [Fact]
    public void Every_frame_survives_a_reorder_and_shows_what_it_showed()
    {
        var before = ThreeSegments();
        var after = TimelineEdits.MoveSegment(before, 1, 0);

        var was = Enumerable.Range(0, (int)before.DurationFrames)
            .Select(t => SourceFrameAt(before, t))
            .OrderBy(f => f);

        var now = Enumerable.Range(0, (int)after.DurationFrames)
            .Select(t => SourceFrameAt(after, t))
            .OrderBy(f => f);

        Assert.Equal(was, now);
    }

    [Fact]
    public void An_overlay_travels_with_the_segment_it_sits_on()
    {
        var p = ThreeSegments();
        p = TimelineEdits.ImportSource(p, TestProjects.Source(0, 400), appendToBase: false);
        p = TimelineEdits.AddOverlay(
            p, new OverlayClip(new FrameRange(120, 180), SourceId: 2, SourceStartFrame: 40, new RectI(0, 0, 320, 192)));

        // The middle segment goes to the front, so the overlay on it goes with it: 20 frames
        // in from the segment's start, wherever that start now is.
        var after = TimelineEdits.MoveSegment(p, 1, 0);

        var moved = Assert.Single(after.Overlays);
        Assert.Equal(new FrameRange(20, 80), moved.Range);
        Assert.Equal(40, moved.SourceStartFrame);
        ProjectInvariants.Check(after);
    }

    [Fact]
    public void A_crop_travels_with_the_segment_it_was_applied_to()
    {
        var p = TimelineEdits.SetCrop(ThreeSegments(), new FrameRange(200, 300), TestProjects.HalfCrop());

        var after = TimelineEdits.MoveSegment(p, 2, 0);

        var crop = Assert.Single(after.Crops);
        Assert.Equal(new FrameRange(0, 100), crop.Range);
        Assert.Equal(TestProjects.HalfCrop(), crop.Rect);
        ProjectInvariants.Check(after);
    }

    /// <summary>
    /// A span across two segments that are being pulled apart is cut in two, and each half
    /// goes on showing what it showed.
    /// </summary>
    [Fact]
    public void A_span_across_a_seam_is_split_and_both_halves_keep_their_content()
    {
        var p = ThreeSegments();
        p = TimelineEdits.ImportSource(p, TestProjects.Source(0, 400), appendToBase: false);
        p = TimelineEdits.AddOverlay(
            p, new OverlayClip(new FrameRange(150, 250), SourceId: 2, SourceStartFrame: 0, new RectI(0, 0, 320, 192)));

        // Segment 1 carries frames 150-200 of the overlay's first 50 frames; segment 2 carries
        // the rest. Moving segment 1 to the front separates them.
        var after = TimelineEdits.MoveSegment(p, 1, 0);

        Assert.Equal(2, after.Overlays.Length);

        // The half that travelled: the overlay's own first 50 frames, now at the front.
        Assert.Equal(new FrameRange(50, 100), after.Overlays[0].Range);
        Assert.Equal(0, after.Overlays[0].SourceStartFrame);

        // The half that stayed on segment 2: still the overlay's frames 50-100.
        Assert.Equal(new FrameRange(200, 250), after.Overlays[1].Range);
        Assert.Equal(50, after.Overlays[1].SourceStartFrame);

        ProjectInvariants.Check(after);
    }

    /// <summary>Two halves of one crop brought back together are one crop again.</summary>
    [Fact]
    public void Crop_halves_that_meet_again_are_merged()
    {
        var p = TimelineEdits.SetCrop(ThreeSegments(), new FrameRange(0, 200), TestProjects.HalfCrop());
        Assert.Single(p.Crops);

        // Swapping the first two segments leaves the crop covering the same two, still whole.
        var after = TimelineEdits.MoveSegment(p, 0, 1);

        var crop = Assert.Single(after.Crops);
        Assert.Equal(new FrameRange(0, 200), crop.Range);
        ProjectInvariants.Check(after);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(-1, 2)]
    [InlineData(3, 0)]
    public void An_impossible_move_changes_nothing(int index, int destination)
    {
        var p = ThreeSegments();
        Assert.Same(p, TimelineEdits.MoveSegment(p, index, destination));
    }

    [Fact]
    public void A_destination_past_the_end_lands_on_the_end()
    {
        var after = TimelineEdits.MoveSegment(ThreeSegments(), 0, 99);

        Assert.Equal(0, SourceFrameAt(after, 200));
        ProjectInvariants.Check(after);
    }

    // ---- removing ------------------------------------------------------------------

    [Fact]
    public void Removing_a_segment_closes_the_gap_behind_it()
    {
        var after = TimelineEdits.RemoveSegment(ThreeSegments(), 1);

        Assert.Equal(200, after.DurationFrames);
        Assert.Equal(0, SourceFrameAt(after, 0));
        Assert.Equal(200, SourceFrameAt(after, 100));
        ProjectInvariants.Check(after);
    }

    /// <summary>
    /// Removing a segment is a ripple delete of its range, so everything that follows one
    /// follows the other — including what happens to the overlays on top.
    /// </summary>
    [Fact]
    public void Removing_a_segment_is_the_ripple_delete_of_its_range()
    {
        var p = ThreeSegments();
        p = TimelineEdits.ImportSource(p, TestProjects.Source(0, 400), appendToBase: false);
        p = TimelineEdits.AddOverlay(
            p, new OverlayClip(new FrameRange(220, 260), SourceId: 2, SourceStartFrame: 0, new RectI(0, 0, 320, 192)));

        Assert.Equal(
            TimelineEdits.RippleDelete(p, new FrameRange(100, 200)),
            TimelineEdits.RemoveSegment(p, 1));
    }

    [Fact]
    public void Removing_the_only_segment_leaves_an_empty_but_valid_project()
    {
        var p = TestProjects.Single(500);

        var after = TimelineEdits.RemoveSegment(p, 0);

        Assert.Equal(0, after.DurationFrames);
        Assert.Empty(after.Base);
        ProjectInvariants.Check(after);
    }

    [Fact]
    public void Removing_something_that_is_not_there_changes_nothing()
    {
        var p = ThreeSegments();

        Assert.Same(p, TimelineEdits.RemoveSegment(p, 3));
        Assert.Same(p, TimelineEdits.RemoveSegment(p, -1));
    }
}
