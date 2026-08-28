using BertCut.Core.Model;

namespace BertCut.Core.Tests;

/// <summary>
/// Placement geometry for crop and overlay rectangles.
/// </summary>
/// <remarks>
/// <para>
/// The aspect lock is not cosmetic, and it is the <b>overlay's</b>: a clip's box is held to
/// the ratio of the picture going into it, so what lands is never stretched. Approximating
/// the ratio is not enough — a rounded height leaves a sliver of stretch that a run of
/// resizes accumulates — so every locked operation here preserves it exactly. The tests that
/// pass the frame's own dimensions as the ratio are exercising that path.
/// </para>
/// <para>
/// A crop asks for the free shape instead, and its rules are the frame, the minimum size and
/// an even edge. Both halves are held in place below.
/// </para>
/// </remarks>
public class RectPlacementTests
{
    private const int FrameW = 1280;
    private const int FrameH = 768;

    private static void AssertMatchesOutputAspect(RectI rect)
    {
        // The same cross-multiplied check ProjectInvariants applies.
        Assert.True(
            (long)rect.W * FrameH == (long)rect.H * FrameW,
            $"{rect} does not match the {FrameW}x{FrameH} aspect ratio");
    }

    private static void AssertInsideFrame(RectI rect)
    {
        Assert.True(rect.X >= 0 && rect.Y >= 0, $"{rect} starts outside the frame");
        Assert.True(rect.Right <= FrameW && rect.Bottom <= FrameH, $"{rect} extends past the frame");
        Assert.True(rect.W > 0 && rect.H > 0, $"{rect} is degenerate");
    }

    [Fact]
    public void An_initial_crop_matches_the_output_aspect_exactly()
    {
        var rect = RectPlacement.Initial(FrameW, FrameH, FrameW, FrameH, 0.6, Anchor.Centre);

        AssertMatchesOutputAspect(rect);
        AssertInsideFrame(rect);
    }

    [Fact]
    public void A_crop_produced_by_placement_satisfies_the_project_invariant()
    {
        // The end-to-end check: geometry from this class must be acceptable to the model.
        var rect = RectPlacement.Initial(FrameW, FrameH, FrameW, FrameH, 0.45, Anchor.Centre);
        var p = TestProjects.Single(300);

        var cropped = Edits.TimelineEdits.SetCrop(p, new Time.FrameRange(0, 100), rect);

        Assert.Null(ProjectInvariants.Validate(cropped));
    }

    [Theory]
    [InlineData(1.1)]
    [InlineData(1.5)]
    [InlineData(0.9)]
    [InlineData(0.5)]
    public void Resizing_preserves_the_aspect_ratio(double factor)
    {
        var rect = RectPlacement.Initial(FrameW, FrameH, FrameW, FrameH, 0.6, Anchor.Centre);

        var resized = RectPlacement.Resize(rect, factor, FrameW, FrameH);

        AssertMatchesOutputAspect(resized);
        AssertInsideFrame(resized);
    }

    [Fact]
    public void Repeated_resizing_never_escapes_the_frame_or_collapses()
    {
        var rect = RectPlacement.Initial(FrameW, FrameH, FrameW, FrameH, 0.6, Anchor.Centre);

        // Holding Shift+Up is a real gesture; it must saturate rather than misbehave.
        for (var i = 0; i < 40; i++)
        {
            rect = RectPlacement.Resize(rect, 1.1, FrameW, FrameH);
            AssertInsideFrame(rect);
        }

        for (var i = 0; i < 40; i++)
        {
            rect = RectPlacement.Resize(rect, 1 / 1.1, FrameW, FrameH);
            AssertInsideFrame(rect);
            Assert.True(rect.W >= 2 && rect.H >= 2);
        }
    }

    [Fact]
    public void Resizing_keeps_the_rectangle_centred_on_its_region()
    {
        // Growing about the centre is what makes repeated presses read as zooming rather
        // than as dragging the box toward a corner.
        var rect = new RectI(400, 240, 320, 192);
        var centreX = rect.X + (rect.W / 2);
        var centreY = rect.Y + (rect.H / 2);

        var grown = RectPlacement.Resize(rect, 1.2, FrameW, FrameH);

        Assert.InRange(grown.X + (grown.W / 2), centreX - 2, centreX + 2);
        Assert.InRange(grown.Y + (grown.H / 2), centreY - 2, centreY + 2);
    }

    [Fact]
    public void Moving_clamps_at_the_frame_edges()
    {
        var rect = new RectI(0, 0, 320, 192);

        Assert.Equal(0, RectPlacement.Move(rect, -100, -100, FrameW, FrameH).X);
        Assert.Equal(0, RectPlacement.Move(rect, -100, -100, FrameW, FrameH).Y);

        var pushedRight = RectPlacement.Move(rect, 10_000, 10_000, FrameW, FrameH);
        Assert.Equal(FrameW - rect.W, pushedRight.X);
        Assert.Equal(FrameH - rect.H, pushedRight.Y);
    }

