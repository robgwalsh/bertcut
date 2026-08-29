using System.Text.Json;
using System.Text.Json.Serialization;
using BertCut.Core.Export;
using BertCut.Core.Session;

namespace BertCut.Media;

/// <summary>What a previous launch worked out about the ffmpeg it found.</summary>
public sealed record CapabilityDocument
{
    /// <summary>Schema version. The loader refuses anything else.</summary>
    public int V { get; init; } = 1;

    /// <summary>The exe this describes, with the size and timestamp it had when probed.</summary>
    public string Path { get; init; } = "";

    public long Length { get; init; }

    public long WriteTicks { get; init; }

    public string Version { get; init; } = "";

    public List<string> Encoders { get; init; } = [];

    public List<string> Filters { get; init; } = [];

    public bool CudaDecode { get; init; }
}

[JsonSerializable(typeof(CapabilityDocument))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CapabilityJsonContext : JsonSerializerContext;

/// <summary>
/// Remembers the answer <see cref="FfmpegRuntime.Locate"/> paid for last time.
/// </summary>
/// <remarks>
/// <para>
/// Locating ffmpeg is the single largest thing between the process starting and the window
/// existing: <c>-version</c>, <c>-encoders</c>, <c>-filters</c> and <c>-hwaccels</c>, then a real
/// 256x256 encode per candidate hardware encoder and a CUDA device open — five to eight child
/// processes, each mapping ~139 MB of shared libraries, and each self-measured at 150-300 ms.
/// None of it changes between launches on a machine that has not been touched, and all of it was
/// being paid again every time.
/// </para>
/// <para>
/// <b>The fingerprint is the exe's path, length and write time</b>, so a hit costs one
/// <c>File.Stat</c> and launches nothing at all — not even <c>-version</c>, whose banner is
/// stored rather than re-read. An update replaces the binaries and changes the fingerprint, so a
/// new build is never described by an old answer.
/// </para>
/// <para>
/// What a fingerprint cannot see is the <i>machine</i> changing underneath it — a card removed, a
/// driver rolled back — which would leave a cached NVENC claiming to work. That is why
/// <see cref="Invalidate"/> exists and why the export path calls it when an encoder fails to
/// initialise: the cache is a memo of a measurement, and the measurement is retaken the moment
/// reality disagrees with it.
/// </para>
/// </remarks>
public static class FfmpegCapabilityCache
{
    private const int Version = 1;

    public static string FilePath => Path.Combine(AppPaths.Cache, "ffmpeg-capabilities.json");

    /// <summary>
    /// The remembered answer for <paramref name="exePath"/>, or null when there is none, it
    /// describes a different build, or it cannot be read.
    /// </summary>
    public static (string Version, EncoderCapabilities Capabilities)? Load(string exePath)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var document = JsonSerializer.Deserialize(
                File.ReadAllText(FilePath), CapabilityJsonContext.Default.CapabilityDocument);

            if (document is null || document.V != Version) return null;
            if (!Matches(document, exePath)) return null;

            return (document.Version, new EncoderCapabilities(
                document.Encoders.ToHashSet(StringComparer.Ordinal),
                document.Filters.ToHashSet(StringComparer.Ordinal),
                document.CudaDecode));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Remembers what was probed. Best effort: a cache that cannot be written is not an error.</summary>
    public static void Save(string exePath, string version, EncoderCapabilities capabilities)
    {
        try
        {
            var info = new FileInfo(exePath);
            if (!info.Exists) return;

            var document = new CapabilityDocument
            {
                V = Version,
                Path = exePath,
                Length = info.Length,
                WriteTicks = info.LastWriteTimeUtc.Ticks,
                Version = version,
                Encoders = [.. capabilities.Encoders.Order(StringComparer.Ordinal)],
                Filters = [.. capabilities.Filters.Order(StringComparer.Ordinal)],
                CudaDecode = capabilities.HasCudaHwaccel,
            };

            System.IO.Directory.CreateDirectory(AppPaths.Cache);

            var temporary = FilePath + ".tmp";
            File.WriteAllText(
                temporary, JsonSerializer.Serialize(document, CapabilityJsonContext.Default.CapabilityDocument));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            // A cache is an optimisation; failing to write one must never fail a launch.
        }
    }

    /// <summary>
    /// Forgets the remembered answer, so the next launch re-probes.
    /// </summary>
    /// <remarks>
    /// Called when an encoder the cache promised turns out not to initialise, which is what a
    /// removed card or a rolled-back driver looks like from here.
    /// </remarks>
    public static void Invalidate()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* next launch retries */ }
    }

    private static bool Matches(CapabilityDocument document, string exePath)
    {
        var info = new FileInfo(exePath);
        if (!info.Exists) return false;

        return string.Equals(document.Path, exePath, StringComparison.OrdinalIgnoreCase)
            && document.Length == info.Length
            && document.WriteTicks == info.LastWriteTimeUtc.Ticks;
    }
}
