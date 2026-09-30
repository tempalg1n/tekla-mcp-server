using System;
using System.IO;
using System.IO.Compression;

namespace TeklaMcp.Core.Rendering;

/// <summary>
/// Minimal PNG writer (8-bit RGB, non-interlaced) so Core can produce images without a drawing
/// library. Rows use the adaptive per-row filter choice from the PNG specification (minimum sum
/// of absolute differences); the zlib stream is DeflateStream plus the RFC 1950 header and
/// Adler-32 trailer.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(RasterCanvas canvas) =>
        EncodeRgb(canvas.Width, canvas.Height, canvas.Pixels);

    /// <summary>Encode row-major RGB (3 bytes per pixel).</summary>
    public static byte[] EncodeRgb(int width, int height, byte[] rgb)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (rgb == null || rgb.Length < width * height * 3) throw new ArgumentException("Pixel buffer too small.", nameof(rgb));

        using var output = new MemoryStream();
        output.Write(Signature, 0, Signature.Length);

        var ihdr = new byte[13];
        WriteUInt32BigEndian(ihdr, 0, (uint)width);
        WriteUInt32BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour RGB
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // no interlace
        WriteChunk(output, "IHDR", ihdr);

        WriteChunk(output, "IDAT", Zlib(Filter(width, height, rgb)));
        WriteChunk(output, "IEND", new byte[0]);
        return output.ToArray();
    }

    private static byte[] Filter(int width, int height, byte[] rgb)
    {
        const int bpp = 3;
        var stride = width * bpp;
        var raw = new byte[(stride + 1) * height];
        var candidate = new byte[5][];
        for (var f = 0; f < 5; f++) candidate[f] = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            var prev = (y - 1) * stride;
            var bestFilter = 0;
            var bestScore = long.MaxValue;
            for (var f = 0; f < 5; f++)
            {
                var line = candidate[f];
                long score = 0;
                for (var i = 0; i < stride; i++)
                {
                    int x = rgb[row + i];
                    int a = i >= bpp ? rgb[row + i - bpp] : 0;
                    int b = y > 0 ? rgb[prev + i] : 0;
                    int c = i >= bpp && y > 0 ? rgb[prev + i - bpp] : 0;
                    int predicted;
                    switch (f)
                    {
                        case 1: predicted = a; break;
                        case 2: predicted = b; break;
                        case 3: predicted = (a + b) >> 1; break;
                        case 4: predicted = Paeth(a, b, c); break;
                        default: predicted = 0; break;
                    }
                    var v = (byte)(x - predicted);
                    line[i] = v;
                    score += v < 128 ? v : 256 - v;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = f;
                }
            }

            var dst = y * (stride + 1);
            raw[dst] = (byte)bestFilter;
            Buffer.BlockCopy(candidate[bestFilter], 0, raw, dst + 1, stride);
        }
        return raw;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); // CM=8 (deflate), CINFO=7 (32 KiB window)
        ms.WriteByte(0x9C); // FLEVEL=2 (default), FCHECK so that 0x789C % 31 == 0
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        var adler = Adler32(data);
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        const uint mod = 65521;
        uint a = 1, b = 0;
        var i = 0;
        while (i < data.Length)
        {
            // 5552 is the largest block for which b cannot overflow 32 bits before the modulo.
            var end = Math.Min(data.Length, i + 5552);
            for (; i < end; i++)
            {
                a += data[i];
                b += a;
            }
            a %= mod;
            b %= mod;
        }
        return (b << 16) | a;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var header = new byte[8];
        WriteUInt32BigEndian(header, 0, (uint)data.Length);
        for (var i = 0; i < 4; i++) header[4 + i] = (byte)type[i];
        output.Write(header, 0, 8);
        output.Write(data, 0, data.Length);

        var crc = 0xFFFFFFFFu;
        for (var i = 4; i < 8; i++) crc = CrcTable[(crc ^ header[i]) & 0xFF] ^ (crc >> 8);
        foreach (var d in data) crc = CrcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;

        var trailer = new byte[4];
        WriteUInt32BigEndian(trailer, 0, crc);
        output.Write(trailer, 0, 4);
    }

    private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
