using System;
using System.Collections.Generic;
using System.Globalization;

namespace TeklaMcp.Core.Rendering;

/// <summary>An opaque 8-bit RGB colour.</summary>
public readonly struct Rgb : IEquatable<Rgb>
{
    public readonly byte R;
    public readonly byte G;
    public readonly byte B;

    public Rgb(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>From 0xRRGGBB.</summary>
    public static Rgb FromHex(int rgb) =>
        new Rgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    /// <summary>Parse "#RRGGBB" / "RRGGBB"; false when malformed.</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text!.Trim().TrimStart('#');
        if (s.Length != 6 ||
            !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;
        color = FromHex(value);
        return true;
    }

    public string ToHex() => "#" + R.ToString("X2") + G.ToString("X2") + B.ToString("X2");

    public bool Equals(Rgb other) => R == other.R && G == other.G && B == other.B;
    public override bool Equals(object? obj) => obj is Rgb other && Equals(other);
    public override int GetHashCode() => (R << 16) | (G << 8) | B;
    public override string ToString() => ToHex();
}

/// <summary>
/// Minimal software rasterizer for schematic pictures: anti-aliased thick lines, dashed lines,
/// filled polygons and circles, and 8×8 bitmap text, all alpha-blended over an RGB buffer.
/// Pure managed code so the picture is identical on the mock (any OS) and the live backend.
/// Everything clips to the canvas; coordinates are pixel units with (0,0) at the top-left
/// corner of the top-left pixel.
/// </summary>
public sealed class RasterCanvas
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major RGB, 3 bytes per pixel.</summary>
    public byte[] Pixels { get; }

    public RasterCanvas(int width, int height, Rgb background)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 3];
        Clear(background);
    }

    public void Clear(Rgb color)
    {
        for (var i = 0; i < Pixels.Length; i += 3)
        {
            Pixels[i] = color.R;
            Pixels[i + 1] = color.G;
            Pixels[i + 2] = color.B;
        }
    }

    public Rgb GetPixel(int x, int y)
    {
        var i = (y * Width + x) * 3;
        return new Rgb(Pixels[i], Pixels[i + 1], Pixels[i + 2]);
    }

    /// <summary>Blend <paramref name="color"/> over pixel (x, y) with opacity <paramref name="alpha"/> (0..1).</summary>
    public void Blend(int x, int y, Rgb color, double alpha)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || alpha <= 0) return;
        var i = (y * Width + x) * 3;
        if (alpha >= 1)
        {
            Pixels[i] = color.R;
            Pixels[i + 1] = color.G;
            Pixels[i + 2] = color.B;
            return;
        }
        var keep = 1 - alpha;
        Pixels[i] = (byte)(Pixels[i] * keep + color.R * alpha + 0.5);
        Pixels[i + 1] = (byte)(Pixels[i + 1] * keep + color.G * alpha + 0.5);
        Pixels[i + 2] = (byte)(Pixels[i + 2] * keep + color.B * alpha + 0.5);
    }

    /// <summary>Fill the pixels whose centres lie in [x0,x1)×[y0,y1).</summary>
    public void FillRect(double x0, double y0, double x1, double y1, Rgb color, double alpha = 1)
    {
        var ix0 = Math.Max(0, (int)Math.Ceiling(Math.Min(x0, x1) - 0.5));
        var ix1 = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(x0, x1) - 0.5) - 1);
        var iy0 = Math.Max(0, (int)Math.Ceiling(Math.Min(y0, y1) - 0.5));
        var iy1 = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(y0, y1) - 0.5) - 1);
        for (var y = iy0; y <= iy1; y++)
            for (var x = ix0; x <= ix1; x++)
                Blend(x, y, color, alpha);
    }

    /// <summary>1-px outline just inside the rectangle.</summary>
    public void StrokeRect(double x0, double y0, double x1, double y1, Rgb color, double alpha = 1)
    {
        FillRect(x0, y0, x1, y0 + 1, color, alpha);
        FillRect(x0, y1 - 1, x1, y1, color, alpha);
        FillRect(x0, y0 + 1, x0 + 1, y1 - 1, color, alpha);
        FillRect(x1 - 1, y0 + 1, x1, y1 - 1, color, alpha);
    }

    /// <summary>
    /// Anti-aliased line of the given width with round caps. Coverage is the distance from each
    /// pixel centre to the segment, so a zero-length segment draws a dot.
    /// </summary>
    public void DrawLine(double x0, double y0, double x1, double y1, Rgb color, double width = 1, double alpha = 1)
    {
        if (!IsFinite(x0) || !IsFinite(y0) || !IsFinite(x1) || !IsFinite(y1)) return;
        var hw = Math.Max(0.5, width / 2);
        var reach = hw + 1.5;
        if (Math.Max(x0, x1) < -reach || Math.Min(x0, x1) > Width + reach ||
            Math.Max(y0, y1) < -reach || Math.Min(y0, y1) > Height + reach)
            return;

        // Clip far-away endpoints first so a huge off-screen line does not scan millions of columns.
        if (!ClipSegment(ref x0, ref y0, ref x1, ref y1, -reach, -reach, Width + reach, Height + reach))
            return;

        var dx = x1 - x0;
        var dy = y1 - y0;
        var len2 = dx * dx + dy * dy;
        var steep = Math.Abs(dy) > Math.Abs(dx);

        // Walk the major axis; per column (row) touch only the band around the line.
        double majorMin, majorMax;
        if (steep)
        {
            majorMin = Math.Min(y0, y1) - hw - 1;
            majorMax = Math.Max(y0, y1) + hw + 1;
        }
        else
        {
            majorMin = Math.Min(x0, x1) - hw - 1;
            majorMax = Math.Max(x0, x1) + hw + 1;
        }
        var len = Math.Sqrt(len2);
        var cos = len > 0 ? (steep ? Math.Abs(dy) : Math.Abs(dx)) / len : 1;
        var band = hw / Math.Max(cos, 0.5) + 1.5;

        var limit = steep ? Height : Width;
        var mStart = Math.Max(0, (int)Math.Floor(majorMin));
        var mEnd = Math.Min(limit - 1, (int)Math.Ceiling(majorMax));
        for (var m = mStart; m <= mEnd; m++)
        {
            var pm = m + 0.5;
            double center;
            if (steep)
            {
                var t = Math.Abs(dy) > 1e-12 ? Clamp01((pm - y0) / dy) : 0;
                center = x0 + t * dx;
            }
            else
            {
                var t = Math.Abs(dx) > 1e-12 ? Clamp01((pm - x0) / dx) : 0;
                center = y0 + t * dy;
            }

            var nStart = (int)Math.Floor(center - band);
            var nEnd = (int)Math.Ceiling(center + band);
            for (var n = nStart; n <= nEnd; n++)
            {
                var px = steep ? n + 0.5 : pm;
                var py = steep ? pm : n + 0.5;
                var d = DistanceToSegment(px, py, x0, y0, dx, dy, len2);
                var coverage = hw + 0.5 - d;
                if (coverage <= 0) continue;
                if (coverage > 1) coverage = 1;
                if (steep) Blend(n, m, color, coverage * alpha);
                else Blend(m, n, color, coverage * alpha);
            }
        }
    }

    /// <summary>Dashed anti-aliased line; <paramref name="dash"/>/<paramref name="gap"/> in pixels.</summary>
    public void DrawDashedLine(
        double x0, double y0, double x1, double y1, Rgb color, double width, double dash, double gap, double alpha = 1)
    {
        if (!IsFinite(x0) || !IsFinite(y0) || !IsFinite(x1) || !IsFinite(y1)) return;
        var reach = width + 2;
        if (!ClipSegment(ref x0, ref y0, ref x1, ref y1, -reach, -reach, Width + reach, Height + reach))
            return;
        var dx = x1 - x0;
        var dy = y1 - y0;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9 || dash <= 0)
        {
            DrawLine(x0, y0, x1, y1, color, width, alpha);
            return;
        }
        var ux = dx / len;
        var uy = dy / len;
        for (var s = 0.0; s < len; s += dash + gap)
        {
            var e = Math.Min(len, s + dash);
            DrawLine(x0 + ux * s, y0 + uy * s, x0 + ux * e, y0 + uy * e, color, width, alpha);
        }
    }

    /// <summary>Even-odd scanline fill (pixel centres), no anti-aliasing — outline it for smooth edges.</summary>
    public void FillPolygon(IReadOnlyList<(double X, double Y)> points, Rgb color, double alpha = 1)
    {
        if (points == null || points.Count < 3) return;
        var minY = double.MaxValue;
        var maxY = double.MinValue;
        foreach (var p in points)
        {
            if (!IsFinite(p.X) || !IsFinite(p.Y)) return;
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        var yStart = Math.Max(0, (int)Math.Floor(minY));
        var yEnd = Math.Min(Height - 1, (int)Math.Ceiling(maxY));
        var xs = new List<double>();
        for (var y = yStart; y <= yEnd; y++)
        {
            var sy = y + 0.5;
            xs.Clear();
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                var a = points[i];
                var b = points[j];
                if ((a.Y <= sy && b.Y > sy) || (b.Y <= sy && a.Y > sy))
                    xs.Add(a.X + (sy - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            xs.Sort();
            for (var k = 0; k + 1 < xs.Count; k += 2)
            {
                var from = Math.Max(0, (int)Math.Ceiling(xs[k] - 0.5));
                var to = Math.Min(Width - 1, (int)Math.Floor(xs[k + 1] - 0.5));
                for (var x = from; x <= to; x++) Blend(x, y, color, alpha);
            }
        }
    }

    /// <summary>Anti-aliased filled disc.</summary>
    public void FillCircle(double cx, double cy, double radius, Rgb color, double alpha = 1)
    {
        if (!IsFinite(cx) || !IsFinite(cy) || radius <= 0) return;
        var x0 = Math.Max(0, (int)Math.Floor(cx - radius - 1));
        var x1 = Math.Min(Width - 1, (int)Math.Ceiling(cx + radius + 1));
        var y0 = Math.Max(0, (int)Math.Floor(cy - radius - 1));
        var y1 = Math.Min(Height - 1, (int)Math.Ceiling(cy + radius + 1));
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                var coverage = radius + 0.5 - d;
                if (coverage <= 0) continue;
                Blend(x, y, color, Math.Min(1, coverage) * alpha);
            }
        }
    }

    /// <summary>Anti-aliased ring (circle outline) of the given stroke width.</summary>
    public void StrokeCircle(double cx, double cy, double radius, Rgb color, double width = 1, double alpha = 1)
    {
        if (!IsFinite(cx) || !IsFinite(cy) || radius <= 0) return;
        var hw = Math.Max(0.5, width / 2);
        var outer = radius + hw + 1;
        var x0 = Math.Max(0, (int)Math.Floor(cx - outer));
        var x1 = Math.Min(Width - 1, (int)Math.Ceiling(cx + outer));
        var y0 = Math.Max(0, (int)Math.Floor(cy - outer));
        var y1 = Math.Min(Height - 1, (int)Math.Ceiling(cy + outer));
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                var coverage = hw + 0.5 - Math.Abs(d - radius);
                if (coverage <= 0) continue;
                Blend(x, y, color, Math.Min(1, coverage) * alpha);
            }
        }
    }

    /// <summary>Size in pixels of <paramref name="text"/> at an integer <paramref name="scale"/>.</summary>
    public static (int Width, int Height) MeasureText(string text, int scale = 1)
    {
        scale = Math.Max(1, scale);
        return ((text ?? "").Length * BitmapFont.GlyphSize * scale, BitmapFont.GlyphSize * scale);
    }

    /// <summary>Draw <paramref name="text"/> with its top-left corner at (x, y).</summary>
    public void DrawText(int x, int y, string text, Rgb color, int scale = 1, double alpha = 1)
    {
        if (string.IsNullOrEmpty(text)) return;
        scale = Math.Max(1, scale);
        var penX = x;
        foreach (var c in text)
        {
            var glyph = BitmapFont.Glyph(c);
            for (var row = 0; row < BitmapFont.GlyphSize; row++)
            {
                var bits = glyph[row];
                if (bits == 0) continue;
                for (var col = 0; col < BitmapFont.GlyphSize; col++)
                {
                    if ((bits & (1 << col)) == 0) continue;
                    for (var sy = 0; sy < scale; sy++)
                        for (var sx = 0; sx < scale; sx++)
                            Blend(penX + col * scale + sx, y + row * scale + sy, color, alpha);
                }
            }
            penX += BitmapFont.GlyphSize * scale;
        }
    }

    /// <summary>Liang–Barsky clip of a segment to a rectangle; false when fully outside.</summary>
    public static bool ClipSegment(
        ref double x0, ref double y0, ref double x1, ref double y1,
        double minX, double minY, double maxX, double maxY)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        double t0 = 0, t1 = 1;
        if (!ClipTest(-dx, x0 - minX, ref t0, ref t1)) return false;
        if (!ClipTest(dx, maxX - x0, ref t0, ref t1)) return false;
        if (!ClipTest(-dy, y0 - minY, ref t0, ref t1)) return false;
        if (!ClipTest(dy, maxY - y0, ref t0, ref t1)) return false;
        var nx0 = x0 + t0 * dx;
        var ny0 = y0 + t0 * dy;
        var nx1 = x0 + t1 * dx;
        var ny1 = y0 + t1 * dy;
        x0 = nx0;
        y0 = ny0;
        x1 = nx1;
        y1 = ny1;
        return true;
    }

    private static bool ClipTest(double p, double q, ref double t0, ref double t1)
    {
        if (Math.Abs(p) < 1e-12) return q >= 0;
        var r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    private static double DistanceToSegment(
        double px, double py, double x0, double y0, double dx, double dy, double len2)
    {
        var t = len2 > 1e-12 ? Clamp01(((px - x0) * dx + (py - y0) * dy) / len2) : 0;
        var nx = x0 + t * dx - px;
        var ny = y0 + t * dy - py;
        return Math.Sqrt(nx * nx + ny * ny);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
