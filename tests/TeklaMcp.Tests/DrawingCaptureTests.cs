using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Models;
using TeklaMcp.Core.Rendering;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// <c>tekla_capture_drawing</c> (issue #31): locating the sheet on a capture of the drawing editor,
/// cropping to a view or region, mapping picture pixels back to sheet mm, telling the editor window
/// apart, and the mock stand-in. The geometry follows the live probe on Tekla 2021 (A2 sheet
/// 594×420, 2.867 px/mm, editor client area 2519×1204, sheet left edge at x=408).
/// </summary>
public class DrawingCaptureTests
{
    private const int Paper = 0xFEFEFB;
    private const int Blue = 0x0000FE;

    private static Point3D P(double x, double y) => new Point3D(x, y, 0);

    /// <summary>A blank editor picture with lines drawn on it.</summary>
    private sealed class Picture
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int[] Rgb;

        public Picture(int width, int height)
        {
            Width = width;
            Height = height;
            Rgb = Enumerable.Repeat(Paper, width * height).ToArray();
        }

        public void HLine(int y, double x0, double x1, int color = Blue)
        {
            if (y < 0 || y >= Height) return;
            for (var x = (int)Math.Max(0, Math.Round(x0)); x <= Math.Min(Width - 1, Math.Round(x1)); x++)
                Rgb[y * Width + x] = color;
        }

        public void VLine(int x, double y0, double y1, int color = Blue)
        {
            if (x < 0 || x >= Width) return;
            for (var y = (int)Math.Max(0, Math.Round(y0)); y <= Math.Min(Height - 1, Math.Round(y1)); y++)
                Rgb[y * Width + x] = color;
        }

        /// <summary>Rectangle outline; edges outside the picture are simply not drawn.</summary>
        public void Rect(double left, double top, double right, double bottom, int color = Blue)
        {
            HLine((int)Math.Round(top), left, right, color);
            HLine((int)Math.Round(bottom), left, right, color);
            VLine((int)Math.Round(left), top, bottom, color);
            VLine((int)Math.Round(right), top, bottom, color);
        }

        /// <summary>A sheet at <paramref name="scale"/> px/mm with its title-block frame (20/5/5/5 mm margins) and some content.</summary>
        public void Sheet(double left, double top, double scale, double width = 594, double height = 420)
        {
            Rect(left, top, left + width * scale, top + height * scale);
            Rect(left + 20 * scale, top + 5 * scale, left + (width - 5) * scale, top + (height - 5) * scale);
            // A table and a few long content lines that must not be taken for the sheet.
            Rect(left + 400 * scale, top + 10 * scale, left + 585 * scale, top + 40 * scale);
            HLine((int)(top + 200 * scale), left + 40 * scale, left + 350 * scale, 0x959595);
            VLine((int)(left + 150 * scale), top + 30 * scale, top + 300 * scale, 0x959595);
        }

