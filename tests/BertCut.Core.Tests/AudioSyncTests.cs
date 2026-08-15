using BertCut.Core.Audio;

namespace BertCut.Core.Tests;

/// <summary>
/// The correlation, tested on synthesised envelopes with no ffmpeg and no window.
/// </summary>
/// <remarks>
/// Every offset here is planted, so these tests say whether the algorithm finds a known
/// answer rather than whether two particular recordings happen to line up. The case that
/// matters most is <see cref="The_clip_matching_itself_is_refused_when_it_is_excluded"/> —
/// the driving use case has one file holding both angles, and the reference window matching
/// its own position is a perfect score that means nothing.
/// </remarks>
public class AudioSyncTests
{
    private const int Rate = AudioPeaks.DefaultRate;

    [Fact]
    public void An_offset_between_two_recordings_is_recovered()
    {
        var event_ = Envelope(seed: 1, buckets: 6 * Rate);

        // The same event, sitting 12.5 s into a different camera's recording.
        var camera = Silence(30 * Rate);
        Place(camera, at: (int)(12.5 * Rate), what: event_);

        var request = new SyncRequest(
            Reference: Peaks(Pad(event_, before: 0, total: 6 * Rate)),
            ReferenceStart: 0,
            ReferenceLength: 6,
            Candidate: Peaks(camera));

        var best = Assert.Single(AudioSync.FindOffsets(request, take: 1));

        Assert.Equal(12.5, best.OffsetSeconds, 1);
        Assert.True(best.Confidence > 0.9, $"confidence was {best.Confidence:0.000}");
    }

    /// <remarks>
    /// This documents the trap rather than the feature: with no exclusion the top answer is
    /// the reference window's own position, which is a perfect match and a useless one. If
    /// this test ever starts failing, the exclusion in the test below has stopped being
    /// necessary and both should be revisited together.
    /// </remarks>
    [Fact]
    public void A_clip_matching_itself_outscores_the_real_second_angle()
    {
        var (file, angleOne, angleTwo) = TwoAngles();

        var request = new SyncRequest(
            Reference: Peaks(file),
            ReferenceStart: angleOne,
            ReferenceLength: AngleSeconds,
            Candidate: Peaks(file));

        var best = AudioSync.FindOffsets(request)[0];

        Assert.Equal(angleOne, best.OffsetSeconds, 1);
        Assert.NotEqual(angleTwo, best.OffsetSeconds, 1);
    }

    [Fact]
    public void The_clip_matching_itself_is_refused_when_it_is_excluded()
    {
        var (file, angleOne, angleTwo) = TwoAngles();

        var request = new SyncRequest(
            Reference: Peaks(file),
            ReferenceStart: angleOne,
            ReferenceLength: AngleSeconds,
            Candidate: Peaks(file))
        {
            Exclude = (angleOne, angleOne + AngleSeconds),
        };

        var best = AudioSync.FindOffsets(request)[0];

        Assert.Equal(angleTwo, best.OffsetSeconds, 1);
        Assert.True(best.Confidence > 0.6, $"confidence was {best.Confidence:0.000}");
    }

    [Fact]
    public void A_quieter_and_noisier_second_angle_still_matches()
    {
        var event_ = Envelope(seed: 3, buckets: 5 * Rate);

        var camera = Silence(20 * Rate);

        // Half the level, a DC offset from a different room, and noise at 40% of the event's
        // own deviation. The coefficient is invariant to the first two by construction; the
        // third is the only part that actually degrades it.
        var muffled = new float[event_.Length];
        for (var i = 0; i < muffled.Length; i++) muffled[i] = (float)((event_[i] * 0.5) + 0.2);
        AddNoise(muffled, relativeToDeviation: 0.4, seed: 99);

        Place(camera, at: 7 * Rate, what: muffled);

        var request = new SyncRequest(
            Reference: Peaks(event_), ReferenceStart: 0, ReferenceLength: 5,
            Candidate: Peaks(camera));

        var best = AudioSync.FindOffsets(request, take: 1)[0];

        Assert.Equal(7.0, best.OffsetSeconds, 1);
        Assert.True(best.Confidence > 0.7, $"confidence was {best.Confidence:0.000}");
    }

