using System.Numerics;

namespace BertCut.Core.Audio;

/// <summary>
/// An in-place radix-2 fast Fourier transform, and the cross-correlation built on it.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written because <c>BertCut.Core</c> depends on nothing but the BCL, which has no FFT,
/// and because the only caller needs one specific thing: the lag at which two amplitude
/// envelopes line up. That is a correlation over the whole of both signals, which is
/// O(n log n) through the frequency domain and O(n²) done directly. For a pair of one-hour
/// recordings at the 100 Hz envelope rate that is the difference between a few milliseconds
/// and a couple of minutes.
/// </para>
/// <para>
/// Radix-2 only, so <see cref="Transform"/> requires a power-of-two length;
/// <see cref="NextPowerOfTwo"/> is how callers get there. Mixed-radix would save memory on
/// awkward lengths and buy nothing else.
/// </para>
/// </remarks>
public static class Fft
{
    /// <summary>The smallest power of two greater than or equal to <paramref name="value"/>.</summary>
    public static int NextPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        return 1 << (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(value - 1)));
    }

    /// <summary>
    /// Transforms <paramref name="buffer"/> in place. Its length must be a power of two.
    /// </summary>
    /// <param name="inverse">
    /// When true, computes the inverse transform and scales by 1/n, so that a forward
    /// transform followed by an inverse one returns the original signal.
    /// </param>
    public static void Transform(Complex[] buffer, bool inverse = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var n = buffer.Length;
        if (n <= 1) return;

        if ((n & (n - 1)) != 0)
            throw new ArgumentException($"Length must be a power of two, was {n}.", nameof(buffer));

        // Decimation in time: reorder into bit-reversed index order, after which the
        // butterflies below run over contiguous pairs.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;

            if (i < j) (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));

            for (var start = 0; start < n; start += length)
            {
                var w = Complex.One;

                for (var k = 0; k < length / 2; k++)
                {
                    var even = buffer[start + k];
                    var odd = buffer[start + k + (length / 2)] * w;

                    buffer[start + k] = even + odd;
                    buffer[start + k + (length / 2)] = even - odd;

                    w *= step;
                }
            }
        }

        if (!inverse) return;

        for (var i = 0; i < n; i++) buffer[i] /= n;
    }

    /// <summary>
    /// Cross-correlates <paramref name="signal"/> against <paramref name="pattern"/>.
    /// </summary>
    /// <returns>
    /// One score per lag, where index <c>k</c> is how well <paramref name="pattern"/> matches
    /// <paramref name="signal"/> starting at sample <c>k</c>. The result has
    /// <c>signal.Length</c> entries; lags where the pattern would run off the end are
    /// included but score low, because the overlap is short.
    /// </returns>
    /// <remarks>
    /// Correlation is convolution with one input reversed, and convolution is a pointwise
    /// product in the frequency domain. Both inputs are zero-padded to at least the sum of
    /// their lengths first: without that padding the transform's inherent periodicity wraps
    /// the tail of one signal onto the head of the other and invents a match that is not
    /// there.
    /// </remarks>
    public static double[] CrossCorrelate(ReadOnlySpan<float> signal, ReadOnlySpan<float> pattern)
    {
        if (signal.IsEmpty || pattern.IsEmpty) return [];

        var size = NextPowerOfTwo(signal.Length + pattern.Length);

        var a = new Complex[size];
        var b = new Complex[size];

        for (var i = 0; i < signal.Length; i++) a[i] = signal[i];

        // Reversed, which turns the convolution the transform computes into a correlation.
        for (var i = 0; i < pattern.Length; i++) b[pattern.Length - 1 - i] = pattern[i];

        Transform(a);
        Transform(b);

        for (var i = 0; i < size; i++) a[i] *= b[i];

        Transform(a, inverse: true);

        // Reversing the pattern shifted every lag by its length, so lag k lands at
        // k + pattern.Length - 1.
        var result = new double[signal.Length];
        for (var k = 0; k < result.Length; k++)
        {
            var index = k + pattern.Length - 1;
            result[k] = index < size ? a[index].Real : 0;
        }

        return result;
    }
}
