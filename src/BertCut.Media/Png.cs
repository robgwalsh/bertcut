using System.Buffers.Binary;
using System.IO.Compression;
using BertCut.Media.Decode;

namespace BertCut.Media;

/// <summary>
/// Writes a decoded frame out as a PNG.
/// </summary>
/// <remarks>
/// <para>
/// Exists so a composited frame can be looked at rather than only asserted about. The
/// preview compositor produces the same buffer the export path has to agree with, so being
/// able to dump one to disk from a headless test is the cheapest possible way to answer
/// "is the crop where I think it is" — no window, no WPF, no display.
/// </para>
/// <para>
/// Hand-rolled rather than taken from <c>System.Drawing</c> or WIC because both drag a
/// Windows imaging stack into a library whose whole point is that it renders without one,
/// and the format's uncompressed-through-zlib path is about sixty lines.
/// </para>
/// <para>
/// Alpha is dropped: colour type 2, not 6. Frames arrive straight from <c>sws_scale</c> as
/// BGRA and nothing above the decoder maintains the alpha channel, so a viewer that honours
/// it would show a fully transparent image and invite the conclusion that the compositor had
/// produced nothing. Discarding the channel makes what is on screen unambiguous.
/// </para>
/// </remarks>
public static class Png
{
    /// <summary>Writes a decoded frame to <paramref name="path"/>.</summary>
    public static void Save(DecodedFrame frame, string path) =>
        Save(path, frame.Width, frame.Height, frame.Stride, frame.Pixels);

    /// <summary>Writes a BGRA buffer to <paramref name="path"/>.</summary>
    public static void Save(string path, int width, int height, int stride, ReadOnlySpan<byte> bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(bgra.Length, (long)stride * height);

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8;      // bit depth
        header[9] = 2;      // colour type: truecolour, no alpha
        header[10] = 0;     // deflate
        header[11] = 0;     // adaptive filtering
        header[12] = 0;     // no interlace
        WriteChunk(file, "IHDR", header);

        WriteChunk(file, "IDAT", Deflate(width, height, stride, bgra));
        WriteChunk(file, "IEND", []);
    }

    /// <summary>Packs the scanlines as BGR→RGB and zlib-compresses them.</summary>
    /// <remarks>
    /// Every row is prefixed with filter type 0. Filtering would compress better, but the
    /// frames this writes are a debugging artefact with a lifetime of minutes, and an
    /// unfiltered row is one memcpy-shaped loop that cannot be subtly wrong.
    /// </remarks>
    private static byte[] Deflate(int width, int height, int stride, ReadOnlySpan<byte> bgra)
    {
        var raw = new byte[(1 + (long)width * 3) * height];
        var at = 0;

        for (var y = 0; y < height; y++)
        {
            raw[at++] = 0;

            var row = bgra.Slice(y * stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                raw[at++] = row[x * 4 + 2];     // R
                raw[at++] = row[x * 4 + 1];     // G
                raw[at++] = row[x * 4];         // B
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> tag = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        stream.Write(tag);
        stream.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(tag, data));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var c = 0xFFFFFFFFu;

        foreach (var b in first) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in second) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);

        return c ^ 0xFFFFFFFFu;
    }
}
