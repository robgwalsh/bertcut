using System.Collections.Immutable;
using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Core.Timeline;

/// <summary>
/// A stretch of timeline over which source, crop, and overlay are all constant.
/// </summary>
public readonly record struct FlatSegment(
    FrameRange Timeline,
    int SourceId,
    long SourceStartFrame,
    RectI? Crop,
    OverlayClip? Overlay,
    long OverlaySourceStartFrame);

/// <summary>
/// Flattens a <see cref="Project"/> into spans the export pipeline can turn into one
/// ffmpeg invocation each.
/// </summary>
/// <remarks>
/// This is the other half of the preview/export contract: <see cref="TimelineResolver"/>
/// answers per frame for the compositor, this answers per span for the argument builder,
/// and a property test asserts they agree for every frame of a generated project. Two
/// independent implementations of crop and overlay geometry would eventually disagree at
/// some rounding boundary; deriving both from one place is what prevents that.
///
/// Because each segment carries exactly one crop and at most one overlay, an exported
/// segment needs only a single <c>crop</c>/<c>scale</c>/<c>overlay</c> chain with no
/// per-frame expressions in the geometry.
/// </remarks>
public static class RenderPlan
{
    public static ImmutableArray<FlatSegment> Build(Project p)
    {
        if (p.Base.IsEmpty) return ImmutableArray<FlatSegment>.Empty;

        var cuts = CollectBoundaries(p);
        var result = ImmutableArray.CreateBuilder<FlatSegment>(cuts.Count);
        var resolver = new TimelineResolver(p);

        for (var i = 0; i < cuts.Count - 1; i++)
        {
            var range = new FrameRange(cuts[i], cuts[i + 1]);
            if (range.IsEmpty) continue;

            // Every frame in the span resolves identically by construction, so the first
            // frame describes the whole span.
            var head = resolver.Resolve(range.Start);
            if (head is null) continue;

            result.Add(new FlatSegment(
                Timeline: range,
                SourceId: head.Value.SourceId,
                SourceStartFrame: head.Value.SourceFrame,
                Crop: head.Value.Crop,
                Overlay: head.Value.Overlay,
                OverlaySourceStartFrame: head.Value.OverlaySourceFrame));
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// Every frame at which the output's description can change: base segment edges and
    /// both ends of each crop and overlay span.
    /// </summary>
    private static List<long> CollectBoundaries(Project p)
    {
        var set = new SortedSet<long> { 0, p.DurationFrames };

        foreach (var seg in p.Base) set.Add(seg.TimelineStart);
        foreach (var crop in p.Crops)
        {
            set.Add(crop.Range.Start);
            set.Add(crop.Range.End);
        }

        foreach (var overlay in p.Overlays)
        {
            set.Add(overlay.Range.Start);
            set.Add(overlay.Range.End);
        }

        var duration = p.DurationFrames;
        return set.Where(f => f >= 0 && f <= duration).ToList();
    }
}
