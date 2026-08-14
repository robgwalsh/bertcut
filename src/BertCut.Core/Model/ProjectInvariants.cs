using BertCut.Core.Time;

namespace BertCut.Core.Model;

/// <summary>
/// The structural rules every <see cref="Project"/> must satisfy.
/// </summary>
/// <remarks>
/// Called after every edit in Debug builds and from one test per edit operation. Ripple
/// delete rewrites positions across the whole document, so a violated invariant is the
/// earliest and cheapest signal that an edit is wrong — far cheaper than noticing a
/// misplaced cut in an exported file.
/// </remarks>
public static class ProjectInvariants
{
    public static void Check(Project p)
    {
        var error = Validate(p);
        if (error is not null) throw new InvalidOperationException($"Project invariant violated: {error}");
    }

    /// <summary>Returns a description of the first violated invariant, or null when valid.</summary>
    public static string? Validate(Project p)
    {
        if (p.Output.Width <= 0 || p.Output.Height <= 0)
            return $"output size must be positive, was {p.Output.Width}x{p.Output.Height}";
        if (p.Output.FrameRate.Num <= 0 || p.Output.FrameRate.Den <= 0)
            return $"output frame rate must be positive, was {p.Output.FrameRate}";

        var sourceIds = new HashSet<int>();
        foreach (var s in p.Sources)
            if (!sourceIds.Add(s.Id)) return $"duplicate source id {s.Id}";

        // The base track is gapless and contiguous. Everything downstream — the prefix-sum
        // mapping, ripple delete, the render plan — assumes this holds.
        long expectedStart = 0;
        for (var i = 0; i < p.Base.Length; i++)
        {
            var seg = p.Base[i];
            if (seg.LengthFrames <= 0)
                return $"base[{i}] has non-positive length {seg.LengthFrames}";
            if (seg.TimelineStart != expectedStart)
                return $"base[{i}] starts at {seg.TimelineStart}, expected {expectedStart} (gap or overlap)";
            if (seg.SourceStartFrame < 0)
                return $"base[{i}] has negative source start {seg.SourceStartFrame}";
            if (!sourceIds.Contains(seg.SourceId))
                return $"base[{i}] references unknown source id {seg.SourceId}";

            var src = p.RequireSource(seg.SourceId);
            if (seg.SourceStartFrame + seg.LengthFrames > src.FrameCount)
                return $"base[{i}] reads source frames [{seg.SourceStartFrame}, " +
                       $"{seg.SourceStartFrame + seg.LengthFrames}) past the source's {src.FrameCount} frames";

            expectedStart += seg.LengthFrames;
        }

        var duration = p.DurationFrames;

        var cropError = ValidateSpans(
            p.Crops.Select(c => c.Range).ToArray(), duration, nameof(p.Crops));
        if (cropError is not null) return cropError;

        for (var i = 0; i < p.Crops.Length; i++)
        {
            var rect = p.Crops[i].Rect;
            if (rect.W <= 0 || rect.H <= 0)
                return $"crops[{i}] has non-positive size {rect}";
            if (rect.X < 0 || rect.Y < 0 || rect.Right > p.Output.Width || rect.Bottom > p.Output.Height)
                return $"crops[{i}] rect {rect} falls outside the {p.Output.Width}x{p.Output.Height} output";

            // Aspect lock: the crop must match the output ratio exactly, so zoom-to-fill
            // never needs a pad. Cross-multiplied to stay in integers.
            if ((long)rect.W * p.Output.Height != (long)rect.H * p.Output.Width)
                return $"crops[{i}] rect {rect} does not match the output aspect ratio " +
                       $"{p.Output.GcdWidth}:{p.Output.GcdHeight}";
        }

        var overlayError = ValidateSpans(
            p.Overlays.Select(o => o.Range).ToArray(), duration, nameof(p.Overlays));
        if (overlayError is not null) return overlayError;

        for (var i = 0; i < p.Overlays.Length; i++)
        {
            var clip = p.Overlays[i];
            if (!sourceIds.Contains(clip.SourceId))
                return $"overlays[{i}] references unknown source id {clip.SourceId}";
            if (clip.SourceStartFrame < 0)
                return $"overlays[{i}] has negative source start {clip.SourceStartFrame}";
            if (clip.Dest.W <= 0 || clip.Dest.H <= 0)
                return $"overlays[{i}] has non-positive destination size {clip.Dest}";
        }

        return null;
    }

    private static string? ValidateSpans(FrameRange[] ranges, long duration, string name)
    {
        for (var i = 0; i < ranges.Length; i++)
        {
            var r = ranges[i];
            if (r.IsEmpty) return $"{name}[{i}] is empty {r}";
            if (r.Start < 0 || r.End > duration)
                return $"{name}[{i}] {r} falls outside the timeline [0, {duration})";
            if (i > 0 && r.Start < ranges[i - 1].End)
                return $"{name}[{i}] {r} overlaps or precedes {name}[{i - 1}] {ranges[i - 1]}";
        }

        return null;
    }
}
