namespace BertCut.Core.Time;

/// <summary>
/// Exact conversion of a value from one time base to another — the managed equivalent of
/// FFmpeg's <c>av_rescale_q</c>.
/// </summary>
/// <remarks>
/// The intermediate product overflows <see cref="long"/> for realistic inputs: a two-hour
/// recording at a 1/90000 time base is ~6.5e8 ticks, and multiplying that by another time
/// base's numerator passes 2^63 easily. <see cref="Int128"/> makes the intermediate free
/// of overflow without giving up exactness the way a double would.
/// </remarks>
public static class RationalMath
{
    /// <summary>
    /// Rescales <paramref name="value"/> from <paramref name="from"/> units to
    /// <paramref name="to"/> units, rounding half away from zero.
    /// </summary>
    /// <remarks>
    /// Use this for positions (an instant in time). Rounding to nearest keeps a position
    /// stable across a round trip, which is what frame stepping and seeking depend on.
    /// </remarks>
    public static long Rescale(long value, Rational from, Rational to)
    {
        var (n, d) = RescaleParts(value, from, to);
        if (d == 0) throw new ArgumentException("Cannot rescale to a zero-valued time base.", nameof(to));

        var half = d / 2;
        var q = n >= 0 ? (n + half) / d : (n - half) / d;
        return checked((long)q);
    }

    /// <summary>
    /// Rescales toward negative infinity.
    /// </summary>
    /// <remarks>
    /// Use this for durations that must not overstate their extent — most importantly the
    /// count of whole output frames a source span covers. Rounding a duration up produces
    /// a segment that reads one frame past its source, which is how a stray duplicated
    /// frame appears at a cut boundary.
    /// </remarks>
    public static long RescaleFloor(long value, Rational from, Rational to)
    {
        var (n, d) = RescaleParts(value, from, to);
        if (d == 0) throw new ArgumentException("Cannot rescale to a zero-valued time base.", nameof(to));

        var q = n / d;
        if (n % d != 0 && n < 0) q -= 1;   // C# truncates toward zero; floor differs for negatives.
        return checked((long)q);
    }

    /// <summary>
    /// Converts a frame index at <paramref name="rate"/> to whole seconds and the
    /// remaining frames — for timecode display only.
    /// </summary>
    public static (long Seconds, long RemainderFrames) SplitSeconds(long frame, Rational rate)
    {
        if (rate.Num == 0) throw new ArgumentException("Frame rate cannot be zero.", nameof(rate));

        // frame / (num/den) seconds = frame * den / num
        var totalDen = (Int128)frame * rate.Den;
        var seconds = totalDen / rate.Num;
        var remainder = frame - (long)(seconds * rate.Num / rate.Den);
        return ((long)seconds, remainder);
    }

    private static (Int128 Numerator, Int128 Denominator) RescaleParts(long value, Rational from, Rational to)
    {
        // value * (from.Num / from.Den) / (to.Num / to.Den)
        //   = value * from.Num * to.Den / (from.Den * to.Num)
        var n = (Int128)value * from.Num * to.Den;
        var d = (Int128)from.Den * to.Num;

        // Keep the denominator positive so the rounding branches above stay correct.
        return d < 0 ? (-n, -d) : (n, d);
    }
}
