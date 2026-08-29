using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Core.Session;
using BertCut.Core.Time;

namespace BertCut.Media;

/// <summary>
/// Keeps a source's timestamp index on disk, keyed by content key.
/// </summary>
/// <remarks>
/// <para>
/// Building one is three ffprobe launches, the last of which walks every packet in the
/// container and parses a line per frame. That is the documented "couple of seconds" for a
/// two-hour recording, and it was being paid again on every open, every append and every
/// session restore of a file already indexed — including at startup, when a restore re-probes
/// each source before the editor is usable.
/// </para>
/// <para>
/// <b>The key is the content key, not the path.</b> Same reasoning as the envelope beside it:
/// the index is a property of the file's contents, so renaming or moving a recording never
/// orphans it and re-encoding one never gets an answer describing the old file. It is the same
/// <c>cache/&lt;contentKey&gt;</c> directory <see cref="Audio.AudioPeaksCache"/> writes into, so
/// nothing here may ever delete that directory wholesale — see
/// <see cref="FilmstripCache.PurgeThumbnails"/> for what happens when something does.
/// </para>
/// <para>
/// About 864 KB per hour at 30 fps, which <c>SourceIndex</c>'s own remarks have claimed was
/// cached on disk since before anything wrote it.
/// </para>
/// </remarks>
public static class SourceIndexCache
{
    /// <summary>Identifies the format, so a stale file from an older layout is ignored.</summary>
    private const uint Magic = 0x5849_4342;   // "BCIX"

    private const int Version = 1;

    public static string PathFor(string contentKey) =>
        Path.Combine(AppPaths.Cache, contentKey, "index.bin");

    /// <summary>
    /// The remembered probe for this content, or null when there is none or it cannot be read.
    /// </summary>
    /// <remarks>
    /// <paramref name="path"/> is stamped into the result rather than compared against: the whole
    /// point of keying by content is that the same bytes under a new name are the same source.
    /// </remarks>
    public static ProbeResult? Load(string contentKey, string path)
    {
        var file = PathFor(contentKey);

        try
        {
            if (!File.Exists(file)) return null;

            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != Magic) return null;
            if (reader.ReadInt32() != Version) return null;

            var timeBase = new Rational(reader.ReadInt32(), reader.ReadInt32());
            var frameRate = new Rational(reader.ReadInt32(), reader.ReadInt32());

            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            var variableRate = reader.ReadBoolean();
            var hasAudio = reader.ReadBoolean();
            var audioSampleRate = reader.ReadInt32();
            var videoCodec = reader.ReadString();
            var pixelFormat = reader.ReadString();

            var count = reader.ReadInt32();
            if (count <= 0) return null;

            var pts = new long[count];
            for (var i = 0; i < count; i++) pts[i] = reader.ReadInt64();

            var keyCount = reader.ReadInt32();
            if (keyCount < 0) return null;

            var keyFrames = new int[keyCount];
            for (var i = 0; i < keyCount; i++) keyFrames[i] = reader.ReadInt32();

            var media = new SourceMedia(
                Id: 0,
                Path: path,
                ContentKey: contentKey,
                FrameCount: count,
                Width: width,
                Height: height,
                FrameRate: frameRate,
                IsVariableFrameRate: variableRate,
                HasAudio: hasAudio,
                AudioSampleRate: audioSampleRate,
                VideoCodec: videoCodec,
                PixelFormat: pixelFormat);

            return new ProbeResult(media, new SourceIndex(timeBase, pts, keyFrames));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or EndOfStreamException or ObjectDisposedException
                                      or ArgumentException)
        {
            // A cache that cannot be read is a cache miss, never a failure to open a video.
            return null;
        }
    }

    /// <summary>
    /// Writes the probe, atomically. Best effort: failing to cache must never fail an open.
    /// </summary>
    public static void Save(string contentKey, ProbeResult result)
    {
        var path = PathFor(contentKey);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temporary = path + ".tmp";
            var index = result.Index;
            var media = result.Media;

            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(Version);

                writer.Write(index.TimeBase.Num);
                writer.Write(index.TimeBase.Den);
                writer.Write(media.FrameRate.Num);
                writer.Write(media.FrameRate.Den);

                writer.Write(media.Width);
                writer.Write(media.Height);
                writer.Write(media.IsVariableFrameRate);
                writer.Write(media.HasAudio);
                writer.Write(media.AudioSampleRate);
                writer.Write(media.VideoCodec);
                writer.Write(media.PixelFormat);

                writer.Write(index.Pts.Length);
                foreach (var value in index.Pts) writer.Write(value);

                writer.Write(index.KeyFrames.Length);
                foreach (var value in index.KeyFrames) writer.Write(value);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // Same contract as the envelope beside it: an optimisation, never a hard dependency.
        }
    }
}
