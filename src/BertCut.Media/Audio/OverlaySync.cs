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

/// <summary>One place an overlay could go, and how well its sound fits there.</summary>
/// <param name="Frame">
/// Measured in whichever space the outcome carrying it is measured in — a frame of the
/// overlay's source for <see cref="SyncOutcome"/>, a frame of the timeline for
/// <see cref="TimelineSyncOutcome"/>.
/// </param>
public readonly record struct SyncMatch(long Frame, double Confidence);

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
    private readonly IReadOnlyList<SyncMatch>? _matches;

    /// <summary>Every offset worth offering, best first.</summary>
    /// <remarks>
    /// Populated even when <see cref="SyncFailure.NoConfidentMatch"/> is reported: that failure
    /// means nothing was strong enough to move a clip on its own authority, not that nothing
    /// was found. A refusal with nothing behind it is what makes this feature feel broken when
    /// it is only being careful, so the best guess travels with it and the user can take it.
    /// Read through the property rather than the field so a default instance answers empty.
    /// </remarks>
    public IReadOnlyList<SyncMatch> Matches
    {
        get => _matches ?? [];
        init => _matches = value;
    }

    public bool Succeeded => Failure == SyncFailure.None;

    public static SyncOutcome Failed(SyncFailure failure) => new(0, 0, 0, failure);

    public static SyncOutcome Failed(SyncFailure failure, IReadOnlyList<SyncMatch> matches) =>
        new(0, 0, 0, failure) { Matches = matches };
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
    private readonly IReadOnlyList<SyncMatch>? _matches;

    /// <inheritdoc cref="SyncOutcome.Matches"/>
    public IReadOnlyList<SyncMatch> Matches
    {
        get => _matches ?? [];
        init => _matches = value;
    }

    public bool Succeeded => Failure == SyncFailure.None;

    public static TimelineSyncOutcome Failed(SyncFailure failure) => new(0, 0, 0, failure);

    public static TimelineSyncOutcome Failed(
        SyncFailure failure, IReadOnlyList<SyncMatch> matches) =>
        new(0, 0, 0, failure) { Matches = matches };
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
    /// <remarks>
    /// 4 ms buckets — an eighth of a frame at 30 fps, and still a measure of <em>loudness</em>.
    /// It was 1000 Hz, which at 48 kHz is 48 samples: about one cycle of a 1 kHz tone, at which
    /// point peak-to-peak stops being a level and becomes waveform detail. Two microphones
    /// standing in different places disagree about waveform detail however well they agree
    /// about the sound — which is the reason <see cref="AudioPeaks"/> correlates an envelope in
    /// the first place, thrown away by measuring one this finely. On the two-camera fixture in
    /// <c>OverlaySyncTests</c> a single correct alignment scores 0.87 at 100 Hz, 0.83 at
    /// 250 Hz, 0.62 at 500 Hz and 0.59 at 1000 Hz. Resolution above 250 Hz was never the scarce
    /// thing here; agreement was.
    /// </remarks>
    private const int FineRate = 250;

    /// <summary>How far either side of the coarse answer the fine pass looks.</summary>
    private const double FineWindowSeconds = 0.5;

    /// <summary>Below this the answer is not offered at all.</summary>
    /// <remarks>
    /// Read against the <em>coarse</em> pass. That is the one that answers "is this the same
    /// sound", it is the one whose score <see cref="SyncCandidate"/> describes, and it is the
    /// one computed over the whole of both recordings rather than over a second either side of
    /// a guess.
    /// </remarks>
    public const double MinimumConfidence = 0.55;

    /// <summary>How well the fine pass must agree before a coarse answer is acted on.</summary>
    /// <remarks>
    /// Deliberately looser than <see cref="MinimumConfidence"/>, because it is a different
    /// question. The fine pass is not asked whether the two recordings hold the same sound —
    /// the coarse pass settled that over the whole file. It is asked whether this particular
    /// alignment survives a closer look. A confirmation made to clear the same bar as the
    /// judgement is just the judgement again, taken on less evidence, and gating on it is what
    /// used to refuse two-camera footage that the coarse pass had identified outright.
    /// </remarks>
    private const double MinimumFineConfidence = 0.35;

    /// <summary>
    /// A range shorter than this rarely identifies a unique moment, and short windows are
    /// where confident-looking wrong answers come from.
    /// </summary>
    public const double MinimumReferenceSeconds = 0.75;

    /// <summary>The longest reference window used; more audio buys nothing and costs time.</summary>
    private const double MaximumReferenceSeconds = 20;

    /// <summary>How much of the reference the fine pass looks at.</summary>
    /// <remarks>
    /// The coarse pass has already said where to look, so this only has to confirm it and
    /// sharpen it — and it is decoded audio rather than a cached envelope, so it is the
    /// expensive half.
    /// </remarks>
    private const double FineReferenceSeconds = 4;

    /// <summary>
    /// How many of the coarse pass's offsets are looked at closely.
    /// </summary>
    /// <remarks>
    /// <see cref="AudioSync.FindOffsets"/> returns a ranked field precisely so the best of them
    /// is not the only one considered, and this used to check the first and discard the rest —
    /// so a true match that ranked second, behind an artifact of the envelope's resolution or a
    /// passage the recording happens to repeat, failed the whole operation while sitting
    /// unexamined in the list.
    /// </remarks>
    private const int Considering = 5;

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

        var considered = Correlate(
            basePeaks, baseSource.Path, referenceStart, referenceLength,
            overlayPeaks, overlaySource.Path,
            sameFile: baseSource.Id == overlaySource.Id,
            preferNear: current,
            project.Output.SampleRate);

        if (considered.Count == 0) return SyncOutcome.Failed(SyncFailure.NoConfidentMatch);

        var limit = Math.Max(0, overlaySource.FrameCount - range.Length);

        var matches = new List<SyncMatch>(considered.Count);
        foreach (var candidate in considered)
        {
            var at = overlayIndex.FrameOf(
                (long)Math.Round(candidate.OffsetSeconds / overlayIndex.TimeBase.Approx));

            matches.Add(new SyncMatch(Math.Clamp(at, 0, limit), candidate.Confidence));
        }

        if (!considered[0].Trusted)
            return SyncOutcome.Failed(SyncFailure.NoConfidentMatch, matches);

        var runner = considered.Count > 1 ? considered[1].Confidence : 0;

        return new SyncOutcome(
            matches[0].Frame, considered[0].Confidence, runner, SyncFailure.None)
        {
            Matches = matches,
        };
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

        var considered = Correlate(
            overlayPeaks, overlaySource.Path, referenceStart, referenceLength,
            basePeaks, baseSource.Path,
            sameFile: baseSource.Id == overlaySource.Id,
            preferNear: preferNear,
            project.Output.SampleRate);

        if (considered.Count == 0) return TimelineSyncOutcome.Failed(SyncFailure.NoConfidentMatch);

        var limit = Math.Max(0, project.DurationFrames - lengthFrames);
        var resolver = new TimelineResolver(project);

        // The answer is a moment in the recording; the timeline is only what survived the
        // cutting, so it may not be on it at all — and if the edit shows it twice, the one
        // nearest where the clip already sits is the one that moves it least.
        long? Place(Considered candidate)
        {
            var baseFrame = baseIndex.FrameOf(
                (long)Math.Round(candidate.OffsetSeconds / baseIndex.TimeBase.Approx));

            return resolver.TimelineFrameOf(baseSource.Id, baseFrame, currentTimelineStart) is { } at
                ? Math.Clamp(at, 0, limit)
                : null;
        }

        var matches = new List<SyncMatch>(considered.Count);
        foreach (var candidate in considered)
            if (Place(candidate) is { } frame)
                matches.Add(new SyncMatch(frame, candidate.Confidence));

        if (!considered[0].Trusted)
            return TimelineSyncOutcome.Failed(SyncFailure.NoConfidentMatch, matches);

        // Reported rather than rounded to the nearest surviving frame: the match is real, and
        // the honest thing to say is that the footage it lines up with is no longer in the
        // edit. Asked of the answer itself, not of the field — a weaker offset that happens to
        // survive the cut is not what was found.
        if (Place(considered[0]) is not { } start)
            return TimelineSyncOutcome.Failed(SyncFailure.MatchNotOnTimeline, matches);

        var runner = considered.Count > 1 ? considered[1].Confidence : 0;

        return new TimelineSyncOutcome(
            start, considered[0].Confidence, runner, SyncFailure.None)
        {
            Matches = matches,
        };
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
    private static IReadOnlyList<Considered> Correlate(
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

        var coarse = AudioSync.FindOffsets(request, Considering);
        if (coarse.Count == 0) return [];

        // Which stretch of the reference the fine pass looks at does not depend on the
        // candidate, so it is chosen once.
        var fineLength = Math.Min(referenceLength, FineReferenceSeconds);
        var fineStart = InformativeStart(referencePeaks, referenceStart, referenceLength, fineLength);

        var considered = new List<Considered>(coarse.Count);

        try
        {
            using var referenceDecoder = new AudioDecoder(referencePath, sampleRate);
            using var candidateDecoder = new AudioDecoder(candidatePath, sampleRate);

            var pattern = AudioPeaksBuilder.BuildRange(
                referenceDecoder, fineStart, fineLength, FineRate);

            // The reference window is read once and correlated against each candidate's
            // neighbourhood in turn. Decoding it per candidate would be the same answer at
            // four times the cost.
            foreach (var candidate in coarse)
            {
                var refined = Refine(
                    pattern, fineStart - referenceStart, fineLength,
                    candidateDecoder, candidate.OffsetSeconds);

                considered.Add(refined is { } fine
                    ? new Considered(fine.OffsetSeconds, candidate.Confidence, fine.Confidence)
                    : new Considered(candidate.OffsetSeconds, candidate.Confidence, 0));
            }
        }
        catch (FfmpegDecodeException)
        {
            // An answer that could not be checked is still an answer worth offering; it is
            // only barred from being applied on its own, which Confirmed already says.
            for (var i = considered.Count; i < coarse.Count; i++)
                considered.Add(new Considered(coarse[i].OffsetSeconds, coarse[i].Confidence, 0));
        }

        // A confirmed alignment beats an unconfirmed one however well the coarse pass liked it.
        // OrderBy is stable, so within each group the coarse ranking survives — and with it
        // PreferNear's tie-break toward leaving things where they are.
        return [.. considered.OrderByDescending(c => c.Confirmed)];
    }

    /// <summary>One offset the coarse pass proposed, and what a closer look made of it.</summary>
    /// <param name="Confidence">
    /// The coarse coefficient — the number that means "the same sound", and the one reported.
    /// </param>
    /// <param name="Agreement">How well the fine pass corroborated it, 0 when it could not.</param>
    private readonly record struct Considered(
        double OffsetSeconds, double Confidence, double Agreement)
    {
        /// <summary>Whether this offset may be acted on by itself.</summary>
        public bool Confirmed => Agreement >= MinimumFineConfidence;

        /// <summary>Whether it is strong enough to apply without the user insisting.</summary>
        public bool Trusted => Confirmed && Confidence >= MinimumConfidence;
    }

    /// <summary>
    /// Re-correlates real audio around one coarse answer, at a finer resolution.
    /// </summary>
    /// <remarks>
    /// Returns null when the window cannot be decoded or when the peak lands against the edge
    /// of the search band — a match pinned to the edge has not been localised, it has run out
    /// of room, and treating it as a confirmation would launder a coarse answer that a closer
    /// look actually disagreed with.
    /// </remarks>
    private static SyncCandidate? Refine(
        AudioPeaks pattern,
        double patternIntoReference,
        double length,
        AudioDecoder candidateDecoder,
        double coarseOffset)
    {
        // The pattern may have been taken from part-way into the reference, so the offset
        // being checked is that much further into the candidate.
        var expected = coarseOffset + patternIntoReference;
        var searchStart = Math.Max(0, expected - FineWindowSeconds);

        var candidate = AudioPeaksBuilder.BuildRange(
            candidateDecoder, searchStart, length + (2 * FineWindowSeconds), FineRate);

        var refined = AudioSync.FindOffsets(
            new SyncRequest(pattern, 0, length, candidate), take: 1);

        if (refined.Count == 0) return null;

        var at = searchStart + refined[0].OffsetSeconds;
        var edge = 1.0 / FineRate;

        // The start of the file is not one of those edges: nothing is being cut off there, so
        // a peak against it is where the sound really is rather than a search that ran out.
        var pinnedLow = searchStart > 0 && at <= searchStart + edge;
        var pinnedHigh = at >= searchStart + (2 * FineWindowSeconds) - edge;

        if (pinnedLow || pinnedHigh) return null;

        return refined[0] with { OffsetSeconds = at - patternIntoReference };
    }

    /// <summary>Picks which stretch of the reference the fine pass should look at.</summary>
    /// <remarks>
    /// The fine pass only needs a few seconds of the reference, and which few matters. Taking
    /// the first of them — which is what this used to do — confirms a twenty-second match
    /// against whatever happens to sit at its front, and if that is room tone the confirmation
    /// is worthless; if it is flat outright there is no confirmation at all, because a pattern
    /// with no deviation correlates with nothing. The cached envelope is already in hand, so
    /// the stretch that varies most is nearly free to find, and variation is the only thing a
    /// correlation has to work with.
    /// </remarks>
    private static double InformativeStart(
        AudioPeaks peaks, double start, double length, double window)
    {
        if (window >= length) return start;

        var buckets = Math.Max(1, (int)Math.Round(window * peaks.Rate));
        var envelope = peaks.EnvelopeRange(start, length);
        if (envelope.Length <= buckets) return start;

        // A quarter of a window at a time: fine enough to find the interesting part, coarse
        // enough that this stays a few passes over a few seconds of envelope.
        var step = Math.Max(1, buckets / 4);

        var best = 0;
        var bestDeviation = -1.0;

        for (var offset = 0; offset + buckets <= envelope.Length; offset += step)
        {
            var deviation = Deviation(envelope.AsSpan(offset, buckets));
            if (deviation <= bestDeviation) continue;

            bestDeviation = deviation;
            best = offset;
        }

        return start + (best / (double)peaks.Rate);
    }

    private static double Deviation(ReadOnlySpan<float> values)
    {
        double sum = 0;
        double squares = 0;

        foreach (var value in values)
        {
            sum += value;
            squares += (double)value * value;
        }

        var mean = sum / values.Length;

        return Math.Sqrt(Math.Max(0, (squares / values.Length) - (mean * mean)));
    }


    private static BaseSegment SegmentAt(Project project, long frame)
    {
        foreach (var segment in project.Base)
            if (segment.Timeline.Contains(frame)) return segment;

        return project.Base[0];
    }
}
