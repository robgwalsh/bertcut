using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Core.Tests;

/// <summary>
/// Moving, trimming and removing an overlay — what the strip's pointer does to a clip.
/// </summary>
/// <remarks>
/// The interesting half of these operations is what they refuse. Every mouse-move during a
/// drag is an edit, so an overlay that could push into its neighbour would consume it a few
/// frames at a time on the way past, and by the time the user let go there would be nothing
/// left to undo back to. Stopping against what is in the way — a neighbour, the ends of the
/// timeline, the end of the clip's own footage — is both the safe behaviour and the only
/// feedback the strip can give that something is there.
/// </remarks>
public class OverlayEditTests
{
    private static Project WithOverlays(params OverlayClip[] clips)
    {
        var p = TestProjects.TwoSources(baseFrames: 1000, overlayFrames: 400);
        return p with { Overlays = TestProjects.Arr(clips) };
    }

    private static OverlayClip Clip(long start, long end, long sourceStart = 0) =>
        new(new FrameRange(start, end), SourceId: 2, sourceStart, new RectI(0, 0, 320, 192));

    [Fact]
    public void Moving_shifts_the_clip_and_keeps_its_length()
    {
        var p = WithOverlays(Clip(100, 200));

        var after = TimelineEdits.SetOverlayStart(p, 0, 500);

        Assert.Equal(new FrameRange(500, 600), after.Overlays[0].Range);
        ProjectInvariants.Check(after);
    }

    /// <summary>
    /// Moving a clip changes when it plays, not what it shows.
    /// </summary>
    /// <remarks>
    /// The counterpart to <c>SetOverlaySourceStart</c>, which changes what it shows and not
    /// when. Dragging a synced overlay somewhere else must not desync it against its own
    /// source — lining it up again is the expensive part of the job.
    /// </remarks>
    [Fact]
    public void Moving_does_not_disturb_where_the_clip_reads_from()
    {
        var p = WithOverlays(Clip(100, 200, sourceStart: 137));

        var after = TimelineEdits.SetOverlayStart(p, 0, 600);

        Assert.Equal(137, after.Overlays[0].SourceStartFrame);
        Assert.Equal(2, after.Overlays[0].SourceId);
        Assert.Equal(p.Overlays[0].Dest, after.Overlays[0].Dest);
    }

    [Fact]
    public void A_clip_cannot_be_dragged_off_the_front_of_the_timeline()
    {
        var p = WithOverlays(Clip(100, 200));

        var after = TimelineEdits.SetOverlayStart(p, 0, -400);

        Assert.Equal(new FrameRange(0, 100), after.Overlays[0].Range);
        ProjectInvariants.Check(after);
    }

    [Fact]
    public void A_clip_cannot_be_dragged_off_the_end_of_the_timeline()
    {
        var p = WithOverlays(Clip(100, 200));

        var after = TimelineEdits.SetOverlayStart(p, 0, 5000);

        Assert.Equal(new FrameRange(900, 1000), after.Overlays[0].Range);
        Assert.Equal(1000, after.DurationFrames);
        ProjectInvariants.Check(after);
    }

    [Fact]
    public void A_clip_stops_against_the_neighbour_ahead_of_it_rather_than_overwriting_it()
    {
        var p = WithOverlays(Clip(100, 200), Clip(400, 500));

        var after = TimelineEdits.SetOverlayStart(p, 0, 450);

        Assert.Equal(new FrameRange(300, 400), after.Overlays[0].Range);
        Assert.Equal(new FrameRange(400, 500), after.Overlays[1].Range);
        ProjectInvariants.Check(after);
    }

    [Fact]
    public void A_clip_stops_against_the_neighbour_behind_it()
    {
        var p = WithOverlays(Clip(100, 200), Clip(400, 500));

        var after = TimelineEdits.SetOverlayStart(p, 1, 50);

        Assert.Equal(new FrameRange(100, 200), after.Overlays[0].Range);
        Assert.Equal(new FrameRange(200, 300), after.Overlays[1].Range);
        ProjectInvariants.Check(after);
    }

