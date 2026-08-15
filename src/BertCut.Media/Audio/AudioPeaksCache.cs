using BertCut.Core.Audio;
using BertCut.Core.Session;
using BertCut.Media.Decode;

namespace BertCut.Media.Audio;

/// <summary>
/// Builds a source's audio envelope once and keeps it on disk, keyed by content key.
/// </summary>
/// <remarks>
/// <para>
/// Same reasoning as <see cref="FilmstripCache"/>, and the same directory: the envelope is a
/// property of the file's contents, not of any edit, so cutting never invalidates it and
/// renaming or moving the recording never orphans it. Two things read it — the waveform lane
/// under the timeline, and the correlation behind the sync key — and both want the whole
/// thing in memory, which at 800 bytes a second is about 3 MB an hour.
/// </para>
/// <para>
/// The file is written to a temporary name and moved into place, so an interrupted build
/// leaves no half-written envelope to be loaded as if it were complete.
/// </para>
/// </remarks>
public static class AudioPeaksCache
{
    /// <summary>Identifies the format, so a stale file from an older layout is ignored.</summary>
    private const uint Magic = 0x4B50_4342;   // "BCPK"

    private const int Version = 1;

    public static string PathFor(string contentKey) =>
        Path.Combine(AppPaths.Cache, contentKey, "peaks.bin");

    public static bool IsBuilt(string contentKey) => File.Exists(PathFor(contentKey));

    /// <summary>
    /// Returns the cached envelope, building it from <paramref name="sourcePath"/> if needed.
    /// </summary>
    /// <remarks>
    /// Returns null when the file has no audio at all, which is an ordinary case — a silent
    /// screen recording is common — and is reported by the caller as "no audio to sync
    /// against" rather than as a failure.
    /// </remarks>
    public static AudioPeaks? GetOrBuild(
        string sourcePath,
        string contentKey,
        int sampleRate,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (TryLoad(contentKey) is { } cached) return cached;

        if (!AudioDecoder.HasAudioStream(sourcePath)) return null;

        using var decoder = new AudioDecoder(sourcePath, sampleRate);
        var peaks = AudioPeaksBuilder.Build(decoder, AudioPeaks.DefaultRate, progress, cancellationToken);

        Save(contentKey, peaks);
        return peaks;
    }

    /// <summary>
    /// Loads a cached envelope, or null when there is none or it cannot be read.
    /// </summary>
    /// <remarks>
    /// Never throws. A corrupt cache entry must degrade to "build it again", never to an
    /// editor that will not open the file.
    /// </remarks>
    public static AudioPeaks? TryLoad(string contentKey)
    {
        var path = PathFor(contentKey);
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != Magic) return null;
            if (reader.ReadInt32() != Version) return null;

            var rate = reader.ReadInt32();
            var count = reader.ReadInt32();

            if (rate < 1 || count < 0) return null;

            // Two floats per bucket after the 16-byte header; anything else is truncated.
            if (stream.Length - stream.Position != (long)count * 2 * sizeof(float)) return null;

            var min = new float[count];
            var max = new float[count];

            for (var i = 0; i < count; i++) min[i] = reader.ReadSingle();
            for (var i = 0; i < count; i++) max[i] = reader.ReadSingle();

            return new AudioPeaks(rate, min, max);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    public static void Save(string contentKey, AudioPeaks peaks)
    {
        ArgumentNullException.ThrowIfNull(peaks);

        var path = PathFor(contentKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";

        using (var stream = File.Create(temporary))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(peaks.Rate);
            writer.Write(peaks.Count);

            foreach (var value in peaks.Min) writer.Write(value);
            foreach (var value in peaks.Max) writer.Write(value);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