    [Fact]
    public void Unrelated_audio_produces_a_confidence_the_caller_can_refuse()
    {
        var request = new SyncRequest(
            Reference: Peaks(Envelope(seed: 11, buckets: 5 * Rate)),
            ReferenceStart: 0,
            ReferenceLength: 5,
            Candidate: Peaks(Envelope(seed: 12, buckets: 40 * Rate)));

        var best = AudioSync.FindOffsets(request, take: 1)[0];

        // Well under the 0.6 the editor treats as a match — the point is the separation from
        // the >0.9 a genuine one scores, not this exact number.
        Assert.True(best.Confidence < 0.5, $"confidence was {best.Confidence:0.000}");
    }

    [Fact]
    public void Equally_good_offsets_break_toward_where_the_clip_already_sits()
    {
        var event_ = Envelope(seed: 5, buckets: 3 * Rate);

        // The identical event twice, so both offsets correlate exactly as well.
        var file = Silence(30 * Rate);
        Place(file, at: 5 * Rate, what: event_);
        Place(file, at: 20 * Rate, what: event_);

        var reference = Peaks(event_);

        var near5 = AudioSync.FindOffsets(new SyncRequest(reference, 0, 3, Peaks(file))
        {
            PreferNear = 6.0,
        })[0];

        var near20 = AudioSync.FindOffsets(new SyncRequest(reference, 0, 3, Peaks(file))
        {
            PreferNear = 19.0,
        })[0];

        Assert.Equal(5.0, near5.OffsetSeconds, 1);
        Assert.Equal(20.0, near20.OffsetSeconds, 1);
    }

    [Fact]
    public void Distinct_offsets_are_returned_rather_than_one_ridge_reported_five_times()
    {
        var event_ = Envelope(seed: 8, buckets: 4 * Rate);

        var file = Silence(40 * Rate);
        Place(file, at: 3 * Rate, what: event_);
        Place(file, at: 25 * Rate, what: event_);

        var results = AudioSync.FindOffsets(
            new SyncRequest(Peaks(event_), 0, 4, Peaks(file)), take: 4);

        Assert.True(results.Count >= 2, $"expected at least two peaks, got {results.Count}");

        var offsets = results.Select(r => r.OffsetSeconds).ToList();
        Assert.Contains(offsets, o => Math.Abs(o - 3) < 0.2);
        Assert.Contains(offsets, o => Math.Abs(o - 25) < 0.2);
    }

    [Fact]
    public void A_candidate_shorter_than_the_reference_window_yields_nothing() =>
        Assert.Empty(AudioSync.FindOffsets(new SyncRequest(
            Reference: Peaks(Envelope(seed: 2, buckets: 10 * Rate)),
            ReferenceStart: 0,
            ReferenceLength: 10,
            Candidate: Peaks(Silence(2 * Rate)))));

    [Fact]
    public void Silence_yields_nothing_rather_than_an_arbitrary_offset() =>
        Assert.Empty(AudioSync.FindOffsets(new SyncRequest(
            Reference: Peaks(Silence(5 * Rate)),
            ReferenceStart: 0,
            ReferenceLength: 5,
            Candidate: Peaks(Envelope(seed: 4, buckets: 30 * Rate)))));

    [Fact]
    public void Mismatched_bucket_rates_are_rejected()
    {
        var request = new SyncRequest(
            Reference: new AudioPeaks(100, new float[100], new float[100]),
            ReferenceStart: 0,
            ReferenceLength: 1,
            Candidate: new AudioPeaks(50, new float[500], new float[500]));

        Assert.Throws<ArgumentException>(() => AudioSync.FindOffsets(request));
    }

    // ---- fixtures -----------------------------------------------------------------

