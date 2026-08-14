namespace BertCut.Core.Time;

/// <summary>
/// An exact rational number, used for frame rates and time bases.
/// </summary>
/// <remarks>
/// Frame rates are rational in practice, not integral: NTSC-derived rates are 30000/1001
/// and 24000/1001, and screen recorders emit stream time bases like 1/90000. Rounding
/// those to a double and multiplying by a frame count accumulates error until cuts land
/// on the wrong frame, so rates never leave this type.
/// </remarks>
public readonly record struct Rational(int Num, int Den) : IComparable<Rational>
{
    public static readonly Rational Zero = new(0, 1);

    /// <summary>Common frame rate: 30000/1001 ≈ 29.97.</summary>
    public static readonly Rational Ntsc30 = new(30000, 1001);

    /// <summary>Common frame rate: 60000/1001 ≈ 59.94.</summary>
    public static readonly Rational Ntsc60 = new(60000, 1001);

    public static Rational FromInt(int n) => new(n, 1);

    /// <summary>The reciprocal. A frame rate inverted is a frame duration.</summary>
    public Rational Inverse => new(Den, Num);

    /// <summary>
    /// For display and layout only — never for time arithmetic.
    /// </summary>
    public double Approx => (double)Num / Den;

    /// <summary>Reduces to lowest terms with a positive denominator.</summary>
    public Rational Normalized()
    {
        if (Den == 0) throw new InvalidOperationException("Rational has a zero denominator.");

        var (num, den) = Den < 0 ? (-Num, -Den) : (Num, Den);
        var g = Gcd(Math.Abs(num), den);
        return g <= 1 ? new Rational(num, den) : new Rational(num / g, den / g);
    }

    /// <summary>
    /// Value equality after reduction. The record's own <c>==</c> compares Num and Den
    /// componentwise, so 30/1 and 60/2 are distinct to it but equal here.
    /// </summary>
    public bool EquivalentTo(Rational other)
    {
        var a = Normalized();
        var b = other.Normalized();
        return a.Num == b.Num && a.Den == b.Den;
    }

    public int CompareTo(Rational other) => ((long)Num * other.Den).CompareTo((long)other.Num * Den);

    public override string ToString() => $"{Num}/{Den}";

    /// <summary>
    /// Parses the "num/den" form written by <see cref="ToString"/> and emitted by ffprobe.
    /// A bare integer is accepted as "n/1".
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> s, out Rational value)
    {
        value = default;
        var slash = s.IndexOf('/');
        if (slash < 0)
        {
            if (!int.TryParse(s, out var whole)) return false;
            value = new Rational(whole, 1);
            return true;
        }

        if (!int.TryParse(s[..slash], out var num)) return false;
        if (!int.TryParse(s[(slash + 1)..], out var den)) return false;
        if (den == 0) return false;

        value = new Rational(num, den);
        return true;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
