using BertCut.Core.Audio;
using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Core.Timeline;
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

    /// <summary>
    /// The sound was found in the base recording, but at a moment that was cut out of the
    /// timeline.
    /// </summary>
    /// <remarks>
    /// Only reachable in the direction that moves a clip along the timeline. Reported rather
    /// than rounded to the nearest surviving frame: the match is real, and the honest thing to
    /// say is that the footage it lines up with is no longer in the edit.
    /// </remarks>
    MatchNotOnTimeline,
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

/// <summary>What a sync attempt concluded, when the clip is what moves.</summary>
/// <param name="TimelineStartFrame">Where the overlay should start on the timeline.</param>
/// <param name="Confidence">The verified coefficient, 0 to 1.</param>
/// <param name="Runner">The next best offset's confidence, for judging decisiveness.</param>
/// <remarks>
/// Separate from <see cref="SyncOutcome"/> because the two directions answer different
/// questions and a single record would leave the caller to remember which of two frame numbers
/// its answer was in. See <see cref="OverlaySync.SolveTimelinePosition"/>.
/// </remarks>
public readonly record struct TimelineSyncOutcome(
    long TimelineStartFrame,
    double Confidence,
    double Runner,
    SyncFailure Failure)
{
    public bool Succeeded => Failure == SyncFailure.None;

    public static TimelineSyncOutcome Failed(SyncFailure failure) => new(0, 0, 0, failure);
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

        var match = Correlate(
            basePeaks, baseSource.Path, referenceStart, referenceLength,
            overlayPeaks, overlaySource.Path,
            sameFile: baseSource.Id == overlaySource.Id,
            preferNear: current,
            project.Output.SampleRate);

        if (match is not { } found) return SyncOutcome.Failed(SyncFailure.NoConfidentMatch);

        var frame = overlayIndex.FrameOf(
            (long)Math.Round(found.OffsetSeconds / overlayIndex.TimeBase.Approx));

        var limit = Math.Max(0, overlaySource.FrameCount - range.Length);

        return new SyncOutcome(
            Math.Clamp(frame, 0, limit), found.Confidence, found.Runner, SyncFailure.None);
    }

    /// <summary>
    /// Finds where on the timeline a clip belongs, so its content lines up with the base
    /// track's sound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other direction from <see cref="Solve"/>, and the one an overlay being placed wants.
    /// There the timeline range was fixed and the question was where in the source to read;
    /// here the content is what the user chose and must not change, so what moves is the clip.
    /// Reference and candidate simply swap: the pattern is the overlay's own audio, and it is
    /// hunted for in the base recording.
    /// </para>
    /// <para>
    /// The identity trap is unchanged and still has to be refused. When the overlay's source
    /// <i>is</i> the base's — one recording holding two angles, the case this feature exists
    /// for — the overlay's audio matches itself perfectly wherever it already sits, and taking
    /// the highest peak would answer "leave it exactly where it is" every time.
    /// </para>
    /// </remarks>
    /// <param name="lengthFrames">The clip's length on the timeline, in output frames.</param>
    /// <param name="currentTimelineStart">
    /// Where the clip sits now. Used to break ties toward staying put, so running this twice
    /// does not walk a clip that is already right.
    /// </param>
    public static TimelineSyncOutcome SolveTimelinePosition(
        Project project,
        int overlaySourceId,
        long overlaySourceStartFrame,
        long lengthFrames,
        long currentTimelineStart,
        Func<int, SourceIndex> indexOf,
        Func<int, AudioPeaks?> peaksOf)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(indexOf);
        ArgumentNullException.ThrowIfNull(peaksOf);

        if (project.Base.IsEmpty) return TimelineSyncOutcome.Failed(SyncFailure.NoAudio);

        var baseSegment = SegmentAt(project, currentTimelineStart);
        var baseSource = project.RequireSource(baseSegment.SourceId);
        var overlaySource = project.FindSource(overlaySourceId);

        if (overlaySource is null) return TimelineSyncOutcome.Failed(SyncFailure.NoAudio);
        if (!baseSource.HasAudio || !overlaySource.HasAudio)
            return TimelineSyncOutcome.Failed(SyncFailure.NoAudio);

        var basePeaks = peaksOf(baseSource.Id);
        var overlayPeaks = peaksOf(overlaySource.Id);
        if (basePeaks is null || overlayPeaks is null)
            return TimelineSyncOutcome.Failed(SyncFailure.NotAnalysed);

        var baseIndex = indexOf(baseSource.Id);
        var overlayIndex = indexOf(overlaySource.Id);

        // The reference is the clip's own audio, in the overlay source's seconds.
        var referenceStart = overlayIndex.SecondsOf(
            Math.Clamp(overlaySourceStartFrame, 0, overlayIndex.FrameCount - 1));

        var referenceLength = Math.Min(
            lengthFrames / project.Output.FrameRate.Approx, MaximumReferenceSeconds);

        if (referenceLength < MinimumReferenceSeconds)
            return TimelineSyncOutcome.Failed(SyncFailure.RangeTooShort);

        // Ties break toward where the clip already is, expressed in the candidate's seconds —
        // which for the base track means reading the timeline position back through the
        // segment it lands in.
        var into = currentTimelineStart - baseSegment.TimelineStart;
        var preferNear = baseIndex.SecondsOf(Math.Clamp(
            baseSegment.SourceStartFrame + into, 0, baseIndex.FrameCount - 1));

        var match = Correlate(
            overlayPeaks, overlaySource.Path, referenceStart, referenceLength,
            basePeaks, baseSource.Path,
            sameFile: baseSource.Id == overlaySource.Id,
            preferNear: preferNear,
            project.Output.SampleRate);

        if (match is not { } found) return TimelineSyncOutcome.Failed(SyncFailure.NoConfidentMatch);

        var baseFrame = baseIndex.FrameOf(
            (long)Math.Round(found.OffsetSeconds / baseIndex.TimeBase.Approx));

        // The answer is a moment in the recording; the timeline is only what survived the
        // cutting, so it may not be on it at all — and if the edit shows it twice, the one
        // nearest where the clip already sits is the one that moves it least.
        var at = new TimelineResolver(project)
            .TimelineFrameOf(baseSource.Id, baseFrame, currentTimelineStart);

        if (at is null) return TimelineSyncOutcome.Failed(SyncFailure.MatchNotOnTimeline);

        var limit = Math.Max(0, project.DurationFrames - lengthFrames);

        return new TimelineSyncOutcome(
            Math.Clamp(at.Value, 0, limit), found.Confidence, found.Runner, SyncFailure.None);
    }

    /// <summary>
    /// The two passes, in whichever direction the caller is asking.
    /// </summary>
    /// <remarks>
    /// Both entry points want the same thing — a coarse answer over the cached envelopes,
    /// independently re-checked against real audio — and differ only in which recording plays
    /// the part of the pattern. Keeping one copy is what stops the identity exclusion from
    /// being fixed in one direction and forgotten in the other.
    /// </remarks>
    private static (double OffsetSeconds, double Confidence, double Runner)? Correlate(
        AudioPeaks referencePeaks,
        string referencePath,
        double referenceStart,
        double referenceLength,
        AudioPeaks candidatePeaks,
        string candidatePath,
        bool sameFile,
        double preferNear,
        int sampleRate)
    {
        var request = new SyncRequest(referencePeaks, referenceStart, referenceLength, candidatePeaks)
        {
            // Only when they are the same file: otherwise every offset is admissible.
            Exclude = sameFile ? (referenceStart, referenceStart + referenceLength) : null,
            PreferNear = preferNear,
        };

        var coarse = AudioSync.FindOffsets(request);
        if (coarse.Count == 0) return null;

        var runnerUp = coarse.Count > 1 ? coarse[1].Confidence : 0;

        var verified = Verify(
            referencePath, referenceStart, referenceLength,
            candidatePath, coarse[0].OffsetSeconds,
            sampleRate);

        if (verified is not { } result || result.Confidence < MinimumConfidence) return null;

        return (result.OffsetSeconds, result.Confidence, runnerUp);
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
