namespace BertCut.Core.Audio;

/// <summary>
/// A source's audio reduced to a fixed-rate min/max envelope.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, one table. The waveform lane under the timeline draws <see cref="Min"/> and
/// <see cref="Max"/> directly, and <see cref="AudioSync"/> correlates
/// <see cref="EnvelopeAt"/> — their difference, which is the peak-to-peak amplitude in that
/// bucket.
/// </para>
/// <para>
/// It is deliberately the <em>envelope</em> that is correlated rather than the waveform.
/// Two cameras recording one event stand in different places, so the same sound reaches
/// them with different phase and a different amount of room in it; sample-level correlation
/// is sensitive to both, while the shape of the loudness over time is not. That shape is
/// what "the same event" actually means here.
/// </para>
/// <para>
/// At the default rate this is 800 bytes a second — about 3 MB for an hour — which is small
/// enough to cache on disk per source and hold in memory for every open source at once.
/// </para>
/// </remarks>
public sealed class AudioPeaks
{
    /// <summary>
    /// Buckets per second. 100 gives 10 ms resolution, already finer than a frame at any
    /// rate this editor sees, so the coarse correlation pass alone lands within a frame.
    /// </summary>
    public const int DefaultRate = 100;

    public AudioPeaks(int rate, float[] min, float[] max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rate, 1);
        ArgumentNullException.ThrowIfNull(min);
        ArgumentNullException.ThrowIfNull(max);

        if (min.Length != max.Length)
            throw new ArgumentException("Min and max must be the same length.", nameof(max));

        Rate = rate;
        Min = min;
        Max = max;
    }

    /// <summary>Buckets per second.</summary>
    public int Rate { get; }

    /// <summary>The most negative sample in each bucket.</summary>
    public float[] Min { get; }

    /// <summary>The most positive sample in each bucket.</summary>
    public float[] Max { get; }

    public int Count => Min.Length;

    /// <summary>The length of the analysed audio, in seconds.</summary>
    public double DurationSeconds => Count / (double)Rate;

    /// <summary>Peak-to-peak amplitude in a bucket — the value correlation runs on.</summary>
    public float EnvelopeAt(int bucket) =>
        bucket < 0 || bucket >= Count ? 0f : Max[bucket] - Min[bucket];

    /// <summary>Where a bucket begins, in seconds.</summary>
    public double SecondsOf(int bucket) => bucket / (double)Rate;

    /// <summary>The bucket containing <paramref name="seconds"/>, clamped into range.</summary>
    public int BucketOf(double seconds) =>
        Math.Clamp((int)(seconds * Rate), 0, Math.Max(0, Count - 1));

    /// <summary>
    /// Copies the envelope over a time range into a new array.
    /// </summary>
    /// <remarks>
    /// Buckets outside the analysed audio come back as zero rather than being trimmed away,
    /// so the caller's window is always the length it asked for and its lag arithmetic does
    /// not have to account for clipping at either end.
    /// </remarks>
    public float[] EnvelopeRange(double startSeconds, double lengthSeconds)
    {
        var length = Math.Max(0, (int)Math.Round(lengthSeconds * Rate));
        var start = (int)Math.Round(startSeconds * Rate);

        var window = new float[length];
        for (var i = 0; i < length; i++) window[i] = EnvelopeAt(start + i);

        return window;
    }

    /// <summary>The whole envelope, one value per bucket.</summary>
    public float[] Envelope()
    {
        var all = new float[Count];
        for (var i = 0; i < Count; i++) all[i] = Max[i] - Min[i];
        return all;
    }
}
