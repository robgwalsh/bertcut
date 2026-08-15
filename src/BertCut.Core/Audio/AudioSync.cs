namespace BertCut.Core.Audio;

/// <summary>One place the reference audio could sit inside the candidate source.</summary>
/// <param name="OffsetSeconds">Where in the candidate source the reference window begins.</param>
/// <param name="Confidence">
/// The normalised correlation coefficient there, from 0 to 1. Above about 0.6 means the same
/// sound; below about 0.4 usually means the two recordings have nothing in common and the
/// offset is noise dressed up as an answer.
/// </param>
public readonly record struct SyncCandidate(double OffsetSeconds, double Confidence);

/// <summary>What to line up, and what not to line it up with.</summary>
public readonly record struct SyncRequest(
    AudioPeaks Reference,
    double ReferenceStart,
    double ReferenceLength,
    AudioPeaks Candidate)
{
    /// <summary>
    /// A region of the candidate to refuse, in candidate seconds.
    /// </summary>
    /// <remarks>
    /// Set this when the reference and the candidate are the same file — see the class
    /// remarks on <see cref="AudioSync"/>. Null when they are different recordings, where
    /// every offset is admissible.
    /// </remarks>
    public (double Start, double End)? Exclude { get; init; }

    /// <summary>
    /// Where the caller currently believes the answer is, in candidate seconds.
    /// </summary>
    /// <remarks>
    /// Only breaks ties between offsets that correlate essentially as well as each other —
    /// a genuinely better match always wins. This is what makes the key idempotent: running
    /// it twice on an already-synced overlay does not walk it to an equally-good but
    /// different alignment.
    /// </remarks>
    public double? PreferNear { get; init; }
}

/// <summary>
/// Finds where a stretch of one recording's audio occurs in another's.
/// </summary>
/// <remarks>
/// <para>
/// This is what turns "the same event filmed twice" into an edit. The caller hands over a
/// window of the base track's audio and a whole candidate source, and gets back the offsets
/// at which the candidate contains that same sound, best first.
/// </para>
/// <para>
/// <b>The identity trap.</b> The overlay's source is very often the base's source — one
/// recording holding two angles played back to back, which is exactly the case this feature
/// exists for. Correlating the base window against that file finds two strong peaks: the
/// second angle, which is the answer, and the base window matching <em>itself</em>, which
/// scores a perfect 1.0 and is useless. Naively taking the highest peak returns the identity
/// match every time, and the feature looks like it silently does nothing.
/// <see cref="SyncRequest.Exclude"/> is how the caller says which region is the reference's
/// own, and any candidate overlapping it by more than
/// <see cref="MaximumExcludedOverlap"/> of the window is discarded before ranking.
/// </para>
/// <para>
/// Correlation is normalised, so a quiet passage and a loud one score comparably and the
/// result is a coefficient the UI can threshold on rather than an arbitrary magnitude.
/// Only full-overlap lags are considered, which keeps the ends of the candidate from
/// scoring highly on a short accidental match.
/// </para>
/// </remarks>
public static class AudioSync
{
    /// <summary>How much a candidate may overlap the excluded region before it is refused.</summary>
    private const double MaximumExcludedOverlap = 0.1;

    /// <summary>
    /// Confidence within which two offsets count as equally good, for
    /// <see cref="SyncRequest.PreferNear"/>.
    /// </summary>
    private const double TieBand = 0.02;

    /// <summary>
    /// Finds the offsets at which the reference window occurs in the candidate, best first.
    /// </summary>
    /// <remarks>
    /// More than one is returned so a caller can tell a decisive match from a field of
    /// equally plausible ones — the gap between the first and second is a better guide to
    /// "did this work" than the first one's confidence alone.
    /// </remarks>
    public static IReadOnlyList<SyncCandidate> FindOffsets(SyncRequest request, int take = 5)
    {
        ArgumentNullException.ThrowIfNull(request.Reference);
        ArgumentNullException.ThrowIfNull(request.Candidate);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);

        if (request.Reference.Rate != request.Candidate.Rate)
            throw new ArgumentException(
                "Reference and candidate envelopes must share a bucket rate.", nameof(request));

        var rate = request.Reference.Rate;
        var pattern = request.Reference.EnvelopeRange(request.ReferenceStart, request.ReferenceLength);
        var signal = request.Candidate.Envelope();

        // Only lags where the whole window fits are scored, so every score compares the same
        // amount of audio and a sliver of overlap at the very end cannot win.
        var lagCount = signal.Length - pattern.Length + 1;
        if (pattern.Length == 0 || lagCount <= 0) return [];

