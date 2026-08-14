using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Core.Tests;

/// <summary>
/// The exhaustive case table for span rippling. Every way a crop or overlay can sit
/// relative to a cut gets an explicit assertion, because a span that is silently dropped,
/// left at a stale position, or split with the wrong source offset produces a defect that
/// only shows up in an exported file.
/// </summary>
public class SpanRippleTests
{
    // The cut is [200, 300) throughout, so it removes 100 frames.
    private static readonly FrameRange Cut = new(200, 300);

    private static ImmutableArrayOfCrops RippleCrops(params FrameRange[] ranges)
    {
        var crops = TestProjects.Arr(ranges.Select(r => new CropSpan(r, TestProjects.HalfCrop())).ToArray());
        var result = SpanRipple.Apply(
            crops, Cut,
            static c => c.Range,
            static (c, r, _) => c with { Range = r });
        return new ImmutableArrayOfCrops(result);
    }

    [Fact]
    public void Span_entirely_before_the_cut_is_untouched()
    {
        var result = RippleCrops(new FrameRange(50, 150));
        Assert.Equal([new FrameRange(50, 150)], result.Ranges);
    }

    [Fact]
    public void Span_ending_exactly_at_the_cut_start_is_untouched()
    {
        var result = RippleCrops(new FrameRange(50, 200));
        Assert.Equal([new FrameRange(50, 200)], result.Ranges);
    }

    [Fact]
    public void Span_entirely_inside_the_cut_is_dropped()
    {
        var result = RippleCrops(new FrameRange(220, 280));
        Assert.Empty(result.Ranges);
    }

    [Fact]
    public void Span_exactly_matching_the_cut_is_dropped()
    {
        var result = RippleCrops(Cut);
        Assert.Empty(result.Ranges);
    }

    [Fact]
    public void Span_entirely_after_the_cut_shifts_left_by_the_cut_length()
    {
        var result = RippleCrops(new FrameRange(400, 500));
        Assert.Equal([new FrameRange(300, 400)], result.Ranges);
    }

    [Fact]
    public void Span_starting_exactly_at_the_cut_end_shifts_left()
    {
        var result = RippleCrops(new FrameRange(300, 400));
        Assert.Equal([new FrameRange(200, 300)], result.Ranges);
    }

    [Fact]
    public void Span_overhanging_the_cut_start_is_truncated_at_the_cut()
    {
        var result = RippleCrops(new FrameRange(150, 250));
        Assert.Equal([new FrameRange(150, 200)], result.Ranges);
    }

    [Fact]
    public void Span_overhanging_the_cut_end_moves_to_the_cut_start()
    {
        var result = RippleCrops(new FrameRange(250, 400));
        Assert.Equal([new FrameRange(200, 300)], result.Ranges);
    }

    [Fact]
    public void Span_straddling_the_whole_cut_splits_into_adjacent_head_and_tail()
    {
        // The span keeps [150,200) and [300,400); the latter slides left onto the former's
        // end, so 250 frames of span become 150 covering [150,300).
        var result = RippleCrops(new FrameRange(150, 400));
        Assert.Equal([new FrameRange(150, 200), new FrameRange(200, 300)], result.Ranges);
    }

    /// <summary>
    /// An overlay straddling a cut must split rather than join: its tail resumes at a
    /// later point in its own source, so collapsing the halves would slide the overlaid
    /// footage by the length of the cut.
    /// </summary>
    [Fact]
    public void Straddling_overlay_splits_and_its_tail_advances_its_source_offset()
    {
        var clip = new OverlayClip(
            Range: new FrameRange(150, 400),
            SourceId: 2,
            SourceStartFrame: 1000,
            Dest: new RectI(0, 0, 320, 192));

        var result = SpanRipple.Apply(
            TestProjects.Arr(clip), Cut,
            static o => o.Range,
            static (o, r, consumed) => o with { Range = r, SourceStartFrame = o.SourceStartFrame + consumed });

        Assert.Equal(2, result.Length);

        // Head keeps the original source offset.
        Assert.Equal(new FrameRange(150, 200), result[0].Range);
        Assert.Equal(1000, result[0].SourceStartFrame);

        // Tail sits immediately after the head. It originally began at timeline 300, which
        // was 150 frames into the clip, so its source offset advances by 150 — and this is
        // exactly why the halves cannot be joined: the tail shows different footage.
        Assert.Equal(new FrameRange(200, 300), result[1].Range);
        Assert.Equal(1150, result[1].SourceStartFrame);
    }

    [Fact]
    public void Overlay_overhanging_the_cut_end_advances_its_source_offset()
    {
        var clip = new OverlayClip(
            Range: new FrameRange(250, 400),
            SourceId: 2,
            SourceStartFrame: 500,
            Dest: new RectI(0, 0, 320, 192));

        var result = SpanRipple.Apply(
            TestProjects.Arr(clip), Cut,
            static o => o.Range,
            static (o, r, consumed) => o with { Range = r, SourceStartFrame = o.SourceStartFrame + consumed });

        var only = Assert.Single(result);
        Assert.Equal(new FrameRange(200, 300), only.Range);

        // The clip lost its first 50 frames (timeline 250-300 fell inside the cut).
        Assert.Equal(550, only.SourceStartFrame);
    }

    [Fact]
    public void Overlay_entirely_after_the_cut_keeps_its_source_offset()
    {
        var clip = new OverlayClip(new FrameRange(400, 500), 2, 500, new RectI(0, 0, 320, 192));

        var result = SpanRipple.Apply(
            TestProjects.Arr(clip), Cut,
            static o => o.Range,
            static (o, r, consumed) => o with { Range = r, SourceStartFrame = o.SourceStartFrame + consumed });

        var only = Assert.Single(result);
        Assert.Equal(new FrameRange(300, 400), only.Range);
        Assert.Equal(500, only.SourceStartFrame);   // shifted in time, same content
    }

    [Fact]
    public void Multiple_spans_are_all_handled_in_one_pass()
    {
        var result = RippleCrops(
            new FrameRange(0, 100),      // before  -> unchanged
            new FrameRange(220, 260),    // inside  -> dropped
            new FrameRange(350, 400));   // after   -> shifted

        Assert.Equal([new FrameRange(0, 100), new FrameRange(250, 300)], result.Ranges);
    }

    private readonly record struct ImmutableArrayOfCrops(System.Collections.Immutable.ImmutableArray<CropSpan> Value)
    {
        public FrameRange[] Ranges => Value.Select(c => c.Range).ToArray();
    }
}
