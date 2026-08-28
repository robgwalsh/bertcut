using System.Collections.Immutable;
using BertCut.Core.Time;

namespace BertCut.Core.Model;

/// <summary>Fixed dimensions and rate of everything this project renders.</summary>
public sealed record OutputFormat(int Width, int Height, Rational FrameRate, int SampleRate = 48000)
{
    public int GcdWidth => Gcd(Width, Height) is var g && g > 0 ? Width / g : Width;
    public int GcdHeight => Gcd(Width, Height) is var g && g > 0 ? Height / g : Height;

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}

/// <summary>
/// An imported file. Small and serialized into the document; the heavy per-frame
/// timestamp table lives out of document in <c>SourceIndex</c>, keyed by ContentKey.
/// </summary>
public sealed record SourceMedia(
    int Id,
    string Path,
    string ContentKey,
    long FrameCount,
    int Width,
    int Height,
    Rational FrameRate,
    bool IsVariableFrameRate,
    bool HasAudio,
    int AudioSampleRate,
    string VideoCodec,
    string PixelFormat);

/// <summary>An axis-aligned integer rectangle in output-space pixels.</summary>
public readonly record struct RectI(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public override string ToString() => $"{W}x{H}+{X}+{Y}";
}

/// <summary>
/// One piece of the base track: a contiguous run of source frames occupying a contiguous
/// run of timeline frames.
/// </summary>
/// <remarks>
/// <see cref="TimelineStart"/> is a denormalized prefix sum of the preceding lengths. It
/// is derived, not authoritative — <c>ProjectInvariants</c> enforces that it agrees with
/// the running total, so a segment can never drift out of place.
/// </remarks>
public readonly record struct BaseSegment(
    long TimelineStart,
    long LengthFrames,
    int SourceId,
    long SourceStartFrame)
{
    public FrameRange Timeline => FrameRange.FromLength(TimelineStart, LengthFrames);
}

/// <summary>
/// A crop applied over a timeline range, zoomed to fill the delivered picture.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Rect"/> is in output-space pixels — the frame as it arrives, before anything
/// has been cropped away — so the user drags on exactly what they see. Its shape is free:
/// a crop may be any ratio, and its two dimensions need only be even, which is what
/// ffmpeg's crop filter requires of yuv420p.
/// </para>
/// <para>
/// A free ratio has to land somewhere, and it lands on <see cref="Project.DeliveredSize"/>:
/// a crop covering the whole timeline hands its own size to the delivered file, so the
/// picture is neither padded nor stretched. A crop over part of the timeline cannot — one
/// stretch of an edit does not get a resolution of its own — so it zooms to fill as it
/// always did, and distorts if the user drew a shape the output is not.
/// </para>
/// </remarks>
public readonly record struct CropSpan(FrameRange Range, RectI Rect);

/// <summary>A picture-in-picture clip on the overlay track.</summary>
/// <remarks>
/// <para>
/// <see cref="Dest"/> is in <b>delivered</b> pixels — where the clip lands on the finished
/// frame — while <see cref="Src"/> is in the overlay source's <b>own</b> pixels. The two
/// spaces are one and the same until a whole-timeline crop changes the delivered size, and
/// keeping <see cref="Src"/> in the footage's own grid is what lets it survive that: it
/// names a region of a recording, which no later edit moves.
/// </para>
/// <para>
/// <see cref="Src"/> is null for the ordinary clip, which shows its source whole. It is set
/// when a region has been cut out of the picture and laid back over it — a corner of the
/// base blown up in place — which is the one case where a clip shares the base's own source.
/// </para>
/// </remarks>
public readonly record struct OverlayClip(
    FrameRange Range,
    int SourceId,
    long SourceStartFrame,
    RectI Dest,
    RectI? Src = null,
    bool Muted = true);

