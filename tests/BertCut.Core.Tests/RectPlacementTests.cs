using BertCut.Core.Model;

namespace BertCut.Core.Tests;

/// <summary>
/// Placement geometry for crop and overlay rectangles.
/// </summary>
/// <remarks>
/// The aspect lock is not cosmetic: <see cref="ProjectInvariants"/> rejects a crop whose
/// ratio does not match the output exactly, because that is what lets zoom-to-fill be a
/// plain crop-then-scale with no pad branch in either renderer. So every operation here
/// has to preserve the ratio, not merely approximate it.
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
}
