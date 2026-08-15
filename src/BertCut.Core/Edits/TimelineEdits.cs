using System.Collections.Immutable;
using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Core.Edits;

/// <summary>
/// Every mutation of a <see cref="Project"/>, as pure functions.
/// </summary>
/// <remarks>
/// Nothing here mutates its input, so undo is a snapshot stack rather than a set of
/// hand-written inverse operations. Ripple delete in particular has no cheap inverse —
/// undoing it means restoring split segments, dropped crops, split overlays, and every
/// downstream position — and a command pattern would mean writing that backwards and
/// exercising it only when a user presses Ctrl+Z.
/// </remarks>
public static class TimelineEdits
{
    /// <summary>Adds a source to the project, assigning it the next free id.</summary>
    public static Project ImportSource(Project p, SourceMedia source, bool appendToBase = true)
    {
        var id = p.Sources.IsEmpty ? 1 : p.Sources.Max(s => s.Id) + 1;
        var withId = source with { Id = id };
        var next = p with { Sources = p.Sources.Add(withId) };

        if (!appendToBase || withId.FrameCount <= 0) return next;

        // A source's own frames are counted at its own rate; the base track counts output
        // frames. Floor so an imported clip never claims a frame it does not have.
        var lengthInOutput = withId.FrameRate.EquivalentTo(next.Output.FrameRate)
            ? withId.FrameCount
            : RationalMath.RescaleFloor(
                withId.FrameCount, withId.FrameRate.Inverse, next.Output.FrameRate.Inverse);

        if (lengthInOutput <= 0) return next;

        var segment = new BaseSegment(next.DurationFrames, lengthInOutput, id, 0);
        return next with { Base = next.Base.Add(segment) };
    }

    /// <summary>
    /// Removes <paramref name="cut"/> from the timeline; everything after it slides left.
    /// </summary>
    public static Project RippleDelete(Project p, FrameRange cut)
    {
        cut = cut.ClampTo(p.DurationFrames);
        if (cut.IsEmpty) return p;

        var baseTrack = RippleBase(p.Base, cut);

        var crops = SpanRipple.Apply(
            p.Crops, cut,
            static c => c.Range,
            static (c, range, _) => c with { Range = range });

        var overlays = SpanRipple.Apply(
            p.Overlays, cut,
            static o => o.Range,
            static (o, range, consumed) => o with
            {
                Range = range,
                SourceStartFrame = o.SourceStartFrame + consumed,
            });

        return p with
        {
            Base = baseTrack,
            Crops = Coalesce(crops),
            Overlays = overlays,
        };
    }

    /// <summary>Splits the base segment containing <paramref name="frame"/> at that frame.</summary>
    /// <remarks>
    /// Splitting is the primitive that crop reuses: applying a crop to a range is a split
    /// at each end followed by setting the rect on the segments between. One operation
    /// serves two features, so there is no separate crop-range entity to keep consistent
    /// with the base track.
    /// </remarks>
    public static Project SplitAt(Project p, long frame)
    {
        var split = SplitBase(p.Base, frame);
        return split.HasValue ? p with { Base = split.Value } : p;
    }

    /// <summary>Applies a crop rect over <paramref name="range"/>, replacing any crop there.</summary>
    public static Project SetCrop(Project p, FrameRange range, RectI rect)
    {
        range = range.ClampTo(p.DurationFrames);
        if (range.IsEmpty) return p;

        var crops = RemoveSpanRange(p.Crops, range, static c => c.Range, static (c, r) => c with { Range = r });
        crops = Insert(crops, new CropSpan(range, rect), static c => c.Range);

        // The base track is split at the crop edges so every FlatSegment has one constant
        // crop, which is what lets each export segment carry a single crop filter.
        var baseTrack = SplitBase(p.Base, range.Start) ?? p.Base;
        baseTrack = SplitBase(baseTrack, range.End) ?? baseTrack;

        return p with { Base = baseTrack, Crops = Coalesce(crops) };
    }

    /// <summary>Removes any crop covering <paramref name="range"/>.</summary>
    public static Project ClearCrop(Project p, FrameRange range)
    {
        range = range.ClampTo(p.DurationFrames);
        if (range.IsEmpty) return p;

        var crops = RemoveSpanRange(p.Crops, range, static c => c.Range, static (c, r) => c with { Range = r });
        return p with { Crops = Coalesce(crops) };
    }

    /// <summary>Places a picture-in-picture clip, replacing any overlay in the same range.</summary>
    public static Project AddOverlay(Project p, OverlayClip clip)
    {
        var range = clip.Range.ClampTo(p.DurationFrames);
        if (range.IsEmpty) return p;

        clip = clip with { Range = range };

        var overlays = RemoveSpanRange(
            p.Overlays, range,
            static o => o.Range,
            static (o, r) => o with
            {
                // A truncated overlay keeps its content aligned: trimming frames from the
                // front must advance its source offset by the same amount.
                SourceStartFrame = o.SourceStartFrame + (r.Start - o.Range.Start),
                Range = r,
            });

        overlays = Insert(overlays, clip, static o => o.Range);
        return p with { Overlays = overlays };
    }

    /// <summary>Removes the overlay containing <paramref name="frame"/>, if any.</summary>
    public static Project RemoveOverlayAt(Project p, long frame)
    {
        for (var i = 0; i < p.Overlays.Length; i++)
            if (p.Overlays[i].Range.Contains(frame))
                return p with { Overlays = p.Overlays.RemoveAt(i) };
        return p;
    }

    /// <summary>Repositions or resizes an overlay.</summary>
    public static Project MoveOverlay(Project p, int index, RectI dest) =>
        p with { Overlays = p.Overlays.SetItem(index, p.Overlays[index] with { Dest = dest }) };

