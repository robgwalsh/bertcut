using BertCut.Core.Audio;
using BertCut.Media.Decode;

namespace BertCut.Media.Audio;

/// <summary>
/// Reduces a source's audio to a fixed-rate min/max envelope in one decode pass.
/// </summary>
/// <remarks>
/// Separate from the cache that stores the result so it can be tested against a decoder
/// directly, without a state directory in play.
/// </remarks>
public static class AudioPeaksBuilder
{
    /// <summary>
    /// Reads <paramref name="decoder"/> to the end, bucketing samples into peaks.
    /// </summary>
    /// <remarks>
    /// Channels are collapsed by taking the extremes across all of them rather than by
    /// averaging. Averaging would cancel anything panned hard the other way, and a sound
    /// that appears in one channel only is exactly the kind of transient the correlation
    /// keys on.
    /// </remarks>
    public static AudioPeaks Build(
        AudioDecoder decoder,
        int rate = AudioPeaks.DefaultRate,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentOutOfRangeException.ThrowIfLessThan(rate, 1);

        var samplesPerBucket = Math.Max(1, decoder.SampleRate / rate);
        var channels = decoder.Channels;

        var buffer = new float[samplesPerBucket * channels];
        var min = new List<float>(1024);
        var max = new List<float>(1024);

        var report = decoder.DurationSeconds > 0 ? decoder.DurationSeconds : 0;
        var reported = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frames = decoder.Read(buffer, 0, samplesPerBucket);
            if (frames == 0) break;

            var low = 0f;
            var high = 0f;

            var samples = frames * channels;
            for (var i = 0; i < samples; i++)
            {
                var value = buffer[i];
                if (value < low) low = value;
                if (value > high) high = value;
            }

            min.Add(low);
            max.Add(high);

            // Reported per second of audio rather than per bucket; a hundred callbacks a
            // second of decoded audio would cost more than the decode.
            if (progress is null || report <= 0) continue;

            var seconds = min.Count / rate;
            if (seconds == reported) continue;

            reported = seconds;
            progress.Report(Math.Clamp(seconds / report, 0, 1));
        }

        return new AudioPeaks(rate, [.. min], [.. max]);
    }

    /// <summary>
    /// Builds an envelope for one stretch of a source, at whatever resolution is asked for.
    /// </summary>
    /// <remarks>
    /// The fine pass of a sync uses this at a much higher rate than the cached whole-file
    /// envelope, over a window of a second or two. Building the whole file at that rate
    /// would be ten times the storage for detail that only ever matters near an answer that
    /// has already been found.
    /// </remarks>
    public static AudioPeaks BuildRange(
        AudioDecoder decoder, double startSeconds, double lengthSeconds, int rate)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentOutOfRangeException.ThrowIfLessThan(rate, 1);

        decoder.SeekTo(Math.Max(0, startSeconds));

        var samplesPerBucket = Math.Max(1, decoder.SampleRate / rate);
        var buckets = Math.Max(0, (int)Math.Round(lengthSeconds * rate));
        var channels = decoder.Channels;

        var buffer = new float[samplesPerBucket * channels];
        var min = new float[buckets];
        var max = new float[buckets];

        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var frames = decoder.Read(buffer, 0, samplesPerBucket);
            if (frames == 0) break;

            var low = 0f;
            var high = 0f;

            var samples = frames * channels;
            for (var i = 0; i < samples; i++)
            {
                var value = buffer[i];
                if (value < low) low = value;
                if (value > high) high = value;
            }

            min[bucket] = low;
            max[bucket] = high;
        }

        return new AudioPeaks(rate, min, max);
    }
}