    /// <summary>Neither the order of the list nor the count changes, so an index stays valid.</summary>
    [Fact]
    public void Moving_keeps_the_overlay_list_sorted_and_the_same_length()
    {
        var p = WithOverlays(Clip(100, 200), Clip(400, 500), Clip(700, 800));

        var after = TimelineEdits.SetOverlayStart(p, 1, 690);

        Assert.Equal(3, after.Overlays.Length);
        Assert.Equal(new FrameRange(600, 700), after.Overlays[1].Range);
        ProjectInvariants.Check(after);
    }

    [Fact]
    public void A_clip_wedged_between_two_others_does_not_move()
    {
        var p = WithOverlays(Clip(0, 100), Clip(100, 200), Clip(200, 300));

        var after = TimelineEdits.SetOverlayStart(p, 1, 250);

        Assert.Same(p, after);
    }

    [Fact]
    public void Asking_for_where_it_already_is_changes_nothing()
    {
        var p = WithOverlays(Clip(100, 200));

        Assert.Same(p, TimelineEdits.SetOverlayStart(p, 0, 100));
    }

    // ---- trimming ------------------------------------------------------------------

    [Fact]
    public void Trimming_the_front_moves_the_in_point_and_the_content_with_it()
    {
        var p = WithOverlays(Clip(100, 200, sourceStart: 40));

        var after = TimelineEdits.TrimOverlayStart(p, 0, 130);

        Assert.Equal(new FrameRange(130, 200), after.Overlays[0].Range);

        // The 30 frames taken off the front are 30 frames further into the source, so every
        // frame the clip still covers shows exactly what it showed before.
        Assert.Equal(70, after.Overlays[0].SourceStartFrame);
        ProjectInvariants.Check(after);
    }

    [Fact]
    public void Trimming_the_back_moves_only_the_out_point()
    {
        var p = WithOverlays(Clip(100, 200, sourceStart: 40));

        var after = TimelineEdits.TrimOverlayEnd(p, 0, 160);

        Assert.Equal(new FrameRange(100, 160), after.Overlays[0].Range);
        Assert.Equal(40, after.Overlays[0].SourceStartFrame);
        ProjectInvariants.Check(after);
    }

    /// <summary>Dragging the front edge backwards lengthens the clip, up to its own footage.</summary>
    [Fact]
    public void The_front_can_be_pulled_back_no_further_than_the_source_reaches()
    {
        var p = WithOverlays(Clip(100, 200, sourceStart: 25));

        var after = TimelineEdits.TrimOverlayStart(p, 0, 0);

        Assert.Equal(new FrameRange(75, 200), after.Overlays[0].Range);
        Assert.Equal(0, after.Overlays[0].SourceStartFrame);
        ProjectInvariants.Check(after);
    }

    /// <summary>
    /// The back stops where the source runs out — 400 frames of it, read from frame 40.
    /// </summary>
    [Fact]
    public void The_back_can_be_pulled_out_no_further_than_the_source_reaches()
    {
        var p = WithOverlays(Clip(100, 200, sourceStart: 40));

        var after = TimelineEdits.TrimOverlayEnd(p, 0, 900);

        Assert.Equal(new FrameRange(100, 460), after.Overlays[0].Range);
        ProjectInvariants.Check(after);
    }

    /// <summary>
    /// With footage to spare at both ends, so it is the neighbours doing the stopping.
    /// </summary>
    [Fact]
    public void Trimming_stops_against_the_neighbours()
    {
        var p = WithOverlays(Clip(100, 200), Clip(400, 500, sourceStart: 300));

        var back = TimelineEdits.TrimOverlayStart(p, 1, 0);
        Assert.Equal(new FrameRange(200, 500), back.Overlays[1].Range);
        ProjectInvariants.Check(back);

        var forward = TimelineEdits.TrimOverlayEnd(p, 0, 900);
        Assert.Equal(new FrameRange(100, 400), forward.Overlays[0].Range);
        ProjectInvariants.Check(forward);
    }