    /// <summary>Toggles an overlay clip's audio.</summary>
    /// <remarks>
    /// Retained because sessions written by earlier builds carry the flag, but nothing reads
    /// it: the export mixes the base track's audio and only that, deliberately — two angles
    /// of one event carry near-identical sound, and summing them combs rather than enriches.
    /// The <c>M</c> key now mutes preview monitoring instead, which does not belong in the
    /// document at all.
    /// </remarks>
    public static Project ToggleOverlayMute(Project p, int index) =>
        p with { Overlays = p.Overlays.SetItem(index, p.Overlays[index] with { Muted = !p.Overlays[index].Muted }) };

    /// <summary>
    /// Slides where an overlay reads from in its own source, without moving it on the timeline.
    /// </summary>
    /// <remarks>
    /// The committed form of what Alt+←/→ does one frame at a time while placing, and what
    /// the audio sync writes when it finds the alignment in one go. Clamped so the overlay
    /// cannot be pushed past the end of its source and start showing nothing.
    /// </remarks>
    public static Project SetOverlaySourceStart(Project p, int index, long sourceStartFrame)
    {
        var clip = p.Overlays[index];
        var source = p.RequireSource(clip.SourceId);
        var limit = Math.Max(0, source.FrameCount - clip.Range.Length);

        var clamped = Math.Clamp(sourceStartFrame, 0, limit);
        if (clamped == clip.SourceStartFrame) return p;

        return p with { Overlays = p.Overlays.SetItem(index, clip with { SourceStartFrame = clamped }) };
    }

    // ---- base track helpers -------------------------------------------------------

    private static ImmutableArray<BaseSegment> RippleBase(ImmutableArray<BaseSegment> segments, FrameRange cut)
    {
        var split = SplitBase(segments, cut.Start) ?? segments;
        split = SplitBase(split, cut.End) ?? split;

        var result = ImmutableArray.CreateBuilder<BaseSegment>(split.Length);
        long start = 0;

        foreach (var seg in split)
        {
            // After the two splits above, every segment lies wholly inside or wholly
            // outside the cut, so this is a straight filter with a re-run prefix sum.
            if (seg.TimelineStart >= cut.Start && seg.TimelineStart + seg.LengthFrames <= cut.End)
                continue;

            result.Add(seg with { TimelineStart = start });
            start += seg.LengthFrames;
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// Splits the segment containing <paramref name="frame"/> in two. Returns null when
    /// the frame already falls on a boundary or outside the track, so callers can skip a
    /// pointless rebuild.
    /// </summary>
    private static ImmutableArray<BaseSegment>? SplitBase(ImmutableArray<BaseSegment> segments, long frame)
    {
        var index = IndexOfSegment(segments, frame);
        if (index < 0) return null;

        var seg = segments[index];
        var offset = frame - seg.TimelineStart;
        if (offset <= 0) return null;   // already a boundary

        var head = seg with { LengthFrames = offset };
        var tail = new BaseSegment(
            TimelineStart: frame,
            LengthFrames: seg.LengthFrames - offset,
            SourceId: seg.SourceId,
            SourceStartFrame: seg.SourceStartFrame + offset);

        return segments.SetItem(index, head).Insert(index + 1, tail);
    }

    private static int IndexOfSegment(ImmutableArray<BaseSegment> segments, long frame)
    {
        var lo = 0;
        var hi = segments.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var seg = segments[mid];
            if (frame < seg.TimelineStart) hi = mid - 1;
            else if (frame >= seg.TimelineStart + seg.LengthFrames) lo = mid + 1;
            else return mid;
        }

        return -1;
    }

    // ---- span list helpers --------------------------------------------------------

    /// <summary>Carves <paramref name="range"/> out of a sorted, non-overlapping span list.</summary>
    private static ImmutableArray<T> RemoveSpanRange<T>(
        ImmutableArray<T> spans,
        FrameRange range,
        Func<T, FrameRange> getRange,
        Func<T, FrameRange, T> withRange)
    {
        var result = ImmutableArray.CreateBuilder<T>(spans.Length + 1);

        foreach (var span in spans)
        {
            var r = getRange(span);
            if (!r.Overlaps(range))
            {
                result.Add(span);
                continue;
            }

            if (r.Start < range.Start) result.Add(withRange(span, new FrameRange(r.Start, range.Start)));
            if (r.End > range.End) result.Add(withRange(span, new FrameRange(range.End, r.End)));
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<T> Insert<T>(ImmutableArray<T> spans, T span, Func<T, FrameRange> getRange)
    {
        var start = getRange(span).Start;
        var i = 0;
        while (i < spans.Length && getRange(spans[i]).Start < start) i++;
        return spans.Insert(i, span);
    }

    /// <summary>
    /// Merges adjacent crop spans carrying the same rect.
    /// </summary>
    /// <remarks>
    /// A ripple delete through the middle of a crop leaves its head and tail touching with
    /// an identical rect. Left alone, that redundant boundary would split the render plan
    /// and cost an extra encode segment on export for no visible difference.
    /// </remarks>
    private static ImmutableArray<CropSpan> Coalesce(ImmutableArray<CropSpan> crops)
    {
        if (crops.Length < 2) return crops;

        var result = ImmutableArray.CreateBuilder<CropSpan>(crops.Length);
        var current = crops[0];

        for (var i = 1; i < crops.Length; i++)
        {
            var next = crops[i];
            if (next.Range.Start == current.Range.End && next.Rect == current.Rect)
                current = current with { Range = new FrameRange(current.Range.Start, next.Range.End) };
            else
            {
                result.Add(current);
                current = next;
            }
        }

        result.Add(current);
        return result.ToImmutable();
    }
}
