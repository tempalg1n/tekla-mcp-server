using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Rendering;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// The hand-rolled picture stack in Core (no drawing library on any TFM): PNG files must decode
/// with an independent reader, drawing must land where it is asked to, and the bitmap font must
/// carry the Cyrillic capitals used as Russian grid labels.
/// </summary>
public class RasterTests
{
    private static readonly Rgb White = Rgb.FromHex(0xFFFFFF);
    private static readonly Rgb Red = Rgb.FromHex(0xFF0000);

    [Fact]
    public void Png_round_trips_through_an_independent_decoder()
    {
        // A busy image so every row filter gets a chance to win.
        var canvas = new RasterCanvas(97, 61, White);
        var random = new Random(42);
        for (var i = 0; i < canvas.Pixels.Length; i++)
            canvas.Pixels[i] = (byte)((i * 7 + i / 291 * 13) % 256 ^ (random.Next(4) == 0 ? random.Next(256) : 0));
        canvas.DrawLine(0, 0, 96, 60, Red, 3);

        var png = PngEncoder.Encode(canvas);
        var image = PngTestReader.Decode(png);

        Assert.Equal(97, image.Width);
        Assert.Equal(61, image.Height);
        Assert.Equal(canvas.Pixels, image.Rgb);
        Assert.True(PngTestReader.FilterTypes(png).Count > 1, "adaptive filtering should use more than one filter type");
    }

    [Fact]
    public void Flat_image_compresses_well()
    {
        var png = PngEncoder.Encode(new RasterCanvas(1280, 960, White));
        Assert.True(png.Length < 20_000, "a blank 1280x960 picture should be a few KB, got " + png.Length);
    }

    [Fact]
    public void Thick_line_covers_its_centre_and_leaves_the_background()
    {
        var canvas = new RasterCanvas(100, 50, White);
        canvas.DrawLine(10, 25, 90, 25, Red, 3);

        Assert.Equal(Red, canvas.GetPixel(50, 24));
        Assert.Equal(Red, canvas.GetPixel(50, 25));
        Assert.Equal(White, canvas.GetPixel(50, 10));
        Assert.Equal(White, canvas.GetPixel(95, 25));
        // Anti-aliased edge: neither background nor full colour.
        var edge = canvas.GetPixel(50, 26);
        Assert.True(edge.R == 255 && edge.G < 255 && edge.G > 0, "expected a partially covered edge pixel, got " + edge);
    }

    [Fact]
    public void Off_canvas_line_is_clipped_without_touching_pixels()
    {
        var canvas = new RasterCanvas(20, 20, White);
        canvas.DrawLine(-1e7, -5000, 1e7, -5000, Red, 5);
        Assert.All(Enumerable.Range(0, 20), x => Assert.Equal(White, canvas.GetPixel(x, 0)));
    }

    [Fact]
    public void Polygon_fill_stays_inside_the_outline()
    {
        var canvas = new RasterCanvas(40, 40, White);
        canvas.FillPolygon(new List<(double X, double Y)> { (10, 10), (30, 10), (30, 30), (10, 30) }, Red);

        Assert.Equal(Red, canvas.GetPixel(20, 20));
        Assert.Equal(Red, canvas.GetPixel(10, 10));
        Assert.Equal(White, canvas.GetPixel(30, 20));
        Assert.Equal(White, canvas.GetPixel(5, 5));
    }

    [Fact]
    public void Clip_segment_keeps_the_part_inside_the_rectangle()
    {
        double x0 = -10, y0 = 5, x1 = 30, y1 = 5;
        Assert.True(RasterCanvas.ClipSegment(ref x0, ref y0, ref x1, ref y1, 0, 0, 20, 10));
        Assert.Equal(0, x0, 6);
        Assert.Equal(20, x1, 6);

        double a0 = -10, b0 = -10, a1 = -5, b1 = -5;
        Assert.False(RasterCanvas.ClipSegment(ref a0, ref b0, ref a1, ref b1, 0, 0, 20, 10));
    }

    [Fact]
    public void Text_draws_glyphs_and_measures_cells()
    {
        var canvas = new RasterCanvas(64, 20, White);
        canvas.DrawText(0, 0, "A ", Red, 2);

        Assert.Contains(Enumerable.Range(0, 16).SelectMany(x => Enumerable.Range(0, 16), (x, y) => canvas.GetPixel(x, y)),
            p => p.Equals(Red));
        Assert.DoesNotContain(Enumerable.Range(16, 16).SelectMany(x => Enumerable.Range(0, 16), (x, y) => canvas.GetPixel(x, y)),
            p => p.Equals(Red));
        Assert.Equal((32, 16), RasterCanvas.MeasureText("AB", 2));
    }

    [Fact]
    public void Font_has_the_russian_grid_letters()
    {
        foreach (var c in "АБВГДЕЖИКЛМНПРСТУФШЭЮЯ")
            Assert.True(BitmapFont.Supports(c), "missing glyph for '" + c + "'");

        // Twins reuse the Latin shape, lower case reuses the capital, unknown falls back to '?'.
        Assert.Equal(BitmapFont.Glyph('A'), BitmapFont.Glyph('А'));
        Assert.Equal(BitmapFont.Glyph('Б'), BitmapFont.Glyph('б'));
        Assert.NotEqual(BitmapFont.Glyph('Б'), BitmapFont.Glyph('B'));
        Assert.Equal(BitmapFont.Glyph('?'), BitmapFont.Glyph('☃'));
        Assert.False(BitmapFont.Supports('☃'));
    }

    [Fact]
    public void Colour_parsing_accepts_hash_and_rejects_garbage()
    {
        Assert.True(Rgb.TryParse("#E00000", out var c));
        Assert.Equal("#E00000", c.ToHex());
        Assert.True(Rgb.TryParse("1f5fd6", out var d));
        Assert.Equal("#1F5FD6", d.ToHex());
        Assert.False(Rgb.TryParse("red", out _));
    }
}