        var (patternMean, patternDeviation) = MeanAndDeviation(pattern);
        if (patternDeviation <= double.Epsilon) return [];

        // Centring the pattern makes the raw correlation the numerator of the normalised
        // coefficient outright: the cross terms involving the signal's own mean sum to zero
        // because the centred pattern does.
        var centred = new float[pattern.Length];
        for (var i = 0; i < pattern.Length; i++) centred[i] = (float)(pattern[i] - patternMean);

        var raw = Fft.CrossCorrelate(signal, centred);
        var (sums, squares) = PrefixSums(signal);

        var scores = new double[lagCount];
        for (var lag = 0; lag < lagCount; lag++)
        {
            var deviation = WindowDeviation(sums, squares, lag, pattern.Length);
            scores[lag] = deviation <= double.Epsilon
                ? 0
                : raw[lag] / (pattern.Length * deviation * patternDeviation);
        }

        return Rank(scores, request, rate, pattern.Length, take);
    }

    /// <summary>
    /// Picks the well-separated peaks, drops any that overlap the excluded region, and
    /// orders what is left.
    /// </summary>
    /// <remarks>
    /// Peaks are suppressed within half a window of an already-taken one, because a real
    /// match produces a broad ridge rather than a spike — without that, the top five results
    /// would all describe the same alignment a bucket apart from each other.
    /// </remarks>
    private static List<SyncCandidate> Rank(
        double[] scores, SyncRequest request, int rate, int windowBuckets, int take)
    {
        var guard = Math.Max(1, windowBuckets / 2);

        var order = new int[scores.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => scores[b].CompareTo(scores[a]));

        var windowSeconds = windowBuckets / (double)rate;
        var chosen = new List<SyncCandidate>(take);
        var taken = new List<int>(take);

        foreach (var lag in order)
        {
            if (chosen.Count == take) break;
            if (scores[lag] <= 0) break;

            var tooClose = false;
            foreach (var already in taken)
                if (Math.Abs(already - lag) < guard) { tooClose = true; break; }

            if (tooClose) continue;

            var offset = lag / (double)rate;
            if (IsExcluded(request.Exclude, offset, windowSeconds)) continue;

            taken.Add(lag);
            chosen.Add(new SyncCandidate(offset, scores[lag]));
        }

        if (request.PreferNear is not { } near || chosen.Count < 2) return chosen;

        // Within the tie band the offsets are indistinguishable on the audio alone, so the
        // one nearest where the caller already put the clip wins and the operation is
        // idempotent.
        var best = chosen[0].Confidence;
        chosen.Sort((a, b) =>
        {
            var aTied = best - a.Confidence <= TieBand;
            var bTied = best - b.Confidence <= TieBand;

            if (aTied && bTied)
                return Math.Abs(a.OffsetSeconds - near).CompareTo(Math.Abs(b.OffsetSeconds - near));

            return b.Confidence.CompareTo(a.Confidence);
        });

        return chosen;
    }

    private static bool IsExcluded((double Start, double End)? exclude, double offset, double length)
    {
        if (exclude is not { } region) return false;

        var overlap = Math.Min(offset + length, region.End) - Math.Max(offset, region.Start);
        return overlap > length * MaximumExcludedOverlap;
    }

    private static (double Mean, double Deviation) MeanAndDeviation(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty) return (0, 0);

        double sum = 0;
        double squares = 0;

        foreach (var value in values)
        {
            sum += value;
            squares += (double)value * value;
        }

        var mean = sum / values.Length;
        var variance = (squares / values.Length) - (mean * mean);

        return (mean, Math.Sqrt(Math.Max(0, variance)));
    }

    /// <summary>Running totals, so any window's mean and deviation cost two subtractions.</summary>
    private static (double[] Sums, double[] Squares) PrefixSums(ReadOnlySpan<float> values)
    {
        var sums = new double[values.Length + 1];
        var squares = new double[values.Length + 1];

        for (var i = 0; i < values.Length; i++)
        {
            sums[i + 1] = sums[i] + values[i];
            squares[i + 1] = squares[i] + ((double)values[i] * values[i]);
        }

        return (sums, squares);
    }

    private static double WindowDeviation(double[] sums, double[] squares, int start, int length)
    {
        var mean = (sums[start + length] - sums[start]) / length;
        var variance = ((squares[start + length] - squares[start]) / length) - (mean * mean);

        return Math.Sqrt(Math.Max(0, variance));
    }
}
