using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Core.Timeline;

namespace BertCut.Core.Tests;

/// <summary>
/// Choosing what an overlay shows, and working out where it can go.
/// </summary>
/// <remarks>
/// The arithmetic behind the three ways of starting an overlay. All of it is pure, so none of
/// it needs the window the interface tests drive — see <c>tools/ui/overlay-place.bcs</c> for
/// the questions that genuinely are about the strip.
/// </remarks>
public class OverlayPlacementTests
{
    /// <summary>
    /// The id the fixtures' first source gets: <c>TimelineEdits.ImportSource</c> assigns ids,
    /// starting at 1, rather than taking the one the builder was handed.
    /// </summary>
    private const int Src = 1;

    // ---- what the three choices show ----

    [Fact]
    public void A_marked_range_reads_the_source_frames_underneath_it()
    {
        var p = TestProjects.Single(300);

        var content = OverlayPlacement.FromTimelineRange(p, new FrameRange(40, 100));

        Assert.NotNull(content);
        Assert.Equal(Src, content!.Value.SourceId);
        Assert.Equal(40, content.Value.SourceStartFrame);
        Assert.Equal(60, content.Value.LengthFrames);
    }

    [Fact]
    public void A_marked_range_reads_through_the_cut_it_lands_in_rather_than_from_the_front()
    {
        // The back half of the source moved to the front: timeline 0 now shows source 150.
        var p = TestProjects.Single(300)
            .WithBase(
                new BaseSegment(TimelineStart: 0, LengthFrames: 150, SourceId: Src, SourceStartFrame: 150),
                new BaseSegment(TimelineStart: 150, LengthFrames: 150, SourceId: Src, SourceStartFrame: 0));

        var content = OverlayPlacement.FromTimelineRange(p, new FrameRange(10, 40));

        Assert.Equal(160, content!.Value.SourceStartFrame);
        Assert.Equal(30, content.Value.LengthFrames);
    }

    [Fact]
    public void A_marked_range_spanning_a_cut_stops_at_the_cut()
    {
        // An overlay clip reads one contiguous run of one source. A range crossing a join
        // cannot be honoured as asked, and the half the user can see is the first one.
        var p = TestProjects.Single(300)
            .WithBase(
                new BaseSegment(0, 100, Src, 200),
                new BaseSegment(100, 100, Src, 0));

        var content = OverlayPlacement.FromTimelineRange(p, new FrameRange(60, 160));

        Assert.Equal(260, content!.Value.SourceStartFrame);
        Assert.Equal(40, content.Value.LengthFrames);
    }

    [Fact]
    public void A_marked_range_cannot_promise_more_frames_than_the_source_has_left()
    {
        // Reading from source frame 280 of a 300-frame file leaves 20, however long the mark is.
        var p = TestProjects.Single(300).WithBase(new BaseSegment(0, 300, Src, 280));

        var content = OverlayPlacement.FromTimelineRange(p, new FrameRange(0, 200));

        Assert.Equal(280, content!.Value.SourceStartFrame);
        Assert.Equal(20, content.Value.LengthFrames);
    }

    [Fact]
    public void A_segment_is_taken_whole_and_needs_no_conversion()
    {
        var p = TestProjects.Single(300)
            .WithBase(
                new BaseSegment(0, 100, Src, 0),
                new BaseSegment(100, 80, Src, 220));

        var content = OverlayPlacement.FromSegment(p, 1);

        Assert.Equal(220, content!.Value.SourceStartFrame);
        Assert.Equal(80, content.Value.LengthFrames);
    }

    [Fact]
    public void A_segment_index_that_no_longer_exists_is_refused_rather_than_thrown()
    {
        var p = TestProjects.Single(300);

        Assert.Null(OverlayPlacement.FromSegment(p, 7));
        Assert.Null(OverlayPlacement.FromSegment(p, -1));
    }

    [Fact]
    public void A_whole_file_is_measured_in_timeline_frames_not_its_own()
    {
        // A 60 fps take on a 30 fps timeline: 400 of its frames occupy 200 of the output's.
        var p = TimelineEdits.ImportSource(
            TestProjects.Single(300), TestProjects.Source(1, 400, fps: 60), appendToBase: false);

        var content = OverlayPlacement.FromWholeSource(p, p.Sources[^1].Id);

        Assert.Equal(0, content!.Value.SourceStartFrame);
        Assert.Equal(200, content.Value.LengthFrames);
    }

    // ---- where it lands ----

    [Fact]
    public void A_clip_starts_at_the_playhead_and_keeps_the_length_that_was_chosen()
    {
        var p = TestProjects.Single(300);
        var content = new OverlayContent(Src, 0, 60);

        Assert.Equal(new FrameRange(40, 100), OverlayPlacement.RangeAt(p, content, 40));
    }

