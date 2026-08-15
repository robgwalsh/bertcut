using BertCut.Core.Audio;
using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Media.Decode;

namespace BertCut.Media.Audio;

/// <summary>Why a sync did not produce an answer.</summary>
public enum SyncFailure
{
    None,

    /// <summary>One of the two sources has no audio track at all.</summary>
    NoAudio,

    /// <summary>The envelope for a source has not finished building yet.</summary>
    NotAnalysed,

    /// <summary>The marked range is too short to identify a moment in a recording.</summary>
    RangeTooShort,

    /// <summary>Nothing in the candidate correlates well enough to act on.</summary>
    NoConfidentMatch,
}

/// <summary>What a sync attempt concluded.</summary>
/// <param name="SourceStartFrame">Where the overlay should start in its own source.</param>
/// <param name="Confidence">The verified coefficient, 0 to 1.</param>
/// <param name="Runner">The next best offset's confidence, for judging decisiveness.</param>
public readonly record struct SyncOutcome(
    long SourceStartFrame,
    double Confidence,
    double Runner,
    SyncFailure Failure)
{
    public bool Succeeded => Failure == SyncFailure.None;

    public static SyncOutcome Failed(SyncFailure failure) => new(0, 0, 0, failure);
}

/// <summary>
/// Lines an overlay's content up with the base track underneath it, by their sound.
/// </summary>
/// <remarks>
/// <para>
/// Two passes. The coarse one correlates the cached 100 Hz envelopes over the whole of both
/// sources, which finds the answer anywhere in the files — the two angles may be minutes
/// apart. The fine one decodes real audio around that answer and correlates it at 1 kHz,
/// which is both a refinement and, more usefully, an independent check: a coarse peak that
/// was an artifact of the cached envelope's resolution does not survive being looked at ten
/// times more closely. The confidence reported to the user is the fine pass's.
/// </para>
/// <para>
/// See <see cref="AudioSync"/> for why the overlay's own position in its source has to be
/// excluded when the overlay and the base share a file — which, for the case this feature
/// exists to serve, they usually do.
/// </para>
/// </remarks>
public static class OverlaySync
{
    /// <summary>Bucket rate for the verification pass.</summary>
    private const int FineRate = 1000;

    /// <summary>How far either side of the coarse answer the fine pass looks.</summary>
    private const double FineWindowSeconds = 0.5;

    /// <summary>Below this the answer is not offered at all.</summary>
    public const double MinimumConfidence = 0.55;

    /// <summary>
    /// A range shorter than this rarely identifies a unique moment, and short windows are
    /// where confident-looking wrong answers come from.
    /// </summary>
    public const double MinimumReferenceSeconds = 0.75;

    /// <summary>The longest reference window used; more audio buys nothing and costs time.</summary>
    private const double MaximumReferenceSeconds = 20;

    /// <summary>
    /// Finds where <paramref name="overlaySourceId"/> should start so its content matches the
    /// base track over <paramref name="range"/>.
    /// </summary>
    /// <param name="peaksOf">
    /// Supplies a source's cached envelope, or null when it has none yet. Taking this as a
    /// function keeps the decision about building and caching with the caller, who knows
    /// whether it is allowed to block.
    /// </param>
    public static SyncOutcome Solve(
        Project project,
        Core.Time.FrameRange range,
        int overlaySourceId,
        long currentSourceStartFrame,
        Func<int, SourceIndex> indexOf,
        Func<int, AudioPeaks?> peaksOf)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(indexOf);
        ArgumentNullException.ThrowIfNull(peaksOf);

        if (project.Base.IsEmpty) return SyncOutcome.Failed(SyncFailure.NoAudio);

        var baseSegment = SegmentAt(project, range.Start);
        var baseSource = project.RequireSource(baseSegment.SourceId);
        var overlaySource = project.FindSource(overlaySourceId);

        if (overlaySource is null) return SyncOutcome.Failed(SyncFailure.NoAudio);
        if (!baseSource.HasAudio || !overlaySource.HasAudio) return SyncOutcome.Failed(SyncFailure.NoAudio);

