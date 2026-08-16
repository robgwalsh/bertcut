using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Core.Edits;

/// <summary>
/// What a pending overlay is going to show: a run of one source's frames, and how long that
/// run lasts on the timeline.
/// </summary>
/// <remarks>
/// <para>
/// The three ways of choosing an overlay — a marked range, a base segment, a whole file —
/// differ only in how these numbers are worked out. Once they are, the kind stops mattering:
/// nothing downstream asks where a clip came from, only what it shows and for how long.
/// </para>
/// <para>
/// <see cref="LengthFrames"/> is in <b>output</b> frames while <see cref="SourceStartFrame"/>
/// is in the source's own. They are the same count only when the source runs at the project's
/// rate, which for the second camera this feature exists for is exactly when it does not.
/// </para>
/// </remarks>
public readonly record struct OverlayContent(int SourceId, long SourceStartFrame, long LengthFrames)
{
    public bool IsEmpty => LengthFrames <= 0;
}

/// <summary>
/// Works out what a chosen overlay shows, and where on the timeline it can go.
/// </summary>
/// <remarks>
/// In Core rather than in the view model so it can be tested without a window. Every question
/// here is arithmetic over an immutable <see cref="Project"/> — which segment a mark lands in,
/// how a source's frame count converts to timeline frames, what a clip bumps into — and none
/// of it needs a running editor to have an answer.
/// </remarks>
public static class OverlayPlacement
{
    /// <summary>The content under a marked timeline range.</summary>
    /// <remarks>
    /// Capped at the end of the segment the range starts in. An <see cref="OverlayClip"/> reads
    /// one contiguous run of one source, so a range spanning a cut cannot be honoured as asked;
    /// the choice is between the first part of it and refusing outright, and the first part is
    /// what the user can see they are getting. The caller is expected to say so.
    /// </remarks>
    public static OverlayContent? FromTimelineRange(Project project, FrameRange range)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (range.IsEmpty) return null;

        var segment = SegmentAt(project, range.Start);
        if (segment is not { } seg) return null;

        var source = project.RequireSource(seg.SourceId);
        var into = range.Start - seg.TimelineStart;
        var sourceStart = seg.SourceStartFrame + TimelineEdits.ToSourceFrames(project, source, into);

        // Two ceilings: the cut in front of it, and the frames the source has left.
        var length = Math.Min(range.Length, seg.Timeline.End - range.Start);
        length = Math.Min(
            length, TimelineEdits.ToOutputFrames(project, source, source.FrameCount - sourceStart));

        return length <= 0 ? null : new OverlayContent(seg.SourceId, sourceStart, length);
    }

    /// <summary>The content of one piece of the base track.</summary>
    /// <remarks>
    /// The simplest of the three: a <see cref="BaseSegment"/> already is a run of source frames
    /// with a length in output frames, so there is nothing to convert and nothing to clamp.
    /// </remarks>
    public static OverlayContent? FromSegment(Project project, int index)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (index < 0 || index >= project.Base.Length) return null;

        var seg = project.Base[index];
        return seg.LengthFrames <= 0
            ? null
            : new OverlayContent(seg.SourceId, seg.SourceStartFrame, seg.LengthFrames);
    }

    /// <summary>The whole of an imported file.</summary>
    /// <remarks>
    /// The one kind where the rate conversion is load-bearing: a 60 fps webcam take's frame
    /// count is not a count of timeline frames, and flooring is what stops the clip claiming
    /// a frame it does not have.
    /// </remarks>
    public static OverlayContent? FromWholeSource(Project project, int sourceId)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.FindSource(sourceId) is not { } source) return null;

        var length = TimelineEdits.ToOutputFrames(project, source, source.FrameCount);
        return length <= 0 ? null : new OverlayContent(sourceId, 0, length);
    }

    /// <summary>
    /// Where a clip of this content goes if the user is asking for it at
    /// <paramref name="playhead"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It stops against what is in the way rather than being cut down by it.</b> The length
    /// was settled when the content was chosen, and a clip that quietly came out shorter
    /// because the playhead happened to be near the end of the timeline would break the promise
    /// the choice just made. This is the same rule a dragged clip already follows — see
    /// <see cref="TimelineEdits.SetOverlayStart"/> — and it is the only feedback the strip can
    /// give that something is there.
    /// </para>
    /// <para>
    /// Truncation survives only for the case clamping cannot answer: a gap smaller than the
    /// content, where there is no position that fits. Everywhere else the returned range is
    /// exactly <see cref="OverlayContent.LengthFrames"/> long, which is what lets the ghost
    /// band promise what <c>Enter</c> will do — and it means this path never overlaps an
    /// existing clip, so <see cref="TimelineEdits.AddOverlay"/>'s truncation of its neighbours
    /// can never fire from here.
    /// </para>
    /// <para>
    /// Landing on top of an existing clip puts the new one <i>after</i> it. The playhead is
    /// inside that clip, so both neighbours are equally close; going forward is the direction
    /// the user was travelling, and it leaves the clip they were looking at intact.
    /// </para>
    /// </remarks>
    public static FrameRange RangeAt(Project project, OverlayContent content, long playhead)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (content.IsEmpty || project.DurationFrames <= 0) return FrameRange.Empty;

        // The gap the playhead is in: past anything it has landed on, and up to whatever
        // comes next.
        var gapStart = 0L;
        var gapEnd = project.DurationFrames;

        foreach (var clip in project.Overlays)
        {
            if (clip.Range.End <= playhead) gapStart = Math.Max(gapStart, clip.Range.End);
            else if (clip.Range.Contains(playhead)) gapStart = Math.Max(gapStart, clip.Range.End);
            else gapEnd = Math.Min(gapEnd, clip.Range.Start);
        }

        if (gapEnd <= gapStart) return FrameRange.Empty;

        var latest = gapEnd - content.LengthFrames;
        var start = Math.Clamp(playhead, gapStart, Math.Max(gapStart, latest));
        var end = Math.Min(start + content.LengthFrames, gapEnd);

        return new FrameRange(start, end);
    }

    private static BaseSegment? SegmentAt(Project project, long frame)
    {
        foreach (var segment in project.Base)
            if (segment.Timeline.Contains(frame)) return segment;

        return null;
    }
}