    [Fact]
    public void A_clip_stops_against_the_end_of_the_timeline_rather_than_being_cut_short()
    {
        // The length was settled when the content was chosen. Coming out shorter because the
        // playhead was near the end would break the promise the choice just made.
        var p = TestProjects.Single(300);
        var content = new OverlayContent(Src, 0, 60);

        Assert.Equal(new FrameRange(240, 300), OverlayPlacement.RangeAt(p, content, 280));
    }

    [Fact]
    public void A_clip_stops_against_the_one_in_front_of_it()
    {
        var p = TestProjects.Single(300) with
        {
            Overlays = TestProjects.Arr(new OverlayClip(new FrameRange(200, 260), Src, 0, default)),
        };

        var content = new OverlayContent(Src, 0, 60);

        Assert.Equal(new FrameRange(140, 200), OverlayPlacement.RangeAt(p, content, 180));
    }

    [Fact]
    public void Landing_on_a_clip_puts_the_new_one_after_it()
    {
        var p = TestProjects.Single(300) with
        {
            Overlays = TestProjects.Arr(new OverlayClip(new FrameRange(100, 160), Src, 0, default)),
        };

        var content = new OverlayContent(Src, 0, 60);

        Assert.Equal(new FrameRange(160, 220), OverlayPlacement.RangeAt(p, content, 130));
    }

    [Fact]
    public void A_gap_too_small_to_hold_the_clip_is_the_one_case_that_truncates()
    {
        // Clamping has no answer here — no position fits — so the range is short, and the
        // faint band saying so is the only warning there is.
        var p = TestProjects.Single(300) with
        {
            Overlays = TestProjects.Arr(
                new OverlayClip(new FrameRange(0, 100), Src, 0, default),
                new OverlayClip(new FrameRange(130, 300), Src, 0, default)),
        };

        var content = new OverlayContent(Src, 0, 60);

        Assert.Equal(new FrameRange(100, 130), OverlayPlacement.RangeAt(p, content, 110));
    }

    [Fact]
    public void Placing_never_overlaps_an_existing_clip_so_adding_one_never_truncates_a_neighbour()
    {
        // The property the clamping exists for. AddOverlay truncates whatever it lands on, and
        // that is exactly what a full-length source dropped near an existing clip used to do —
        // eating it on one keypress with nothing on screen beforehand.
        var p = TestProjects.Single(300) with
        {
            Overlays = TestProjects.Arr(new OverlayClip(new FrameRange(150, 210), Src, 0, default)),
        };

        var content = new OverlayContent(Src, 0, 80);

        for (var playhead = 0; playhead < 300; playhead++)
        {
            var range = OverlayPlacement.RangeAt(p, content, playhead);
            if (range.IsEmpty) continue;

            var next = TimelineEdits.AddOverlay(p, new OverlayClip(range, Src, 0, default));

            Assert.Equal(2, next.Overlays.Length);
            Assert.Contains(next.Overlays, o => o.Range == new FrameRange(150, 210));
        }
    }

    [Fact]
    public void Where_it_reads_from_is_settled_by_the_choice_and_never_by_where_it_lands()
    {
        // The whole point of choosing the content first: moving the playhead moves the clip,
        // it does not slide what the clip is showing.
        var p = TestProjects.Single(300);
        var content = new OverlayContent(Src, 120, 60);

        for (var playhead = 0; playhead < 300; playhead++)
        {
            var range = OverlayPlacement.RangeAt(p, content, playhead);
            if (range.IsEmpty) continue;

            Assert.Equal(60, range.Length);
        }
    }

    // ---- the inverse mapping the audio sync needs ----

    [Fact]
    public void A_source_frame_that_was_cut_out_of_the_timeline_shows_nowhere()
    {
        // Not an error: the base track is what survived the cutting, and a sync that snapped
        // to the nearest surviving frame instead would be confidently wrong.
        var p = TestProjects.Single(300).WithBase(new BaseSegment(0, 100, Src, 0));

        Assert.Null(new TimelineResolver(p).TimelineFrameOf(Src, 200));
    }

    [Fact]
    public void A_source_frame_is_found_through_the_segment_that_holds_it()
    {
        var p = TestProjects.Single(300)
            .WithBase(
                new BaseSegment(0, 100, Src, 200),
                new BaseSegment(100, 100, Src, 0));

        Assert.Equal(30, new TimelineResolver(p).TimelineFrameOf(Src, 230));
        Assert.Equal(140, new TimelineResolver(p).TimelineFrameOf(Src, 40));
    }

    [Fact]
    public void A_frame_the_edit_shows_twice_answers_with_the_one_nearest_where_we_are()
    {
        var p = TestProjects.Single(300)
            .WithBase(
                new BaseSegment(0, 50, Src, 0),
                new BaseSegment(50, 50, Src, 0));

        var resolver = new TimelineResolver(p);

        Assert.Equal(10, resolver.TimelineFrameOf(Src, 10, near: 0));
        Assert.Equal(60, resolver.TimelineFrameOf(Src, 10, near: 90));
    }
}
