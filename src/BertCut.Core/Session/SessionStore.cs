using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Core.Session;

/// <summary>Serialized form of a project. Compact, versioned, and human-readable.</summary>
public sealed record ProjectDocument
{
    /// <summary>Schema version. The loader switches on this.</summary>
    public int V { get; init; } = 1;

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Frame rate as "num/den" — never a float, which would not round-trip.</summary>
    public string Rate { get; init; } = "30/1";

    public int SampleRate { get; init; } = 48000;

    public List<SourceDocument> Sources { get; init; } = [];

    /// <summary>Base segments as [length, sourceId, sourceStart]; timeline starts are implied.</summary>
    public List<long[]> Base { get; init; } = [];

    /// <summary>Crops as [start, end, x, y, w, h].</summary>
    public List<long[]> Crops { get; init; } = [];

    /// <summary>Overlays as [start, end, sourceId, sourceStart, x, y, w, h, muted].</summary>
    public List<long[]> Overlays { get; init; } = [];
}

public sealed record SourceDocument
{
    public int Id { get; init; }
    public string Path { get; init; } = "";
    public string ContentKey { get; init; } = "";
    public long FrameCount { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Rate { get; init; } = "30/1";
    public bool Vfr { get; init; }
    public bool HasAudio { get; init; }
    public int AudioSampleRate { get; init; }
    public string Codec { get; init; } = "";
    public string PixelFormat { get; init; } = "";
}

[JsonSerializable(typeof(ProjectDocument))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class SessionJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes autosaved sessions.
/// </summary>
/// <remarks>
/// <para>
/// A session is keyed by the content key of its first source, which is how reopening a
/// video restores the edits made to it last time without any Save or Open-project step.
/// </para>
/// <para>
/// Writes go to a temporary file and are then swapped in with <c>File.Replace</c>, which
/// also produces a <c>.bak</c>. A crash mid-write therefore cannot leave a torn session
/// file — the worst case is losing the last few hundred milliseconds of edits.
/// </para>
/// </remarks>
public static class SessionStore
{
    private static string Root => AppPaths.Sessions;

    public static string PathFor(string sessionKey) =>
        Path.Combine(Root, sessionKey, "project.json");

    /// <summary>Writes a session, replacing any previous one atomically.</summary>
    public static void Save(string sessionKey, Project project)
    {
        var path = PathFor(sessionKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = JsonSerializer.Serialize(ToDocument(project), SessionJsonContext.Default.ProjectDocument);

        var temp = path + ".tmp";
        File.WriteAllText(temp, json);

        if (File.Exists(path)) File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(temp, path);
    }

    /// <summary>
    /// Loads a session, or null when none exists or it cannot be read.
    /// </summary>
    /// <remarks>
    /// Falls back to the backup before giving up, and never throws: a corrupt session must
    /// degrade to "start fresh", not to an app that will not open the file at all.
    /// </remarks>
    public static Project? TryLoad(string sessionKey)
    {
        var path = PathFor(sessionKey);

        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;

            try
            {
                var document = JsonSerializer.Deserialize(
                    File.ReadAllText(candidate), SessionJsonContext.Default.ProjectDocument);

                if (document is not null) return FromDocument(document);
            }
            catch (Exception e) when (e is JsonException or IOException or ArgumentException)
            {
                // Try the backup, then fall through to a fresh start.
            }
        }

        return null;
    }

    internal static ProjectDocument ToDocument(Project p) => new()
    {
        Width = p.Output.Width,
        Height = p.Output.Height,
        Rate = p.Output.FrameRate.ToString(),
        SampleRate = p.Output.SampleRate,
        Sources = [.. p.Sources.Select(s => new SourceDocument
        {
            Id = s.Id,
            Path = s.Path,
            ContentKey = s.ContentKey,
            FrameCount = s.FrameCount,
            Width = s.Width,
            Height = s.Height,
            Rate = s.FrameRate.ToString(),
            Vfr = s.IsVariableFrameRate,
            HasAudio = s.HasAudio,
            AudioSampleRate = s.AudioSampleRate,
            Codec = s.VideoCodec,
            PixelFormat = s.PixelFormat,
        })],
        Base = [.. p.Base.Select(b => new[] { b.LengthFrames, b.SourceId, b.SourceStartFrame })],
        Crops = [.. p.Crops.Select(c => new long[]
            { c.Range.Start, c.Range.End, c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H })],
        Overlays = [.. p.Overlays.Select(o => new long[]
        {
            o.Range.Start, o.Range.End, o.SourceId, o.SourceStartFrame,
            o.Dest.X, o.Dest.Y, o.Dest.W, o.Dest.H, o.Muted ? 1 : 0,
        })],
    };

    internal static Project FromDocument(ProjectDocument d)
    {
        if (d.V != 1) throw new InvalidOperationException($"Unsupported session version {d.V}.");

        var rate = Rational.TryParse(d.Rate, out var r) ? r : Rational.FromInt(30);
        var output = new OutputFormat(d.Width, d.Height, rate, d.SampleRate);

        var sources = d.Sources.Select(s => new SourceMedia(
            s.Id, s.Path, s.ContentKey, s.FrameCount, s.Width, s.Height,
            Rational.TryParse(s.Rate, out var sr) ? sr : Rational.FromInt(30),
            s.Vfr, s.HasAudio, s.AudioSampleRate, s.Codec, s.PixelFormat)).ToImmutableArray();

        // Timeline positions are the running total of the lengths, so they are recomputed
        // rather than stored — the file cannot disagree with itself about where a segment sits.
        var segments = ImmutableArray.CreateBuilder<BaseSegment>(d.Base.Count);
        long start = 0;
        foreach (var b in d.Base)
        {
            segments.Add(new BaseSegment(start, b[0], (int)b[1], b[2]));
            start += b[0];
        }

        var crops = d.Crops
            .Select(c => new CropSpan(new FrameRange(c[0], c[1]), new RectI((int)c[2], (int)c[3], (int)c[4], (int)c[5])))
            .ToImmutableArray();

        var overlays = d.Overlays
            .Select(o => new OverlayClip(
                new FrameRange(o[0], o[1]), (int)o[2], o[3],
                new RectI((int)o[4], (int)o[5], (int)o[6], (int)o[7]), o[8] != 0))
            .ToImmutableArray();

        return new Project(output, sources, segments.ToImmutable(), crops, overlays);
    }
}