    [Theory]
    [InlineData(Anchor.TopLeft)]
    [InlineData(Anchor.TopRight)]
    [InlineData(Anchor.BottomLeft)]
    [InlineData(Anchor.BottomRight)]
    [InlineData(Anchor.Centre)]
    public void Snapping_lands_inside_the_frame(Anchor anchor)
    {
        var rect = new RectI(500, 300, 320, 192);

        var snapped = RectPlacement.Snap(rect, FrameW, FrameH, anchor, margin: 24);

        AssertInsideFrame(snapped);
        Assert.Equal(rect.W, snapped.W);
        Assert.Equal(rect.H, snapped.H);
    }

    [Fact]
    public void Snapping_to_opposite_corners_produces_opposite_positions()
    {
        var rect = new RectI(0, 0, 320, 192);

        var topLeft = RectPlacement.Snap(rect, FrameW, FrameH, Anchor.TopLeft, 24);
        var bottomRight = RectPlacement.Snap(rect, FrameW, FrameH, Anchor.BottomRight, 24);

        Assert.Equal(24, topLeft.X);
        Assert.Equal(24, topLeft.Y);
        Assert.Equal(FrameW - 320 - 24, bottomRight.X);
        Assert.Equal(FrameH - 192 - 24, bottomRight.Y);
    }

    [Fact]
    public void A_freehand_drag_is_corrected_to_the_locked_aspect()
    {
        // A drag describes a region of interest; the ratio is not negotiable.
        var rect = RectPlacement.FromDrag(200, 100, 700, 200, FrameW, FrameH, FrameW, FrameH);

        AssertMatchesOutputAspect(rect);
        AssertInsideFrame(rect);
    }

    [Fact]
    public void A_backwards_drag_produces_the_same_rectangle_as_a_forwards_one()
    {
        var forwards = RectPlacement.FromDrag(200, 100, 700, 400, FrameW, FrameH, FrameW, FrameH);
        var backwards = RectPlacement.FromDrag(700, 400, 200, 100, FrameW, FrameH, FrameW, FrameH);

        Assert.Equal(forwards, backwards);
    }

    [Fact]
    public void A_degenerate_drag_still_yields_a_usable_rectangle()
    {
        // A stray click during placement must not produce a zero-sized crop.
        var rect = RectPlacement.FromDrag(640, 384, 640, 384, FrameW, FrameH, FrameW, FrameH);

        AssertInsideFrame(rect);
        Assert.True(rect.W >= RectPlacement.MinimumSize - 2);
    }

    [Fact]
    public void Dragging_a_corner_keeps_the_opposite_one_where_it_was()
    {
        // The whole point of a corner handle: the corner you are not holding stays put.
        var rect = new RectI(200, 120, 640, 384);

        var resized = RectPlacement.FromCorner(
            rect.Right, rect.Bottom, 400, 240, FrameW, FrameH, FrameW, FrameH);

        Assert.Equal(rect.Right, resized.Right);
        Assert.Equal(rect.Bottom, resized.Bottom);
        AssertMatchesOutputAspect(resized);
        AssertInsideFrame(resized);
    }

