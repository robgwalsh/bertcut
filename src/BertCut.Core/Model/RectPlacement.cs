namespace BertCut.Core.Model;

/// <summary>Where a rectangle can be snapped within the frame.</summary>
public enum Anchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Centre,
}

/// <summary>
/// Aspect-locked positioning and resizing of a rectangle inside the output frame.
/// </summary>
/// <remarks>
/// <para>
/// Both rectangles the editor places are aspect-locked. A crop is locked to the output
/// ratio, which is what lets zoom-to-fill be a plain crop-then-scale with no letterbox
/// branch in either renderer; an overlay is locked to its own source's ratio so the
/// picture is never stretched. Because the ratio is fixed, resizing is one-dimensional and
/// every operation here preserves it exactly.
/// </para>
/// <para>
/// Everything is integer arithmetic in output-space pixels — the same units the export
/// filter graph uses — so a rectangle the user positions is the rectangle ffmpeg receives.
/// </para>
/// </remarks>
public static class RectPlacement
{
    /// <summary>Smallest edge a rectangle may shrink to, in output pixels.</summary>
    public const int MinimumSize = 32;

    /// <summary>
    /// A starting rectangle: the given fraction of the frame, snapped to an anchor.
    /// </summary>
    public static RectI Initial(int frameWidth, int frameHeight, int aspectW, int aspectH, double fraction, Anchor anchor)
    {
        var (w, h) = FitAspect(
            (int)Math.Round(frameWidth * fraction),
            aspectW, aspectH, frameWidth, frameHeight);

        return Snap(new RectI(0, 0, w, h), frameWidth, frameHeight, anchor);
    }

    /// <summary>Moves a rectangle, keeping it inside the frame.</summary>
    public static RectI Move(RectI rect, int dx, int dy, int frameWidth, int frameHeight) =>
        Clamp(rect with { X = rect.X + dx, Y = rect.Y + dy }, frameWidth, frameHeight);

    /// <summary>
    /// Scales a rectangle about its centre, preserving its aspect ratio.
    /// </summary>
    /// <remarks>
    /// Growing about the centre rather than a corner is what makes repeated presses feel
    /// like zooming rather than dragging — the region of interest stays where the user put it.
    /// </remarks>
    public static RectI Resize(RectI rect, double factor, int frameWidth, int frameHeight)
    {
        var centreX = rect.X + (rect.W / 2.0);
        var centreY = rect.Y + (rect.H / 2.0);

        var aspectW = rect.W;
        var aspectH = rect.H;

        var target = Math.Max(MinimumSize, (int)Math.Round(rect.W * factor));
        var (w, h) = FitAspect(target, aspectW, aspectH, frameWidth, frameHeight);

        return Clamp(
            new RectI((int)Math.Round(centreX - (w / 2.0)), (int)Math.Round(centreY - (h / 2.0)), w, h),
            frameWidth, frameHeight);
    }

    /// <summary>Places a rectangle against an edge or the centre, with a small margin.</summary>
    public static RectI Snap(RectI rect, int frameWidth, int frameHeight, Anchor anchor, int margin = 0)
    {
        var (x, y) = anchor switch
        {
            Anchor.TopLeft => (margin, margin),
            Anchor.TopRight => (frameWidth - rect.W - margin, margin),
            Anchor.BottomLeft => (margin, frameHeight - rect.H - margin),
            Anchor.BottomRight => (frameWidth - rect.W - margin, frameHeight - rect.H - margin),
            Anchor.Centre => ((frameWidth - rect.W) / 2, (frameHeight - rect.H) / 2),
            _ => (rect.X, rect.Y),
        };

        return Clamp(rect with { X = x, Y = y }, frameWidth, frameHeight);
    }

