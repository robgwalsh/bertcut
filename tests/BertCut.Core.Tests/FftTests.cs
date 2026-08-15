using System.Numerics;
using BertCut.Core.Audio;

namespace BertCut.Core.Tests;

public class FftTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 2048)]
    public void NextPowerOfTwo_rounds_up(int value, int expected) =>
        Assert.Equal(expected, Fft.NextPowerOfTwo(value));

    [Fact]
    public void A_constant_signal_transforms_to_a_single_bin()
    {
        var buffer = new Complex[8];
        Array.Fill(buffer, Complex.One);

        Fft.Transform(buffer);

        // All the energy is at DC: bin 0 is n, everything else is zero.
        Assert.Equal(8, buffer[0].Real, 6);
        for (var i = 1; i < buffer.Length; i++)
            Assert.Equal(0, buffer[i].Magnitude, 6);
    }

    [Fact]
    public void A_sinusoid_transforms_to_its_own_bin()
    {
        const int n = 64;
        const int bin = 5;

        var buffer = new Complex[n];
        for (var i = 0; i < n; i++) buffer[i] = Math.Cos(2 * Math.PI * bin * i / n);

        Fft.Transform(buffer);

        // A real cosine splits its energy between bin k and bin n-k.
        Assert.Equal(n / 2.0, buffer[bin].Magnitude, 6);
        Assert.Equal(n / 2.0, buffer[n - bin].Magnitude, 6);
        Assert.Equal(0, buffer[bin + 1].Magnitude, 6);
    }

    [Fact]
    public void The_inverse_transform_returns_the_original_signal()
    {
        var random = new Random(20260815);
        var original = new Complex[128];
        for (var i = 0; i < original.Length; i++)
            original[i] = new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5);

        var buffer = (Complex[])original.Clone();

        Fft.Transform(buffer);
        Fft.Transform(buffer, inverse: true);

        for (var i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i].Real, buffer[i].Real, 9);
            Assert.Equal(original[i].Imaginary, buffer[i].Imaginary, 9);
        }
    }

    [Fact]
    public void A_non_power_of_two_length_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Fft.Transform(new Complex[6]));

    [Fact]
    public void Cross_correlation_peaks_at_the_lag_the_pattern_was_planted_at()
    {
        const int lag = 37;

        var random = new Random(7);
        var signal = new float[512];
        for (var i = 0; i < signal.Length; i++) signal[i] = (float)random.NextDouble();

        var pattern = signal.AsSpan(lag, 24).ToArray();
        var scores = Fft.CrossCorrelate(signal, pattern);

        var best = 0;
        for (var i = 1; i < scores.Length; i++)
            if (scores[i] > scores[best]) best = i;

        Assert.Equal(lag, best);
    }

    /// <remarks>
    /// The zero padding inside <see cref="Fft.CrossCorrelate"/> is what stops the transform's
    /// periodicity wrapping the end of the signal onto its start. Without it a pattern taken
    /// from the very end also scores highly at lag 0, so this is the case that would fail.
    /// </remarks>
    [Fact]
    public void Cross_correlation_does_not_wrap_around_the_end_of_the_signal()
    {
        var signal = new float[256];
        for (var i = 0; i < 8; i++) signal[signal.Length - 8 + i] = 1f;

        var pattern = new float[8];
        Array.Fill(pattern, 1f);

        var scores = Fft.CrossCorrelate(signal, pattern);

        Assert.Equal(8, scores[^8], 6);
        Assert.Equal(0, scores[0], 6);
    }

    [Fact]
    public void Cross_correlation_of_an_empty_input_is_empty()
    {
        Assert.Empty(Fft.CrossCorrelate([], [1f]));
        Assert.Empty(Fft.CrossCorrelate([1f], []));
    }
}