        var basePeaks = peaksOf(baseSource.Id);
        var overlayPeaks = peaksOf(overlaySource.Id);
        if (basePeaks is null || overlayPeaks is null) return SyncOutcome.Failed(SyncFailure.NotAnalysed);

        var baseIndex = indexOf(baseSource.Id);
        var overlayIndex = indexOf(overlaySource.Id);

        // The reference is the base track's own audio under the overlay, in base source
        // seconds — read from the timestamp table, so this is right under variable rate.
        var into = range.Start - baseSegment.TimelineStart;
        var referenceStartFrame = baseSegment.SourceStartFrame + into;
        var referenceStart = baseIndex.SecondsOf(
            Math.Clamp(referenceStartFrame, 0, baseIndex.FrameCount - 1));

        var referenceLength = Math.Min(
            range.Length / project.Output.FrameRate.Approx, MaximumReferenceSeconds);

        if (referenceLength < MinimumReferenceSeconds)
            return SyncOutcome.Failed(SyncFailure.RangeTooShort);

        var current = overlayIndex.SecondsOf(
            Math.Clamp(currentSourceStartFrame, 0, overlayIndex.FrameCount - 1));

        var request = new SyncRequest(basePeaks, referenceStart, referenceLength, overlayPeaks)
        {
            // Only when they are the same file: otherwise every offset is admissible.
            Exclude = baseSource.Id == overlaySource.Id
                ? (referenceStart, referenceStart + referenceLength)
                : null,
            PreferNear = current,
        };

        var coarse = AudioSync.FindOffsets(request);
        if (coarse.Count == 0) return SyncOutcome.Failed(SyncFailure.NoConfidentMatch);

        var runnerUp = coarse.Count > 1 ? coarse[1].Confidence : 0;

        var verified = Verify(
            baseSource.Path, referenceStart, referenceLength,
            overlaySource.Path, coarse[0].OffsetSeconds,
            project.Output.SampleRate);

        if (verified is not { } result || result.Confidence < MinimumConfidence)
            return SyncOutcome.Failed(SyncFailure.NoConfidentMatch);

        var frame = overlayIndex.FrameOf(
            (long)Math.Round(result.OffsetSeconds / overlayIndex.TimeBase.Approx));

        var limit = Math.Max(0, overlaySource.FrameCount - range.Length);

        return new SyncOutcome(
            Math.Clamp(frame, 0, limit), result.Confidence, runnerUp, SyncFailure.None);
    }

    /// <summary>
    /// Re-correlates real audio around the coarse answer, at ten times the resolution.
    /// </summary>
    /// <remarks>
    /// Returns null when either window cannot be decoded, which the caller treats as no
    /// match — an answer that cannot be checked is not offered.
    /// </remarks>
    private static SyncCandidate? Verify(
        string basePath,
        double referenceStart,
        double referenceLength,
        string overlayPath,
        double coarseOffset,
        int sampleRate)
    {
        // A long reference is unnecessary here: the coarse pass already said where to look,
        // and this only has to confirm it and sharpen it.
        var length = Math.Min(referenceLength, 4);

        try
        {
            using var baseDecoder = new AudioDecoder(basePath, sampleRate);
            using var overlayDecoder = new AudioDecoder(overlayPath, sampleRate);

            var reference = AudioPeaksBuilder.BuildRange(
                baseDecoder, referenceStart, length, FineRate);

            var searchStart = Math.Max(0, coarseOffset - FineWindowSeconds);
            var candidate = AudioPeaksBuilder.BuildRange(
                overlayDecoder, searchStart, length + (2 * FineWindowSeconds), FineRate);

            var refined = AudioSync.FindOffsets(
                new SyncRequest(reference, 0, length, candidate), take: 1);

            if (refined.Count == 0) return null;

            return refined[0] with { OffsetSeconds = searchStart + refined[0].OffsetSeconds };
        }
        catch (FfmpegDecodeException)
        {
            return null;
        }
    }

    private static BaseSegment SegmentAt(Project project, long frame)
    {
        foreach (var segment in project.Base)
            if (segment.Timeline.Contains(frame)) return segment;

        return project.Base[0];
    }
}
