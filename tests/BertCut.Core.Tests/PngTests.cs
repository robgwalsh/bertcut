using System.Buffers.Binary;
using System.IO.Compression;
using BertCut.Media;
using BertCut.Media.Decode;

namespace BertCut.Core.Tests;

/// <summary>
/// Pins the PNG writer against the format, byte for byte.
/// </summary>
/// <remarks>
/// The encoder exists so a composited frame can be looked at, which means a bug in it would
/// be read as a bug in the compositor — a black image blamed on the wrong layer. So these
/// tests decode what it wrote rather than checking it merely produced bytes: the channel
/// order, the filter bytes, the declared dimensions and every chunk CRC.
/// </remarks>
public class PngTests
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bertcut-png", Guid.NewGuid().ToString("N"));

    private string Path_(string name)
    {
        Directory.CreateDirectory(_dir);
        return System.IO.Path.Combine(_dir, name);
    }

    [Fact]
    public void Writes_a_well_formed_signature_and_header()
    {
        var path = Path_("header.png");
        Png.Save(path, 3, 2, 12, new byte[24]);

        var bytes = File.ReadAllBytes(path);

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], bytes[..8]);

        var chunks = Chunks(bytes);

        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));

        var header = chunks[0].Data;
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4)));
        Assert.Equal(8, header[8]);
        Assert.Equal(2, header[9]);     // truecolour, no alpha
        Assert.Equal(0, header[10]);
        Assert.Equal(0, header[11]);
        Assert.Equal(0, header[12]);
    }

    [Fact]
    public void Reorders_bgra_to_rgb_and_drops_alpha()
    {
        // One pixel per corner of a 2x2, each a channel that can only be confused with
        // another if the swap is wrong. Alpha is zero throughout: an encoder that kept it
        // would produce an entirely transparent image.
        byte[] pixels =
        [
            0x00, 0x00, 0xFF, 0x00,   0x00, 0xFF, 0x00, 0x00,     // red,  green
            0xFF, 0x00, 0x00, 0x00,   0x10, 0x20, 0x30, 0x00,     // blue, mixed
        ];

        var path = Path_("colours.png");
        Png.Save(path, 2, 2, 8, pixels);

        var raw = Inflate(Chunks(File.ReadAllBytes(path)).Single(c => c.Type == "IDAT").Data);

        Assert.Equal(
        [
            0x00, 0xFF, 0x00, 0x00,  0x00, 0xFF, 0x00,            // filter, red,  green
            0x00, 0x00, 0x00, 0xFF,  0x30, 0x20, 0x10,            // filter, blue, mixed
        ], raw);
    }

    [Fact]
    public void Honours_a_stride_wider_than_the_row()
    {
        // Decoders hand back padded buffers, and reading the padding as pixels shifts every
        // row by a few bytes — a skew that looks like a compositor fault.
        byte[] pixels =
        [
            0x01, 0x02, 0x03, 0xFF,   0xAA, 0xBB, 0xCC, 0xDD,     // row 0 + padding
            0x04, 0x05, 0x06, 0xFF,   0xAA, 0xBB, 0xCC, 0xDD,     // row 1 + padding
        ];

        var path = Path_("stride.png");
        Png.Save(path, 1, 2, 8, pixels);

        var raw = Inflate(Chunks(File.ReadAllBytes(path)).Single(c => c.Type == "IDAT").Data);

        Assert.Equal([0x00, 0x03, 0x02, 0x01, 0x00, 0x06, 0x05, 0x04], raw);
    }

    [Fact]
    public void Every_chunk_carries_a_correct_crc()
    {
        var path = Path_("crc.png");
        Png.Save(path, 7, 5, 28, new byte[140]);

        // Chunks() validates each CRC as it reads, so reaching the end is the assertion.
        Assert.Equal(3, Chunks(File.ReadAllBytes(path)).Count);
    }

    [Fact]
    public void Saves_a_decoded_frame_at_its_own_size()
    {
        var frame = new DecodedFrame(64, 48);
        for (var i = 0; i < frame.Pixels.Length; i++) frame.Pixels[i] = (byte)i;

        var path = Path_("frame.png");
        Png.Save(frame, path);

        var header = Chunks(File.ReadAllBytes(path))[0].Data;
        Assert.Equal(64, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4)));
        Assert.Equal(48, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4)));
    }

    [Fact]
    public void Rejects_a_buffer_too_small_for_the_stated_size() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Png.Save(Path_("short.png"), 4, 4, 16, new byte[60]));

    // ---- reading back ------------------------------------------------------------------

    private sealed record Chunk(string Type, byte[] Data);

    /// <summary>Splits a PNG into its chunks, throwing on any CRC that does not match.</summary>
    private static List<Chunk> Chunks(byte[] bytes)
    {
        var chunks = new List<Chunk>();

        for (var at = 8; at < bytes.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at, 4));
            var type = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);
            var data = bytes[(at + 8)..(at + 8 + length)];
            var crc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8 + length, 4));

            Assert.Equal(crc, Crc32(bytes.AsSpan(at + 4, 4 + length)));

            chunks.Add(new Chunk(type, data));
            at += 12 + length;
        }

        return chunks;
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;

        foreach (var b in bytes)
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c ^ 0xFFFFFFFFu;
    }
}
