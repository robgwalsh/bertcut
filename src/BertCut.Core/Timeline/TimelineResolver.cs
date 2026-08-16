using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Core.Timeline;

/// <summary>What the output shows at one timeline frame.</summary>
public readonly record struct FrameResolution(
    int SourceId,
    long SourceFrame,
    RectI? Crop,
    OverlayClip? Overlay,
    long OverlaySourceFrame);

/// <summary>
/// Answers "what does timeline frame <c>t</c> show?" for the preview compositor.
/// </summary>
/// <remarks>
/// This runs once per displayed frame and again on every scrub tick, so it carries a hint
/// cache: the last matched index, then the one after it, are checked before falling back
/// to a binary search. During playback and slow scrubbing that hits essentially always,
/// turning the hot path into two comparisons.
///
/// The hint makes instances stateful, so <b>each thread constructs its own</b> — the
/// render, audio, and UI threads never share one. The <see cref="Project"/> itself is
/// immutable and safe to share; only the hint is not.
/// </remarks>
public sealed class TimelineResolver(Project project)
{
    private int _baseHint;
    private int _cropHint;
    private int _overlayHint;

    public Project Project { get; } = project;

    public long DurationFrames => Project.DurationFrames;

    /// <summary>
    /// Resolves <paramref name="frame"/>, or null when it falls outside the timeline.
    /// </summary>
    public FrameResolution? Resolve(long frame)
    {
        if (frame < 0 || frame >= Project.DurationFrames) return null;

        var segIndex = FindBase(frame);
        if (segIndex < 0) return null;

        var seg = Project.Base[segIndex];
        var offset = frame - seg.TimelineStart;
        var source = Project.RequireSource(seg.SourceId);

        var sourceFrame = seg.SourceStartFrame + ToSourceFrames(offset, source, Project.Output);

        RectI? crop = null;
        var cropIndex = FindSpan(Project.Crops.AsSpan(), frame, ref _cropHint, static c => c.Range);
        if (cropIndex >= 0) crop = Project.Crops[cropIndex].Rect;

        OverlayClip? overlay = null;
        long overlaySourceFrame = 0;
        var overlayIndex = FindSpan(Project.Overlays.AsSpan(), frame, ref _overlayHint, static o => o.Range);
        if (overlayIndex >= 0)
        {
            var clip = Project.Overlays[overlayIndex];
            var clipOffset = frame - clip.Range.Start;
            var overlaySource = Project.RequireSource(clip.SourceId);

            overlay = clip;
            overlaySourceFrame = clip.SourceStartFrame + ToSourceFrames(clipOffset, overlaySource, Project.Output);
        }

        return new FrameResolution(seg.SourceId, sourceFrame, crop, overlay, overlaySourceFrame);
    }

    /// <summary>
    /// Where a source frame shows on the timeline, or null when it shows nowhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inverse of <see cref="Resolve"/>, and the only question the audio sync asks in this
    /// direction: the correlation answers "this moment of the base recording", and what the
    /// caller needs is the timeline frame sitting on it.
    /// </para>
    /// <para>
    /// <b>Null is an answer, not a failure.</b> The base track is what survived the cutting, so
    /// a source frame the user rippled away is genuinely not on the timeline — and a sync that
    /// snapped to the nearest surviving frame instead would be confidently wrong at a position
    /// nothing was ever matched against.
    /// </para>
    /// <para>
    /// A moment can also show <i>more than once</i> — a run duplicated on the track, or one
    /// simply reordered — so the caller says where it believes the answer is and gets the
    /// nearest of them. That is the same tie-break <c>AudioSync.PreferNear</c> applies one
    /// level down, for the same reason: of two answers that are equally true, the one that
    /// moves things least is the one the user meant.
    /// </para>
    /// <para>
    /// A linear scan, and deliberately not hint-cached like <see cref="FindBase"/>: this runs
    /// once when a key is pressed, never once per displayed frame.
    /// </para>
    /// </remarks>
    /// <param name="near">The timeline frame to break ties toward.</param>
    public long? TimelineFrameOf(int sourceId, long sourceFrame, long near = 0)
    {
        long? best = null;

        foreach (var seg in Project.Base)
        {
            if (seg.SourceId != sourceId) continue;

            var source = Project.RequireSource(seg.SourceId);
            var into = sourceFrame - seg.SourceStartFrame;
            if (into < 0) continue;

            // How much of the source this segment actually covers, in the source's own frames
            // — a 60 fps clip on a 30 fps timeline spends two of its frames per output frame.
            if (into >= ToSourceFrames(seg.LengthFrames, source, Project.Output)) continue;

            var at = seg.TimelineStart + ToOutputFrames(into, source, Project.Output);

            if (best is null || Math.Abs(at - near) < Math.Abs(best.Value - near)) best = at;
        }

        return best;
    }

    /// <summary>
    /// Converts an offset in output frames to an offset in source frames, and back.
    /// </summary>
    /// <remarks>
    /// The common case by far is that every source came off the same recorder at the
    /// project's own rate, so the fast path is an equality check and no arithmetic at all.
    /// Both floor, so neither can claim a frame that is not there.
    /// </remarks>
    private static long ToSourceFrames(long offset, SourceMedia source, OutputFormat output) =>
        source.FrameRate.EquivalentTo(output.FrameRate)
            ? offset
            : RationalMath.RescaleFloor(offset, output.FrameRate.Inverse, source.FrameRate.Inverse);

    private static long ToOutputFrames(long offset, SourceMedia source, OutputFormat output) =>
        source.FrameRate.EquivalentTo(output.FrameRate)
            ? offset
            : RationalMath.RescaleFloor(offset, source.FrameRate.Inverse, output.FrameRate.Inverse);

    private int FindBase(long frame)
    {
        var segments = Project.Base;
        if (segments.IsEmpty) return -1;

        // Hint: the frame we resolved last, then its successor — playback advances one of
        // these two almost every time.
        if (_baseHint < segments.Length && segments[_baseHint].Timeline.Contains(frame))
            return _baseHint;

        var next = _baseHint + 1;
        if (next < segments.Length && segments[next].Timeline.Contains(frame))
            return _baseHint = next;

        var lo = 0;
        var hi = segments.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var seg = segments[mid];
            if (frame < seg.TimelineStart) hi = mid - 1;
            else if (frame >= seg.TimelineStart + seg.LengthFrames) lo = mid + 1;
            else return _baseHint = mid;
        }

        return -1;
    }

    private static int FindSpan<T>(ReadOnlySpan<T> spans, long frame, ref int hint, Func<T, FrameRange> getRange)
    {
        if (spans.IsEmpty) return -1;

        if (hint < spans.Length && getRange(spans[hint]).Contains(frame)) return hint;

        var next = hint + 1;
        if (next < spans.Length && getRange(spans[next]).Contains(frame)) return hint = next;

        var lo = 0;
        var hi = spans.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var r = getRange(spans[mid]);
            if (frame < r.Start) hi = mid - 1;
            else if (frame >= r.End) lo = mid + 1;
            else return hint = mid;
        }

        return -1;
    }
}
