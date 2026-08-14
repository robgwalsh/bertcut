using BertCut.Core.Time;

namespace BertCut.Core.Tests;

public class RationalMathTests
{
    [Fact]
    public void Rescale_converts_between_frame_rates_exactly()
    {
        // 60 frames at 30 fps is 2 seconds, which is 120 frames at 60 fps.
        var thirty = Rational.FromInt(30).Inverse;
        var sixty = Rational.FromInt(60).Inverse;

        Assert.Equal(120, RationalMath.Rescale(60, thirty, sixty));
        Assert.Equal(60, RationalMath.Rescale(120, sixty, thirty));
    }

    [Fact]
    public void Rescale_is_exact_for_ntsc_rates_over_long_durations()
    {
        // Two hours of 29.97 fps is 215784 frames. A double-based conversion accumulates
        // error here; this must land on the exact frame.
        var ntsc = Rational.Ntsc30.Inverse;
        var stream = new Rational(1, 90000);   // the usual MP4 video time base

        const long frames = 215_784;
        var pts = RationalMath.Rescale(frames, ntsc, stream);

        Assert.Equal(frames, RationalMath.Rescale(pts, stream, ntsc));
    }

    [Fact]
    public void Rescale_does_not_overflow_on_large_intermediates()
    {
        // The naive long product here is ~6.5e18 * 90000, far past 2^63. Int128 keeps it exact.
        var stream = new Rational(1, 90000);
        var ntsc = Rational.Ntsc60.Inverse;

        var result = RationalMath.Rescale(648_000_000, stream, ntsc);

        Assert.Equal(431_568, result);
    }

    [Fact]
    public void Rescale_rounds_half_away_from_zero_but_floor_truncates_down()
    {
        var from = new Rational(1, 2);    // half-units
        var to = new Rational(1, 1);      // whole units

        // 3 half-units = 1.5 whole units.
        Assert.Equal(2, RationalMath.Rescale(3, from, to));
        Assert.Equal(1, RationalMath.RescaleFloor(3, from, to));
    }

    [Fact]
    public void RescaleFloor_never_overstates_a_duration()
    {
        // A duration that rounds up would let a segment read one frame past its source,
        // which shows up as a duplicated frame at a cut boundary.
        var ntsc = Rational.Ntsc30.Inverse;
        var thirty = Rational.FromInt(30).Inverse;

        for (var frames = 1; frames < 200; frames++)
        {
            var converted = RationalMath.RescaleFloor(frames, thirty, ntsc);
            Assert.True(converted <= frames * 30000.0 / 30 / 1001 * 1001 / 1000 + 1);
            Assert.True(converted >= 0);
        }
    }

    [Theory]
    [InlineData("30", 30, 1)]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("1/90000", 1, 90000)]
    public void TryParse_reads_the_ffprobe_forms(string text, int num, int den)
    {
        Assert.True(Rational.TryParse(text, out var value));
        Assert.Equal(new Rational(num, den), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("30/0")]
    [InlineData("30/")]
    public void TryParse_rejects_malformed_input(string text) =>
        Assert.False(Rational.TryParse(text, out _));

    [Fact]
    public void EquivalentTo_compares_by_value_not_by_representation()
    {
        Assert.True(new Rational(30, 1).EquivalentTo(new Rational(60, 2)));
        Assert.False(new Rational(30, 1).EquivalentTo(Rational.Ntsc30));

        // The record's own equality is componentwise, which is why EquivalentTo exists.
        Assert.NotEqual(new Rational(30, 1), new Rational(60, 2));
    }
}
