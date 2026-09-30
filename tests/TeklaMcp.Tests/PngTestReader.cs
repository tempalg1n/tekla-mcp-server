using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace TeklaMcp.Tests;

/// <summary>
/// Independent PNG reader for the tests (8-bit RGB only): verifies chunk CRCs and the zlib
/// trailer, reverses the five scanline filters and returns the pixels, so the encoder is checked
/// against the specification rather than against itself.
/// </summary>
internal static class PngTestReader
{
    public sealed class Image
    {
        public int Width;
        public int Height;
        public byte[] Rgb = Array.Empty<byte>();

        public (byte R, byte G, byte B) At(int x, int y)
        {
            var i = (y * Width + x) * 3;
            return (Rgb[i], Rgb[i + 1], Rgb[i + 2]);
        }
    }

    public static Image Decode(byte[] png)
    {
        var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (!png.Take(8).SequenceEqual(signature)) throw new InvalidDataException("Bad PNG signature.");

        var image = new Image();
        var idat = new MemoryStream();
        var pos = 8;
        var sawEnd = false;
        while (pos < png.Length)
        {
            var length = (int)ReadUInt32(png, pos);
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = new byte[length];
            Array.Copy(png, pos + 8, data, 0, length);
            var crc = ReadUInt32(png, pos + 8 + length);
            if (crc != Crc32(png, pos + 4, length + 4)) throw new InvalidDataException("CRC mismatch in " + type);

            switch (type)
            {
                case "IHDR":
                    image.Width = (int)ReadUInt32(data, 0);
                    image.Height = (int)ReadUInt32(data, 4);
                    if (data[8] != 8 || data[9] != 2 || data[12] != 0)
                        throw new InvalidDataException("Only 8-bit RGB non-interlaced is supported.");
                    break;
                case "IDAT":
                    idat.Write(data, 0, data.Length);
                    break;
                case "IEND":
                    sawEnd = true;
                    break;
            }
            pos += 12 + length;
        }
        if (!sawEnd) throw new InvalidDataException("Missing IEND.");

        var zlib = idat.ToArray();
        if (((zlib[0] << 8) | zlib[1]) % 31 != 0) throw new InvalidDataException("Bad zlib header check.");
        byte[] raw;
        using (var inflate = new DeflateStream(new MemoryStream(zlib, 2, zlib.Length - 6), CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            inflate.CopyTo(output);
            raw = output.ToArray();
        }
        if (ReadUInt32(zlib, zlib.Length - 4) != Adler32(raw)) throw new InvalidDataException("Adler-32 mismatch.");

        var stride = image.Width * 3;
        if (raw.Length != (stride + 1) * image.Height) throw new InvalidDataException("Unexpected raw length.");
        image.Rgb = new byte[stride * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var filter = raw[y * (stride + 1)];
            for (var i = 0; i < stride; i++)
            {
                int x = raw[y * (stride + 1) + 1 + i];
                int a = i >= 3 ? image.Rgb[y * stride + i - 3] : 0;
                int b = y > 0 ? image.Rgb[(y - 1) * stride + i] : 0;
                int c = i >= 3 && y > 0 ? image.Rgb[(y - 1) * stride + i - 3] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException("Unknown filter " + filter),
                };
                image.Rgb[y * stride + i] = (byte)(x + predicted);
            }
        }
        return image;
    }

    /// <summary>Filter types used, per row — lets a test prove the adaptive choice is exercised.</summary>
    public static HashSet<int> FilterTypes(byte[] png)
    {
        var image = Decode(png);
        var pos = 8;
        var idat = new MemoryStream();
        while (pos < png.Length)
        {
            var length = (int)ReadUInt32(png, pos);
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IDAT") idat.Write(png, pos + 8, length);
            pos += 12 + length;
        }
        var zlib = idat.ToArray();
        using var inflate = new DeflateStream(new MemoryStream(zlib, 2, zlib.Length - 6), CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        var raw = output.ToArray();
        var set = new HashSet<int>();
        for (var y = 0; y < image.Height; y++) set.Add(raw[y * (image.Width * 3 + 1)]);
        return set;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static uint ReadUInt32(byte[] b, int offset) =>
        ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) | ((uint)b[offset + 2] << 8) | b[offset + 3];

    private static uint Crc32(byte[] data, int offset, int count)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = offset; i < offset + count; i++)
        {
            crc ^= data[i];
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