/// <summary>
/// The complete, immutable description of an edit. This is the single authority that both
/// the preview compositor and the ffmpeg argument builder derive from.
/// </summary>
/// <remarks>
/// Immutability is what makes undo a snapshot stack rather than a set of hand-written
/// inverse operations, and it is what lets the decode and audio threads read the document
/// with no lock while the UI thread edits it.
/// </remarks>
public sealed record Project(
    OutputFormat Output,
    ImmutableArray<SourceMedia> Sources,
    ImmutableArray<BaseSegment> Base,
    ImmutableArray<CropSpan> Crops,
    ImmutableArray<OverlayClip> Overlays)
{
    public static Project Empty(OutputFormat output) => new(
        output,
        ImmutableArray<SourceMedia>.Empty,
        ImmutableArray<BaseSegment>.Empty,
        ImmutableArray<CropSpan>.Empty,
        ImmutableArray<OverlayClip>.Empty);

    /// <summary>Total length of the edit, in output frames.</summary>
    public long DurationFrames =>
        Base.IsEmpty ? 0 : Base[^1].TimelineStart + Base[^1].LengthFrames;

    /// <summary>
    /// The size of the picture that comes out: the whole-timeline crop's own size if there
    /// is one, and the output's otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes a free-shaped crop honest. Reframing an entire recording to a tall
    /// strip should deliver a tall file — the alternative is stretching the strip back to a
    /// landscape frame, which is the one thing a crop must never do. It is derived rather
    /// than stored so it cannot disagree with the crop it describes: drop the crop and the
    /// delivered size goes back to the output's in the same edit, with no second field to
    /// keep in step and nothing to undo separately.
    /// </para>
    /// <para>
    /// Only a crop covering <em>everything</em> qualifies. A delivered size is a property of
    /// the file, so it cannot follow one span of the timeline; a partial crop zooms to fill
    /// as it always has. That is also why this asks for a single span rather than for
    /// coverage: <c>TimelineEdits</c> coalesces equal neighbours, so one span is what a
    /// whole-timeline crop actually looks like, and two different crops that happen to abut
    /// have no single size to offer.
    /// </para>
    /// <para>
    /// Nothing else moves with it. <see cref="CropSpan.Rect"/> and every other rectangle
    /// stay in the space they were in — see <see cref="OverlayClip"/> for the one seam where
    /// delivered and output pixels part company.
    /// </para>
    /// </remarks>
    public (int Width, int Height) DeliveredSize =>
        Crops.Length == 1 && Crops[0].Range.Start == 0 && Crops[0].Range.End == DurationFrames
            ? (Crops[0].Rect.W, Crops[0].Rect.H)
            : (Output.Width, Output.Height);

    public SourceMedia? FindSource(int id)
    {
        foreach (var s in Sources)
            if (s.Id == id) return s;
        return null;
    }

    public SourceMedia RequireSource(int id) =>
        FindSource(id) ?? throw new InvalidOperationException($"Project has no source with id {id}.");

    /// <summary>
    /// Compares two projects by content.
    /// </summary>
    /// <remarks>
    /// The compiler-generated record equality would compare the <see cref="ImmutableArray{T}"/>
    /// members with the default comparer, which for that type is reference equality on the
    /// underlying array — so two documents describing an identical edit would compare
    /// unequal. That silently breaks any "did this change?" check, including verifying
    /// that a session round-tripped or that an undo landed back where it started.
    /// </remarks>
    public bool Equals(Project? other) =>
        other is not null
        && Output == other.Output
        && Sources.SequenceEqual(other.Sources)
        && Base.SequenceEqual(other.Base)
        && Crops.SequenceEqual(other.Crops)
        && Overlays.SequenceEqual(other.Overlays);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Output);

        // Documents are small — tens of segments — so hashing every element is cheap and
        // keeps the hash consistent with the element-wise equality above.
        foreach (var source in Sources) hash.Add(source);
        foreach (var segment in Base) hash.Add(segment);
        foreach (var crop in Crops) hash.Add(crop);
        foreach (var overlay in Overlays) hash.Add(overlay);

        return hash.ToHashCode();
    }
}
