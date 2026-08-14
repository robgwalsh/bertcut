namespace BertCut.Core.Time;

/// <summary>
/// A half-open range of frames, <c>[Start, End)</c>.
/// </summary>
/// <remarks>
/// Every range in BertCut is half-open, without exception. Mixed conventions are the
/// classic source of off-by-one frames at cut boundaries, and a duplicated or dropped
/// frame at a join is exactly the defect this editor cannot afford.
/// </remarks>
public readonly record struct FrameRange(long Start, long End)
{
    public static readonly FrameRange Empty = new(0, 0);

    public static FrameRange FromLength(long start, long length) => new(start, start + length);

    public long Length => End - Start;

    public bool IsEmpty => End <= Start;

    public bool Contains(long frame) => frame >= Start && frame < End;

    /// <summary>True when this range fully covers <paramref name="other"/>.</summary>
    public bool Covers(FrameRange other) => other.Start >= Start && other.End <= End;

    public bool Overlaps(FrameRange other) => Start < other.End && other.Start < End;

    /// <summary>The overlapping portion, or null when the two are disjoint.</summary>
    public FrameRange? Intersect(FrameRange other)
    {
        var start = Math.Max(Start, other.Start);
        var end = Math.Min(End, other.End);
        return end > start ? new FrameRange(start, end) : null;
    }

    /// <summary>Shifts both endpoints by <paramref name="delta"/>.</summary>
    public FrameRange Shift(long delta) => new(Start + delta, End + delta);

    /// <summary>Clamps both endpoints into <c>[0, limit]</c>.</summary>
    public FrameRange ClampTo(long limit) =>
        new(Math.Clamp(Start, 0, limit), Math.Clamp(End, 0, limit));

    public override string ToString() => $"[{Start}, {End})";
}