    private const double AngleSeconds = 6;

    /// <summary>
    /// One recording holding the same event twice — the shape the driving use case has.
    /// </summary>
    private static (float[] File, double AngleOne, double AngleTwo) TwoAngles()
    {
        const double one = 4;
        const double two = 26;

        var event_ = Envelope(seed: 42, buckets: (int)(AngleSeconds * Rate));

        // The second angle is the same event heard from somewhere else: quieter, with its
        // own noise, which is what stops this being a trivially exact match.
        var second = new float[event_.Length];
        for (var i = 0; i < second.Length; i++) second[i] = event_[i] * 0.7f;
        AddNoise(second, relativeToDeviation: 0.35, seed: 43);

        var file = Silence(40 * Rate);
        Place(file, at: (int)(one * Rate), what: event_);
        Place(file, at: (int)(two * Rate), what: second);

        return (file, one, two);
    }

    /// <summary>
    /// A plausible loudness curve: a smoothed random walk, so it has the slow structure real
    /// audio has and never repeats itself inside a clip.
    /// </summary>
    /// <remarks>
    /// A periodic envelope would autocorrelate to many equally good peaks and make every
    /// assertion here meaningless, which is the same reason the harness's synthesised clip
    /// uses an aperiodic modulation.
    /// </remarks>
    private static float[] Envelope(int seed, int buckets)
    {
        var random = new Random(seed);
        var raw = new float[buckets];
        for (var i = 0; i < buckets; i++) raw[i] = (float)random.NextDouble();

        var smoothed = new float[buckets];
        const int window = 5;

        for (var i = 0; i < buckets; i++)
        {
            float sum = 0;
            var count = 0;

            for (var k = -window; k <= window; k++)
            {
                var j = i + k;
                if (j < 0 || j >= buckets) continue;
                sum += raw[j];
                count++;
            }

            smoothed[i] = sum / count;
        }

        return smoothed;
    }

    private static float[] Silence(int buckets) => new float[buckets];

    /// <summary>
    /// Adds white noise scaled to a fraction of the signal's own standard deviation.
    /// </summary>
    /// <remarks>
    /// Scaled rather than absolute, so "40% noise" means 40% of what there is to hear. An
    /// absolute amplitude looks reasonable and can easily be several times the deviation of
    /// a smooth envelope, which turns a robustness test into a test that the algorithm can
    /// find a signal buried under louder noise — a different and much harder question.
    /// </remarks>
    private static void AddNoise(float[] signal, double relativeToDeviation, int seed)
    {
        double sum = 0;
        double squares = 0;

        foreach (var value in signal)
        {
            sum += value;
            squares += (double)value * value;
        }

        var mean = sum / signal.Length;
        var deviation = Math.Sqrt(Math.Max(0, (squares / signal.Length) - (mean * mean)));

        // Uniform noise on [-a, a] has deviation a/sqrt(3).
        var amplitude = deviation * relativeToDeviation * Math.Sqrt(3);

        var random = new Random(seed);
        for (var i = 0; i < signal.Length; i++)
            signal[i] += (float)((random.NextDouble() - 0.5) * 2 * amplitude);
    }

    private static void Place(float[] target, int at, float[] what)
    {
        for (var i = 0; i < what.Length && at + i < target.Length; i++) target[at + i] = what[i];
    }

    private static float[] Pad(float[] values, int before, int total)
    {
        var padded = new float[total];
        Place(padded, before, values);
        return padded;
    }

    /// <summary>Wraps an envelope as symmetric min/max peaks, the way a real source reads.</summary>
    private static AudioPeaks Peaks(float[] envelope)
    {
        var min = new float[envelope.Length];
        var max = new float[envelope.Length];

        for (var i = 0; i < envelope.Length; i++)
        {
            min[i] = -envelope[i] / 2;
            max[i] = envelope[i] / 2;
        }

        return new AudioPeaks(Rate, min, max);
    }
}
