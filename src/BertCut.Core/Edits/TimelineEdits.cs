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

    /// <summary>Ripples one base segment out of the timeline.</summary>
    /// <remarks>
    /// A segment's range already lies on segment boundaries, so this is a ripple delete that
    /// happens to split nothing — which is the point of expressing it as one. Deleting a
    /// segment and marking its exact range and pressing X are then the same operation, and
    /// there is no second implementation of "close the gap" to keep in step with the first.
    /// </remarks>
    public static Project RemoveSegment(Project p, int index) =>
        index < 0 || index >= p.Base.Length ? p : RippleDelete(p, p.Base[index].Timeline);

    /// <summary>
    /// Moves one base segment to another position in the running order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The crops and overlays ride along. They are addressed by timeline range rather than
    /// attached to a segment, so left alone they would stay where they are on the clock while
    /// the picture moved out from under them — a crop framing a face would end up zooming
    /// into whatever took that stretch of time instead. What the user placed it on is what it
    /// belongs to.
    /// </para>
    /// <para>
    /// A span covering two segments that are being separated is cut in two, each half
    /// travelling with the segment beneath it. For an overlay that means its source in-point
    /// moves with the second half, exactly as it does when a ripple delete cuts through one.
    /// Adjacent crop halves that end up touching again are merged back by
    /// <see cref="Coalesce"/>.
    /// </para>
    /// </remarks>
    public static Project MoveSegment(Project p, int index, int destination)
    {
        if (index < 0 || index >= p.Base.Length) return p;

        destination = Math.Clamp(destination, 0, p.Base.Length - 1);
        if (index == destination) return p;

        var order = p.Base.RemoveAt(index).Insert(destination, p.Base[index]);

        // Re-run the prefix sum, keeping each block's old range beside its new start — that
        // pairing is the whole map from the old timeline to the new one.
        var segments = ImmutableArray.CreateBuilder<BaseSegment>(order.Length);
        var blocks = new List<(FrameRange From, long To)>(order.Length);
        long start = 0;

        foreach (var segment in order)
        {
            blocks.Add((segment.Timeline, start));
            segments.Add(segment with { TimelineStart = start });
            start += segment.LengthFrames;
        }

        var crops = Remap(p.Crops, blocks, static c => c.Range, static (c, range, _) => c with { Range = range });

        var overlays = Remap(
            p.Overlays, blocks,
            static o => o.Range,
            (o, range, consumed) => o with
            {
                Range = range,
                SourceStartFrame = o.SourceStartFrame + ToSourceFrames(p, p.RequireSource(o.SourceId), consumed),
            });

        return p with
        {
            Base = segments.ToImmutable(),
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
                return RemoveOverlay(p, i);
        return p;
    }

    /// <summary>Removes one overlay by position in the list.</summary>
    /// <remarks>
    /// What the delete key uses once a clip has been picked out on the strip: the selection
    /// is an index, and asking for "the overlay at frame n" to find it again would go looking
    /// for a clip the user has already pointed at.
    /// </remarks>
    public static Project RemoveOverlay(Project p, int index) =>
        p with { Overlays = p.Overlays.RemoveAt(index) };

    /// <summary>Repositions or resizes an overlay.</summary>
    public static Project MoveOverlay(Project p, int index, RectI dest) =>
        p with { Overlays = p.Overlays.SetItem(index, p.Overlays[index] with { Dest = dest }) };

    /// <summary>
    /// Slides an overlay along the timeline, keeping its length and what it shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clamped into the gap between its neighbours and the ends of the timeline rather than
    /// allowed to overwrite anything. <see cref="AddOverlay"/> truncates whatever it lands on,
    /// which is right for a placement committed once; it is wrong for a drag, where every
    /// mouse-move is an edit and a clip pushed across its neighbour would eat it a few frames
    /// at a time. A clip that will not go where the pointer is stops against what is in the
    /// way, which is also the only feedback the strip can give that something is there.
    /// </para>
    /// <para>
    /// <see cref="OverlayClip.SourceStartFrame"/> deliberately does not follow. Moving a clip
    /// changes when it plays, not what it shows; sliding its content against the base track is
    /// what <see cref="SetOverlaySourceStart"/> is for.
    /// </para>
    /// </remarks>
    public static Project SetOverlayStart(Project p, int index, long timelineStart)
    {
        var clip = p.Overlays[index];
        var length = clip.Range.Length;

        var earliest = index > 0 ? p.Overlays[index - 1].Range.End : 0;
        var latest = (index < p.Overlays.Length - 1
            ? p.Overlays[index + 1].Range.Start
            : p.DurationFrames) - length;

        // Nowhere to go: the clip already fills the gap it is in, to the frame.
        if (latest < earliest) return p;

        var start = Math.Clamp(timelineStart, earliest, latest);
        if (start == clip.Range.Start) return p;

        return p with
        {
            Overlays = p.Overlays.SetItem(index, clip with { Range = FrameRange.FromLength(start, length) }),
        };
    }

    /// <summary>The shortest an overlay can be trimmed to.</summary>
    /// <remarks>
    /// One frame rather than nothing: a zero-length clip is not a clip, and an edge dragged
    /// past its opposite number should stop rather than delete something the user was still
    /// holding on to. The delete key is how a clip goes away.
    /// </remarks>
    public const long MinimumOverlayFrames = 1;

    /// <summary>
    /// Moves an overlay's in-point on the timeline, keeping what it shows where it is.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="SetOverlayStart"/>: that one slides the whole clip, this
    /// one pulls its front edge, so the frames the clip still covers must go on showing
    /// exactly what they showed before — which means the source in-point travels with the
    /// edge. Bounded by the neighbour behind it and by how much of the source sits in front
    /// of the in-point, because a clip cannot start before its own footage does.
    /// </remarks>
    public static Project TrimOverlayStart(Project p, int index, long timelineStart)
    {
        var clip = p.Overlays[index];
        var source = p.RequireSource(clip.SourceId);

        var earliest = Math.Max(
            index > 0 ? p.Overlays[index - 1].Range.End : 0,
            clip.Range.Start - ToOutputFrames(p, source, clip.SourceStartFrame));

        var latest = clip.Range.End - MinimumOverlayFrames;
        if (latest < earliest) return p;

        var start = Math.Clamp(timelineStart, earliest, latest);
        if (start == clip.Range.Start) return p;

        var sourceStart = clip.SourceStartFrame + ToSourceFrames(p, source, start - clip.Range.Start);

        return p with
        {
            Overlays = p.Overlays.SetItem(index, clip with
            {
                Range = new FrameRange(start, clip.Range.End),
                SourceStartFrame = Math.Max(0, sourceStart),
            }),
        };
    }

    /// <summary>
    /// Moves an overlay's out-point on the timeline.
    /// </summary>
    /// <remarks>
    /// Nothing about the content moves — the clip still starts where it started and still
    /// reads from the same place — so this is the simpler edge. It stops against the
    /// neighbour ahead of it and against the end of its own source, which is the frame after
    /// which extending it would show a still.
    /// </remarks>
    public static Project TrimOverlayEnd(Project p, int index, long timelineEnd)
    {
        var clip = p.Overlays[index];
        var source = p.RequireSource(clip.SourceId);

        var earliest = clip.Range.Start + MinimumOverlayFrames;

        var room = Math.Min(
            index < p.Overlays.Length - 1 ? p.Overlays[index + 1].Range.Start : p.DurationFrames,
            clip.Range.Start + ToOutputFrames(p, source, Math.Max(0, source.FrameCount - clip.SourceStartFrame)));

        // Never shorter than it already is: a clip that somehow outruns its source can still
        // be pulled in, which is the only way out of that state.
        var latest = Math.Max(clip.Range.End, room);
        if (latest < earliest) return p;

        var end = Math.Clamp(timelineEnd, earliest, latest);
        if (end == clip.Range.End) return p;

        return p with
        {
            Overlays = p.Overlays.SetItem(index, clip with { Range = new FrameRange(clip.Range.Start, end) }),
        };
    }

    /// <summary>
    /// Converts a count of a source's own frames into output frames, and back.
    /// </summary>
    /// <remarks>
    /// An overlay's source is frequently not the base video — the case the feature exists for
    /// is a second camera — so it is frequently not at the project's rate either. The resolver
    /// already rescales when it reads a clip; these two exist so the limits on trimming one
    /// agree with what it will actually be able to show. Both floor, so neither can claim a
    /// frame that is not there.
    /// </remarks>
    private static long ToOutputFrames(Project p, SourceMedia source, long frames) =>
        source.FrameRate.EquivalentTo(p.Output.FrameRate)
            ? frames
            : RationalMath.RescaleFloor(frames, source.FrameRate.Inverse, p.Output.FrameRate.Inverse);

    private static long ToSourceFrames(Project p, SourceMedia source, long frames) =>
        source.FrameRate.EquivalentTo(p.Output.FrameRate)
            ? frames
            : RationalMath.RescaleFloor(frames, p.Output.FrameRate.Inverse, source.FrameRate.Inverse);

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

    /// <summary>
    /// Rewrites a span list through a rearrangement of the base track.
    /// </summary>
    /// <param name="blocks">Each moved block's old range, and where it now starts.</param>
    /// <param name="withRange">
    /// Rebuilds a span at its new range. The third argument is how far into the original span
    /// the piece begins, which is what an overlay's source in-point has to advance by.
    /// </param>
    /// <remarks>
    /// The blocks partition the timeline and only change order, so this is a permutation of
    /// frames: every span comes out with the same total length it went in with, and the
    /// result cannot overlap. Sorted on the way out because a span cut in two lands in two
    /// places that need not be in the order the pieces were produced.
    /// </remarks>
    private static ImmutableArray<T> Remap<T>(
        ImmutableArray<T> spans,
        List<(FrameRange From, long To)> blocks,
        Func<T, FrameRange> getRange,
        Func<T, FrameRange, long, T> withRange)
    {
        if (spans.IsEmpty) return spans;

        var result = ImmutableArray.CreateBuilder<T>(spans.Length);

        foreach (var span in spans)
        {
            var range = getRange(span);

            foreach (var (from, to) in blocks)
            {
                if (range.Intersect(from) is not { } piece) continue;

                var shift = to - from.Start;
                result.Add(withRange(span, piece.Shift(shift), piece.Start - range.Start));
            }
        }

        result.Sort((a, b) => getRange(a).Start.CompareTo(getRange(b).Start));
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