    [Fact]
    public void An_edge_cannot_be_dragged_through_the_other_one()
    {
        var p = WithOverlays(Clip(100, 200));

        var front = TimelineEdits.TrimOverlayStart(p, 0, 900);
        Assert.Equal(new FrameRange(199, 200), front.Overlays[0].Range);
        ProjectInvariants.Check(front);

        var back = TimelineEdits.TrimOverlayEnd(p, 0, -900);
        Assert.Equal(new FrameRange(100, 101), back.Overlays[0].Range);
        ProjectInvariants.Check(back);
    }

    [Fact]
    public void The_back_cannot_be_pulled_past_the_end_of_the_timeline()
    {
        var p = WithOverlays(Clip(900, 950));

        var after = TimelineEdits.TrimOverlayEnd(p, 0, 1200);

        Assert.Equal(new FrameRange(900, 1000), after.Overlays[0].Range);
        Assert.Equal(1000, after.DurationFrames);
        ProjectInvariants.Check(after);
    }

    /// <summary>
    /// A 60 fps overlay on a 30 fps project spends two of its own frames per timeline frame,
    /// so its limits are half what a naive count would give.
    /// </summary>
    /// <remarks>
    /// The overlay source is usually a second camera, so a source at a different rate from
    /// the project is the ordinary case rather than the exotic one. The resolver already
    /// rescales when it reads the clip; if the trim did not, the last stretch of a clip
    /// pulled to its "limit" would sit past the end of its own footage.
    /// </remarks>
    [Fact]
    public void A_source_at_another_rate_is_counted_in_its_own_frames()
    {
        var p = TimelineEdits.ImportSource(
            TestProjects.Single(1000), TestProjects.Source(0, frames: 400, fps: 60), appendToBase: false);

        p = p with { Overlays = TestProjects.Arr(Clip(100, 200, sourceStart: 0)) };

        // 400 source frames at 60 fps is 200 output frames at 30.
        var after = TimelineEdits.TrimOverlayEnd(p, 0, 900);

        Assert.Equal(new FrameRange(100, 300), after.Overlays[0].Range);
        ProjectInvariants.Check(after);
    }

    // ---- removing ------------------------------------------------------------------

    [Fact]
    public void Removing_takes_out_the_clip_at_that_index_and_leaves_the_rest()
    {
        var p = WithOverlays(Clip(100, 200), Clip(400, 500), Clip(700, 800));

        var after = TimelineEdits.RemoveOverlay(p, 1);

        Assert.Equal(2, after.Overlays.Length);
        Assert.Equal(new FrameRange(100, 200), after.Overlays[0].Range);
        Assert.Equal(new FrameRange(700, 800), after.Overlays[1].Range);
        ProjectInvariants.Check(after);
    }

    /// <summary>
    /// A drag is hundreds of applies and must leave one undo step.
    /// </summary>
    /// <remarks>
    /// The gesture id is what does it — see <see cref="EditorDocument.Apply"/>. Without it,
    /// undoing a drag would walk the clip back across the timeline one mouse-move at a time.
    /// </remarks>
    [Fact]
    public void A_whole_drag_undoes_in_one_step()
    {
        var document = new EditorDocument(WithOverlays(Clip(100, 200)));

        for (var frame = 110; frame <= 400; frame += 10)
        {
            var target = frame;
            document.Apply("Move overlay", p => TimelineEdits.SetOverlayStart(p, 0, target), "overlay-drag-1");
        }

        Assert.Equal(new FrameRange(400, 500), document.Current.Overlays[0].Range);

        document.Undo();

        Assert.Equal(new FrameRange(100, 200), document.Current.Overlays[0].Range);
        Assert.False(document.CanUndo);
    }
}
