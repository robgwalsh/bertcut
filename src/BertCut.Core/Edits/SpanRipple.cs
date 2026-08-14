using System.Collections.Immutable;
using BertCut.Core.Time;

namespace BertCut.Core.Edits;

/// <summary>
/// Applies a ripple delete to a collection of timeline-anchored spans.
/// </summary>
/// <remarks>
/// Both the crop list and the overlay list have to move identically when a range is cut
/// out of the base track, so the logic lives here once rather than being written twice and
/// drifting. This is the single most error-prone function in the codebase: it is the only
/// place where a span can be silently dropped, left behind at a stale position, or split
/// with the wrong source offset.
///
/// The cases, for a cut <c>[cs, ce)</c> of length <c>L</c>:
/// <list type="bullet">
///   <item>entirely before <c>cs</c> — untouched</item>
///   <item>entirely inside the cut — dropped</item>
///   <item>entirely after <c>ce</c> — shifted left by <c>L</c></item>
///   <item>overhangs the start only — truncated to end at <c>cs</c></item>
///   <item>overhangs the end only — moved to start at <c>cs</c>, keeping its tail length,
///         and its source offset advanced past the removed frames</item>
///   <item>straddles the whole cut — split into a head ending at <c>cs</c> and a tail
///         starting at <c>cs</c>, each keeping its own source offset</item>
/// </list>
/// The straddle case is why this cannot simply join the two halves: the tail resumes at a
/// later point in its source, so collapsing it into the head would slip the overlay's
/// content by the length of the cut.
/// </remarks>
public static class SpanRipple
{
    /// <summary>
    /// Rewrites <paramref name="spans"/> for a cut of <paramref name="cut"/> from the timeline.
    /// </summary>
    /// <param name="getRange">Reads a span's timeline range.</param>
    /// <param name="reanchor">
    /// Produces a span covering a new timeline range, given the number of frames that were
    /// consumed from the original span's start. Implementations advance any source-side
    /// offset by that amount; spans with no source offset (crops) ignore it.
    /// </param>
    public static ImmutableArray<T> Apply<T>(
        ImmutableArray<T> spans,
        FrameRange cut,
        Func<T, FrameRange> getRange,
        Func<T, FrameRange, long, T> reanchor)
    {
        if (cut.IsEmpty || spans.IsEmpty) return spans;

        var length = cut.Length;
        var result = ImmutableArray.CreateBuilder<T>(spans.Length);

        foreach (var span in spans)
        {
            var r = getRange(span);

            // Entirely before the cut: nothing moves.
            if (r.End <= cut.Start)
            {
                result.Add(span);
                continue;
            }

            // Entirely at or after the cut: slides left by the full cut length.
            if (r.Start >= cut.End)
            {
                result.Add(reanchor(span, r.Shift(-length), 0));
                continue;
            }

            // Overlapping. The head is whatever precedes the cut, the tail whatever follows.
            var headLength = Math.Max(0, cut.Start - r.Start);
            var tailLength = Math.Max(0, r.End - cut.End);

            if (headLength > 0)
                result.Add(reanchor(span, FrameRange.FromLength(r.Start, headLength), 0));

            if (tailLength > 0)
            {
                // The tail originally sat at [cut.End, r.End) and everything at or past
                // cut.End slides left by the cut length, so it lands at cut.Start —
                // immediately after the head, which ends there.
                //
                // Its source offset advances by every frame of the original span that now
                // precedes it: the head it kept, plus the portion the cut consumed.
                var consumed = headLength + (Math.Min(r.End, cut.End) - Math.Max(r.Start, cut.Start));
                result.Add(reanchor(span, FrameRange.FromLength(cut.Start, tailLength), consumed));
            }

            // Both zero means the span was wholly inside the cut, so it is dropped.
        }

        return result.ToImmutable();
    }
}
