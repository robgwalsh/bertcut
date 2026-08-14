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
/// A crop applied over a timeline range, zoomed to fill the output.
/// </summary>
/// <remarks>
/// <see cref="Rect"/> is in output-space pixels and is aspect-locked to the output format.
/// Output space means the user drags on exactly what they see. The aspect lock means
/// zoom-to-fill is crop-then-scale with no letterbox or pad branch in either renderer,
/// which removes a place where the D3D11 preview and the ffmpeg export could disagree.
/// </remarks>
public readonly record struct CropSpan(FrameRange Range, RectI Rect);

/// <summary>A picture-in-picture clip on the overlay track.</summary>
public readonly record struct OverlayClip(
    FrameRange Range,
    int SourceId,
    long SourceStartFrame,
    RectI Dest,
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