        public SheetPixelMapping Locate(double width = 594, double height = 420) =>
            SheetLocator.Locate(Width, Height, Rgb, width, height);
    }

    [Fact]
    public void Whole_sheet_inside_the_window_is_found_by_four_edges()
    {
        var picture = new Picture(1000, 800);
        picture.Sheet(50, 80, 1.5);

        var mapping = picture.Locate();

        Assert.True(mapping.Found, mapping.Message);
        Assert.Equal("sheet-edges", mapping.Method);
        Assert.Equal(4, mapping.EdgesFound);
        Assert.Equal(1.5, mapping.PixelsPerMm, 2);
        Assert.Equal(50, mapping.SheetLeftPx, 0);
        Assert.Equal(80, mapping.SheetTopPx, 0);
    }

    [Fact]
    public void Freshly_opened_drawing_fills_the_height_and_the_bottom_edge_lies_on_the_border()
    {
        // The live layout: 2.867 px/mm, top edge on row 0, bottom edge just below the client area.
        const double scale = 2.867;
        var picture = new Picture(2519, 1204);
        picture.Sheet(408, 0, scale);

        var mapping = picture.Locate();

        Assert.True(mapping.Found, mapping.Message);
        Assert.Equal(3, mapping.EdgesFound);
        Assert.Equal(scale, mapping.PixelsPerMm, 2);
        Assert.Equal(408, mapping.SheetLeftPx, 0);
        Assert.Equal(0, mapping.SheetTopPx, 0);
        Assert.Contains("fourth", mapping.Message);
    }

    [Fact]
    public void Sheet_running_off_one_side_is_found_when_that_edge_is_on_the_border()
    {
        // Top, bottom and left edges visible, the right edge on the right border. Two sheet edges
        // plus the title-block frame's right and bottom lines also fit within 1 % — the corner
        // check must reject that mix.
        const double scale = 1.2;
        var picture = new Picture((int)Math.Round(100 + 594 * scale), 700);
        picture.Sheet(100, 60, scale);

        var mapping = picture.Locate();

        Assert.True(mapping.Found, mapping.Message);
        Assert.Equal(3, mapping.EdgesFound);
        Assert.Equal(100, mapping.SheetLeftPx, 0);
        Assert.Equal(60, mapping.SheetTopPx, 0);
    }

    [Fact]
    public void Zoomed_in_past_the_sheet_edges_is_not_guessed_from_the_title_block_frame()
    {
        // Sheet edges just outside the window, the 20/5 mm title-block frame inside it, bottom off
        // screen: three lines of the frame would fit at the wrong scale (569 mm read as 594 mm).
        const double scale = 3;
        var picture = new Picture(1740, 1000);
        picture.Sheet(-30, -2 * scale - 1, scale);

        var mapping = picture.Locate();

        Assert.False(mapping.Found);
        Assert.Contains("zoom out", mapping.Message);
    }

    [Fact]
    public void Title_block_cells_seen_up_close_are_not_taken_for_the_sheet()
    {
        // Live Tekla 2021: zoomed in on the title block, cells of 141×100 px matched A2's aspect
        // ratio (1.410 vs 1.414). The table's lines run on past every cell corner; sheet edges stop.
        var picture = new Picture(1200, 700);
        for (var x = 30; x < 1200; x += 141) picture.VLine(x, 0, 699);
        for (var y = 40; y < 700; y += 100) picture.HLine(y, 0, 1199);

        var mapping = picture.Locate();

        Assert.False(mapping.Found);
    }

    [Fact]
    public void A_sheet_next_to_a_view_placed_off_the_sheet_is_still_found()
    {
        // Views may sit outside the paper (live drawing [268] had two at negative X); their lines
        // do not touch the sheet's corners.
        var picture = new Picture(1400, 800);
        picture.Sheet(500, 60, 1.4);
        picture.Rect(40, 100, 400, 500, 0x00FFFF);

        var mapping = picture.Locate();

        Assert.True(mapping.Found, mapping.Message);
        Assert.Equal(500, mapping.SheetLeftPx, 0);
        Assert.Equal(1.4, mapping.PixelsPerMm, 2);
    }

    [Fact]
    public void Blank_or_tiny_pictures_and_unknown_sheet_sizes_are_not_found()
    {
        Assert.False(new Picture(800, 600).Locate().Found);
        Assert.False(new Picture(40, 40).Locate().Found);
        var picture = new Picture(1000, 800);
        picture.Sheet(50, 80, 1.5);
        Assert.False(picture.Locate(0, 420).Found);
    }

    [Fact]
    public void The_outermost_rectangle_with_the_sheet_proportions_wins()
    {
        var picture = new Picture(1200, 900);
        picture.Sheet(60, 50, 1.8);
        // A table drawn with exactly the sheet's proportions, fully inside the sheet.
        picture.Rect(300, 300, 300 + 297, 300 + 210);

        var mapping = picture.Locate();

        Assert.True(mapping.Found);
        Assert.Equal(1.8, mapping.PixelsPerMm, 2);
        Assert.Equal(60, mapping.SheetLeftPx, 0);
    }

    [Fact]
    public void A_sheet_edge_drawn_two_pixels_thick_is_one_line()
    {
        var picture = new Picture(1000, 800);
        picture.Sheet(50, 80, 1.5);
        picture.Rect(51, 81, 50 + 594 * 1.5 - 1, 80 + 420 * 1.5 - 1);

        var mapping = picture.Locate();

        Assert.True(mapping.Found);
        Assert.Equal(1.5, mapping.PixelsPerMm, 1);
        Assert.InRange(mapping.SheetLeftPx, 49.5, 51.5);
    }

    [Fact]
    public void View_frame_comes_from_the_bounding_box_or_from_origin_plus_frame_origin()
    {
        // Live Tekla 2021 view: Origin + FrameOrigin = GetAxisAlignedBoundingBox().LowerLeft.
        var live = new DrawingViewInfo
        {
            Origin = P(-20.999, 242.124),
            FrameOrigin = P(53.55, -37.911),
            Width = 281.083,
            Height = 205.786,
        };
        Assert.True(DrawingCapturePlan.TryViewFrame(live, out var min, out var max));
        Assert.Equal(32.55, min.X, 2);
        Assert.Equal(204.21, min.Y, 2);
        Assert.Equal(313.63, max.X, 2);
        Assert.Equal(410.0, max.Y, 2);

        live.FrameMin = P(1, 2);
        live.FrameMax = P(3, 4);
        Assert.True(DrawingCapturePlan.TryViewFrame(live, out min, out max));
        Assert.Equal(1, min.X);
        Assert.Equal(4, max.Y);

        Assert.False(DrawingCapturePlan.TryViewFrame(new DrawingViewInfo(), out _, out _));
    }

    [Theory]
    [InlineData("10,20,110,90", true, 10, 20, 110, 90)]
    [InlineData("110 90; 10 20", true, 10, 20, 110, 90)]
    [InlineData("1,2,3", false, 0, 0, 0, 0)]
    [InlineData("1,2,x,4", false, 0, 0, 0, 0)]
    [InlineData("5,5,5.5,40", false, 0, 0, 0, 0)]
    [InlineData("", false, 0, 0, 0, 0)]
    public void Sheet_regions_parse_four_paper_mm_numbers(string text, bool ok, double x0, double y0, double x1, double y1)
    {
        Assert.Equal(ok, DrawingCapturePlan.TryParseRegion(text, out var min, out var max, out var error));
        if (!ok)
        {
            Assert.NotEmpty(error);
            return;
        }
        Assert.Equal((x0, y0, x1, y1), (min.X, min.Y, max.X, max.Y));
    }

    private static SheetPixelMapping LiveMapping() => new SheetPixelMapping
    {
        Found = true,
        Method = "sheet-edges",
        PixelsPerMm = 2.867,
        SheetLeftPx = 408,
        SheetTopPx = 0,
        EdgesFound = 3,
    };

    [Fact]
    public void View_crop_on_the_live_layout_frames_the_view_with_its_margin()
    {
        var crop = DrawingCapturePlan.ToCrop(LiveMapping(), 420, 2519, 1204, P(32.55, 204.21), P(313.63, 410), 5);

        Assert.NotNull(crop);
        Assert.False(crop!.Clipped);
        Assert.Equal(486, crop.Left);
        Assert.Equal(14, crop.Top);
        Assert.Equal(1323, crop.Right);
        Assert.Equal(635, crop.Bottom);
        Assert.Equal(27.2, crop.ShownMin.X, 1);
        Assert.Equal(415.1, crop.ShownMax.Y, 1);
    }

    [Fact]
    public void A_whole_sheet_with_its_bottom_on_the_border_is_not_reported_as_clipped()
    {
        var crop = DrawingCapturePlan.ToCrop(LiveMapping(), 420, 2519, 1204, P(0, 0), P(594, 420),
            DrawingCapturePlan.SheetMarginMm);

        Assert.NotNull(crop);
        Assert.False(crop!.Clipped);
        Assert.Equal(0, crop.Top);
        Assert.Equal(1204, crop.Bottom);
    }

    [Fact]
    public void Regions_partly_off_screen_are_clipped_and_fully_off_screen_ones_are_refused()
    {
        var mapping = LiveMapping();
        var partly = DrawingCapturePlan.ToCrop(mapping, 420, 2519, 1204, P(-200, 100), P(50, 200), 0);
        Assert.NotNull(partly);
        Assert.True(partly!.Clipped);
        Assert.Equal(0, partly.Left);

        Assert.Null(DrawingCapturePlan.ToCrop(mapping, 420, 2519, 1204, P(-400, 100), P(-200, 200), 0));
        Assert.Null(DrawingCapturePlan.ToCrop(new SheetPixelMapping(), 420, 2519, 1204, P(0, 0), P(10, 10), 0));
    }

    [Fact]
    public void Picture_pixels_map_back_to_sheet_mm_after_downscaling()
    {
        var mapping = LiveMapping();
        var crop = DrawingCapturePlan.ToCrop(mapping, 420, 2519, 1204, P(0, 0), P(594, 420), 0)!;
        var imageWidth = crop.Width / 2;
        var map = DrawingCapturePlan.ImageMapping(mapping, 420, crop, imageWidth);

        Assert.Equal(2 / 2.867, map.MmPerPixel, 3);
        // The view frame's lower-left corner, sent to the picture and read back.
        var box = DrawingCapturePlan.ToImage(map, imageWidth, crop.Height / 2, P(32.55, 204.21), P(313.63, 410));
        Assert.NotNull(box);
        var pixel = map.MmPerPixel;
        Assert.InRange(map.OriginX + box!.Left * pixel, 32.55 - pixel, 32.55);
        Assert.InRange(map.OriginY - box.Bottom * pixel, 204.21 - pixel, 204.21);
        Assert.InRange(map.OriginY - box.Top * pixel, 410, 410 + pixel);

        Assert.Null(DrawingCapturePlan.ToImage(map, imageWidth, crop.Height / 2, P(700, 0), P(800, 10)));
    }

    [Fact]
    public void Editor_window_is_picked_by_mark_then_by_elimination_of_model_views()
    {
        var titles = new List<string> { "G   [268]", "View 1 - 3D-view" };
        bool IsModelView(string title) => title.StartsWith("View ", StringComparison.Ordinal);

        Assert.Equal(0, DrawingCapturePlan.PickEditorWindow(titles, "[268]", IsModelView, out _));
        // No mark (or a title without it): the one window that is not a model view.
        Assert.Equal(0, DrawingCapturePlan.PickEditorWindow(titles, "", IsModelView, out _));
        Assert.Equal(0, DrawingCapturePlan.PickEditorWindow(titles, "[999]", IsModelView, out _));

        var ambiguous = new List<string> { "Drawing A", "Drawing B", "View 1 - 3D-view" };
        Assert.Equal(-1, DrawingCapturePlan.PickEditorWindow(ambiguous, "[1]", IsModelView, out var error));
        Assert.Contains("Several", error);
        Assert.Equal(-1, DrawingCapturePlan.PickEditorWindow(new List<string> { "View 1 - 3D-view" }, "[1]", IsModelView, out error));
        Assert.Contains("not found", error);
    }

    [Fact]
    public void Mock_sheet_capture_is_a_labelled_stand_in_with_view_boxes()
    {
        var result = new MockTeklaModelService().CaptureDrawing(new DrawingCaptureRequest { Format = "jpeg" });

        Assert.True(result.Captured, result.Message);
        Assert.Equal("mock-schematic", result.Source);
        Assert.Equal("sheet", result.Scope);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(result.Width, PngTestReader.Decode(result.Image!.Bytes).Width);
        Assert.True(result.Width <= 1600 && result.Height <= 1600);
        Assert.Contains(result.Warnings, w => w.Contains("not a capture"));
        Assert.Contains(result.Warnings, w => w.Contains("PNG"));
        Assert.NotNull(result.PixelToSheet);
        Assert.All(result.Views, v => Assert.True(v.OnPicture));
        var front = result.Views.Single(v => v.ViewId == 11001);
        Assert.Equal(40, front.FrameMin!.X, 6);
        Assert.Equal(200, front.FrameMax!.Y, 6);
    }

    [Fact]
    public void Mock_view_capture_frames_that_view_and_unknown_views_are_listed()
    {
        var mock = new MockTeklaModelService();

        var view = mock.CaptureDrawing(new DrawingCaptureRequest { ViewId = 11002, MarginMm = 5 });
        Assert.True(view.Captured, view.Message);
        Assert.Equal("view", view.Scope);
        Assert.Equal(11002, view.View!.ViewId);
        Assert.Equal(235, view.ShownMin!.X, 0);
        Assert.Equal(405, view.ShownMax!.X, 0);
        var box = view.Views.Single(v => v.ViewId == 11002).Pixels!;
        Assert.InRange(box.Left, 0, 30);
        Assert.InRange(view.Width - box.Right, 0, 30);

        var unknown = mock.CaptureDrawing(new DrawingCaptureRequest { ViewId = 4242 });
        Assert.False(unknown.Captured);
        Assert.Null(unknown.Image);
        Assert.Contains("11001", unknown.Message);
    }

    [Fact]
    public void Mock_capture_without_an_open_drawing_says_so()
    {
        var mock = new MockTeklaModelService();
        mock.CloseActiveDrawing(save: false, apply: true);

        var result = mock.CaptureDrawing(new DrawingCaptureRequest());

        Assert.False(result.Captured);
        Assert.Contains("No drawing is open", result.Message);
    }
}