    [Fact]
    public void Dragging_a_corner_past_its_anchor_flips_the_rectangle_rather_than_inverting_it()
    {
        var resized = RectPlacement.FromCorner(
            640, 384, 200, 100, FrameW, FrameH, FrameW, FrameH);

        Assert.True(resized.W > 0 && resized.H > 0);
        Assert.Equal(640, resized.Right);
        Assert.Equal(384, resized.Bottom);
        AssertMatchesOutputAspect(resized);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1279, 767)]
    [InlineData(2000, 2000)]
    [InlineData(-500, -500)]
    public void A_corner_drag_stays_inside_the_frame_and_keeps_the_ratio(int x, int y)
    {
        var resized = RectPlacement.FromCorner(400, 240, x, y, FrameW, FrameH, FrameW, FrameH);

        AssertInsideFrame(resized);
        AssertMatchesOutputAspect(resized);
    }

    [Fact]
    public void Dimensions_are_always_even_for_chroma_subsampling()
    {
        // ffmpeg's crop filter fails outright on an odd dimension with yuv420p.
        for (var target = 33; target < 1200; target += 37)
        {
            var (w, h) = RectPlacement.FitAspect(target, FrameW, FrameH, FrameW, FrameH);
            Assert.Equal(0, w % 2);
            Assert.Equal(0, h % 2);
        }
    }

    [Fact]
    public void An_overlay_rectangle_follows_its_own_source_aspect_not_the_output()
    {
        // A 16:9 webcam over a 5:3 screen recording must not be stretched.
        var rect = RectPlacement.Initial(FrameW, FrameH, 1920, 1080, 0.3, Anchor.BottomRight);

        AssertInsideFrame(rect);
        Assert.InRange((double)rect.W / rect.H, 16.0 / 9 - 0.03, (16.0 / 9) + 0.03);
    }

    [Fact]
    public void A_rectangle_larger_than_the_frame_is_fitted_rather_than_clipped()
    {
        var (w, h) = RectPlacement.FitAspect(99_999, FrameW, FrameH, FrameW, FrameH);

        Assert.True(w <= FrameW && h <= FrameH);
        AssertMatchesOutputAspect(new RectI(0, 0, w, h));
    }

    // ---- the free shape a crop asks for --------------------------------------------

    [Fact]
    public void A_free_drag_keeps_both_of_the_edges_that_were_drawn()
    {
        // The locked path answers a 500x100 drag with a 5:3 rectangle. Free, the drag is
        // the answer, which is the whole of the feature.
        var rect = RectPlacement.FromDrag(200, 100, 700, 200, 0, 0, FrameW, FrameH);

        Assert.Equal(new RectI(200, 100, 500, 100), rect);
        AssertInsideFrame(rect);
    }

    [Theory]
    [InlineData(201, 101, 703, 205)]
    [InlineData(0, 0, 1279, 767)]
    [InlineData(640, 384, 641, 385)]
    public void A_free_rectangle_always_has_even_edges(int x0, int y0, int x1, int y1)
    {
        // ffmpeg's crop filter fails outright on an odd dimension with yuv420p, and a
        // whole-timeline crop's size goes on to be the delivered one.
        var dragged = RectPlacement.FromDrag(x0, y0, x1, y1, 0, 0, FrameW, FrameH);
        var cornered = RectPlacement.FromCorner(x0, y0, x1, y1, 0, 0, FrameW, FrameH);

        Assert.Equal(0, dragged.W % 2);
        Assert.Equal(0, dragged.H % 2);
        Assert.Equal(0, cornered.W % 2);
        Assert.Equal(0, cornered.H % 2);
        AssertInsideFrame(dragged);
        AssertInsideFrame(cornered);
    }

    [Fact]
    public void A_free_corner_drag_reshapes_one_edge_without_touching_the_other()
    {
        // The one gesture that can change a crop's ratio at all: the keyboard has grow and
        // shrink, which move both edges together.
        var cornered = RectPlacement.FromCorner(200, 120, 900, 200, 0, 0, FrameW, FrameH);

        Assert.Equal(new RectI(200, 120, 700, 80), cornered);
    }

    [Fact]
    public void A_free_rectangle_produced_by_placement_satisfies_the_project_invariant()
    {
        // The end-to-end check: a shape the output is not must still be acceptable to the
        // model, which is what the aspect lock used to prevent.
        var rect = RectPlacement.FromDrag(100, 50, 420, 700, 0, 0, FrameW, FrameH);
        var p = TestProjects.Single(300);

        var cropped = Edits.TimelineEdits.SetCrop(p, new Time.FrameRange(0, 100), rect);

        Assert.Null(ProjectInvariants.Validate(cropped));
        Assert.True(rect.H > rect.W, "a portrait crop of a landscape frame is the point");
    }

    [Theory]
    [InlineData(1.1)]
    [InlineData(1.5)]
    [InlineData(0.9)]
    [InlineData(0.5)]
    public void Resizing_a_free_rectangle_scales_both_edges_and_keeps_it_usable(double factor)
    {
        // Locked, one press changes one dimension and the other follows the ratio. Free,
        // both are scaled — and the shape has to be roughly kept, or grow and shrink would
        // walk a tall crop back towards the output's own shape. Small enough that the
        // largest factor here still fits the frame, since the frame wins over the shape.
        var rect = new RectI(300, 100, 200, 300);

        var resized = RectPlacement.Resize(rect, factor, FrameW, FrameH, lockAspect: false);

        AssertInsideFrame(resized);
        Assert.Equal(0, resized.W % 2);
        Assert.Equal(0, resized.H % 2);
        Assert.InRange((double)resized.W / resized.H, (200.0 / 300) - 0.02, (200.0 / 300) + 0.02);
    }

    [Fact]
    public void Resizing_a_free_rectangle_actually_moves_on_every_press()
    {
        // The reason free resizing does not go through FitAspect: 400x798 reduces to
        // 400:798, which fits inside the frame exactly once, so every press would clamp to
        // that single step and the key would silently do nothing.
        var rect = new RectI(400, 100, 400, 598);

        var grown = RectPlacement.Resize(rect, 1.1, FrameW, FrameH, lockAspect: false);
        var shrunk = RectPlacement.Resize(rect, 1 / 1.1, FrameW, FrameH, lockAspect: false);

        Assert.True(grown.W > rect.W, $"grow left {rect} at {grown}");
        Assert.True(shrunk.W < rect.W, $"shrink left {rect} at {shrunk}");
    }

    [Fact]
    public void Repeated_free_resizing_never_escapes_the_frame_or_collapses()
    {
        var rect = new RectI(300, 100, 400, 600);

        for (var i = 0; i < 40; i++)
        {
            rect = RectPlacement.Resize(rect, 1.1, FrameW, FrameH, lockAspect: false);
            AssertInsideFrame(rect);
        }

        for (var i = 0; i < 40; i++)
        {
            rect = RectPlacement.Resize(rect, 1 / 1.1, FrameW, FrameH, lockAspect: false);
            AssertInsideFrame(rect);
            Assert.True(rect.W >= 2 && rect.H >= 2);
        }
    }
}