    /// <summary>
    /// Builds the largest rectangle of the given ratio that fits both the requested width
    /// and the frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is an exact integer multiple of the reduced ratio rather than a rounded
    /// approximation, because <see cref="ProjectInvariants"/> compares crop aspect by
    /// cross-multiplication and rejects anything off by a pixel. Computing the height from
    /// the width and then rounding — even to the nearest even number — does not survive
    /// that check: for a 5:3 output, a width of 768 wants a height of 460.8, and neither
    /// 460 nor 462 is exactly 5:3.
    /// </para>
    /// <para>
    /// Both dimensions also have to be even, since ffmpeg's crop filter fails outright on
    /// an odd dimension with yuv420p. When the reduced ratio has an odd term, the unit is
    /// doubled so every multiple of it is even.
    /// </para>
    /// </remarks>
    public static (int Width, int Height) FitAspect(int targetWidth, int aspectW, int aspectH, int frameWidth, int frameHeight)
    {
        if (aspectW <= 0 || aspectH <= 0) return (Math.Min(targetWidth, frameWidth), frameHeight);

        var divisor = Gcd(aspectW, aspectH);
        var unitW = aspectW / divisor;
        var unitH = aspectH / divisor;

        if (unitW % 2 != 0 || unitH % 2 != 0)
        {
            unitW *= 2;
            unitH *= 2;
        }

        var maxSteps = Math.Min(frameWidth / unitW, frameHeight / unitH);
        if (maxSteps < 1)
        {
            // The ratio is too extreme to express exactly inside the frame. Fall back to
            // the nearest even fit; only a crop is invariant-checked, and a crop's ratio
            // always divides its own frame.
            var w = Math.Max(2, Math.Min(frameWidth, targetWidth) & ~1);
            var h = Math.Max(2, Math.Min(frameHeight, (int)Math.Round((double)w * aspectH / aspectW)) & ~1);
            return (w, h);
        }

        var minSteps = Math.Max(1, (int)Math.Ceiling((double)MinimumSize / unitW));
        minSteps = Math.Min(minSteps, maxSteps);

        var steps = Math.Clamp((int)Math.Round((double)targetWidth / unitW), minSteps, maxSteps);
        return (unitW * steps, unitH * steps);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return Math.Max(1, a);
    }

    /// <summary>
    /// Builds a crop rectangle from a freehand drag, locked to the output's aspect ratio.
    /// </summary>
    /// <remarks>
    /// The drag defines a region of interest; the ratio is not negotiable, so the returned
    /// rectangle is the aspect-correct one nearest what was drawn, centred on it.
    /// </remarks>
    public static RectI FromDrag(int x0, int y0, int x1, int y1, int aspectW, int aspectH, int frameWidth, int frameHeight)
    {
        var left = Math.Min(x0, x1);
        var top = Math.Min(y0, y1);
        var width = Math.Abs(x1 - x0);
        var height = Math.Abs(y1 - y0);

        // Take whichever dimension demands the larger rectangle, so the drag is fully covered.
        var byWidth = width;
        var byHeight = aspectH > 0 ? (int)Math.Round((double)height * aspectW / aspectH) : width;
        var (w, h) = FitAspect(Math.Max(byWidth, byHeight), aspectW, aspectH, frameWidth, frameHeight);

        var centreX = left + (width / 2);
        var centreY = top + (height / 2);

        return Clamp(new RectI(centreX - (w / 2), centreY - (h / 2), w, h), frameWidth, frameHeight);
    }

    /// <summary>
    /// Resizes from a dragged corner, keeping the opposite corner where it is.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="FromDrag"/> for a grab on one of the four handles.
    /// Centring the result the way a freehand drag does would drag the anchored corner
    /// along with the pointer, which is the one thing a corner handle must not do; the
    /// ratio is still not negotiable, so the pointer sets the size and the anchor sets the
    /// position.
    /// </remarks>
    public static RectI FromCorner(int anchorX, int anchorY, int x, int y, int aspectW, int aspectH, int frameWidth, int frameHeight)
    {
        var width = Math.Abs(x - anchorX);
        var height = Math.Abs(y - anchorY);

        // Whichever dimension demands the larger rectangle wins, so the box keeps up with
        // the pointer on both axes.
        var byHeight = aspectH > 0 ? (int)Math.Round((double)height * aspectW / aspectH) : width;
        var (w, h) = FitAspect(Math.Max(width, byHeight), aspectW, aspectH, frameWidth, frameHeight);

        return Clamp(
            new RectI(x >= anchorX ? anchorX : anchorX - w, y >= anchorY ? anchorY : anchorY - h, w, h),
            frameWidth, frameHeight);
    }

    /// <summary>Shifts a rectangle back inside the frame, shrinking it only if it cannot fit.</summary>
    public static RectI Clamp(RectI rect, int frameWidth, int frameHeight)
    {
        var w = Math.Min(rect.W, frameWidth);
        var h = Math.Min(rect.H, frameHeight);

        return new RectI(
            Math.Clamp(rect.X, 0, Math.Max(0, frameWidth - w)),
            Math.Clamp(rect.Y, 0, Math.Max(0, frameHeight - h)),
            w,
            h);
    }
}
