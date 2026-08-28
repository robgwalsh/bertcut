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
/// Positioning and resizing of a rectangle inside the output frame, aspect-locked or free.
/// </summary>
/// <remarks>
/// <para>
/// An overlay's landing box is locked to the ratio of the picture going into it, so that
/// picture is never stretched. A <b>crop</b> is free: the region worth keeping is not the
/// editor's judgement to make, and a whole-timeline crop hands its own shape to the
/// delivered file rather than being squeezed back into the output's — see
/// <see cref="Project.DeliveredSize"/>.
/// </para>
/// <para>
/// A non-positive <c>aspectW</c> or <c>aspectH</c> is how a caller asks for the free
/// shape. Locked, the ratio is preserved exactly and resizing is one-dimensional; free,
/// both edges are the pointer's and the only rules left are the frame, the minimum size,
/// and even dimensions.
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

    /// <summary>Whether a caller has asked for a free shape rather than a locked ratio.</summary>
    private static bool IsFree(int aspectW, int aspectH) => aspectW <= 0 || aspectH <= 0;

    /// <summary>
    /// The nearest usable size to one the pointer asked for, with no ratio to honour.
    /// </summary>
    /// <remarks>
    /// Even, because ffmpeg's crop filter fails outright on an odd dimension with yuv420p
    /// and a whole-timeline crop's size goes on to be the delivered one. This is the whole
    /// of the free-shape rule — the counterpart to <see cref="FitAspect"/>, which has a
    /// ratio to keep and therefore much more to do.
    /// </remarks>
    private static (int Width, int Height) FitFree(int width, int height, int frameWidth, int frameHeight) =>
        (Even(width, frameWidth), Even(height, frameHeight));

    private static int Even(int value, int limit) =>
        Math.Max(2, Math.Min(Math.Max(value, MinimumSize), limit) & ~1);

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
    /// Scales a rectangle about its centre, keeping its shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Growing about the centre rather than a corner is what makes repeated presses feel
    /// like zooming rather than dragging — the region of interest stays where the user put it.
    /// </para>
    /// <para>
    /// A locked rectangle goes through <see cref="FitAspect"/>, which lands on an exact
    /// multiple of the ratio. A free one cannot: an arbitrary shape's reduced unit is often
    /// most of the frame — 400x798 reduces to 400:798, which fits inside 1920x1080 exactly
    /// once — so every press would clamp to the same single step and the key would do
    /// nothing. Both edges are scaled and rounded to even instead, which can drift the shape
    /// by a pixel over a run of presses; that is the right trade for a rectangle whose shape
    /// was never promised to anybody.
    /// </para>
    /// </remarks>
    public static RectI Resize(RectI rect, double factor, int frameWidth, int frameHeight, bool lockAspect = true)
    {
        var centreX = rect.X + (rect.W / 2.0);
        var centreY = rect.Y + (rect.H / 2.0);

        var (w, h) = lockAspect
            ? FitAspect(
                Math.Max(MinimumSize, (int)Math.Round(rect.W * factor)),
                rect.W, rect.H, frameWidth, frameHeight)
            : FitFree(
                (int)Math.Round(rect.W * factor), (int)Math.Round(rect.H * factor),
                frameWidth, frameHeight);

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
    /// approximation. An overlay's box is locked to the ratio of the picture going into it,
    /// and rounding the height off the width leaves a sliver of stretch that a run of
    /// resizes accumulates: for a 5:3 source, a width of 768 wants a height of 460.8, and
    /// neither 460 nor 462 is exactly 5:3.
    /// </para>
    /// <para>
    /// Both dimensions also have to be even, since ffmpeg's crop filter fails outright on
    /// an odd dimension with yuv420p. When the reduced ratio has an odd term, the unit is
    /// doubled so every multiple of it is even.
    /// </para>
    /// <para>
    /// A non-positive ratio is a caller asking for a free shape, which has nothing to fit:
    /// the width is taken as asked and the height fills the frame. Callers that mean it go
    /// through <see cref="FitFree"/>, which keeps both of the pointer's edges.
    /// </para>
    /// </remarks>
    public static (int Width, int Height) FitAspect(int targetWidth, int aspectW, int aspectH, int frameWidth, int frameHeight)
    {
        if (IsFree(aspectW, aspectH)) return (Math.Min(targetWidth, frameWidth), frameHeight);

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
    /// Builds a rectangle from a freehand drag.
    /// </summary>
    /// <remarks>
    /// Free, the drag <em>is</em> the answer, and the only corrections are the frame, the
    /// minimum size and an even edge. Locked, the ratio is not negotiable, so what comes
    /// back is the aspect-correct rectangle nearest what was drawn, centred on it — which
    /// is why both paths end up centred on the drag: the free one is already there.
    /// </remarks>
    public static RectI FromDrag(int x0, int y0, int x1, int y1, int aspectW, int aspectH, int frameWidth, int frameHeight)
    {
        var left = Math.Min(x0, x1);
        var top = Math.Min(y0, y1);
        var width = Math.Abs(x1 - x0);
        var height = Math.Abs(y1 - y0);

        // Take whichever dimension demands the larger rectangle, so the drag is fully covered.
        var (w, h) = IsFree(aspectW, aspectH)
            ? FitFree(width, height, frameWidth, frameHeight)
            : FitAspect(
                Math.Max(width, (int)Math.Round((double)height * aspectW / aspectH)),
                aspectW, aspectH, frameWidth, frameHeight);

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
    /// along with the pointer, which is the one thing a corner handle must not do, so the
    /// pointer sets the size and the anchor sets the position. Free, that makes a handle
    /// the only way to reshape a box in one direction; locked, the pointer's larger demand
    /// still wins on both axes so the box keeps up with it.
    /// </remarks>
    public static RectI FromCorner(int anchorX, int anchorY, int x, int y, int aspectW, int aspectH, int frameWidth, int frameHeight)
    {
        var width = Math.Abs(x - anchorX);
        var height = Math.Abs(y - anchorY);

        // Whichever dimension demands the larger rectangle wins, so the box keeps up with
        // the pointer on both axes.
        var (w, h) = IsFree(aspectW, aspectH)
            ? FitFree(width, height, frameWidth, frameHeight)
            : FitAspect(
                Math.Max(width, (int)Math.Round((double)height * aspectW / aspectH)),
                aspectW, aspectH, frameWidth, frameHeight);

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
