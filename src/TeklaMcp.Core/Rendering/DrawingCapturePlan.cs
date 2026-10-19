using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Rendering;

/// <summary>
/// Backend-independent half of <c>tekla_capture_drawing</c>: which window is the drawing editor,
/// which part of the capture to keep for a sheet / view / region, and how a pixel of the returned
/// picture maps back to sheet paper millimetres. All sheet coordinates are paper mm with the
/// origin at the sheet's lower-left corner (Y up); pixels have the origin top-left (y down).
/// </summary>
public static class DrawingCapturePlan
{
    /// <summary>Paper mm kept around the sheet for scope=sheet, so its edge line stays on the picture.</summary>
    public const double SheetMarginMm = 2;

    /// <summary>A crop of the capture, in capture pixels (right/bottom exclusive).</summary>
    public sealed class Crop
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        /// <summary>True when part of the requested rectangle (without its margin) is off the picture.</summary>
        public bool Clipped;

        /// <summary>Sheet area covered by the crop, paper mm.</summary>
        public Point3D ShownMin = new Point3D();
        public Point3D ShownMax = new Point3D();

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// The view frame on the sheet, paper mm: <see cref="DrawingViewInfo.FrameMin"/>/<see cref="DrawingViewInfo.FrameMax"/>
    /// when the backend read them (<c>View.GetAxisAlignedBoundingBox()</c>), else Origin + FrameOrigin
    /// and Width × Height — the same rectangle on Tekla 2021 (two views, 2026-10-16).
    /// </summary>
    public static bool TryViewFrame(DrawingViewInfo view, out Point3D min, out Point3D max)
    {
        if (view.FrameMin != null && view.FrameMax != null && view.FrameMax.X > view.FrameMin.X && view.FrameMax.Y > view.FrameMin.Y)
        {
            min = new Point3D(view.FrameMin.X, view.FrameMin.Y, 0);
            max = new Point3D(view.FrameMax.X, view.FrameMax.Y, 0);
            return true;
        }
        if (view.Origin != null && view.Width > 0 && view.Height > 0)
        {
            var offset = view.FrameOrigin ?? new Point3D();
            min = new Point3D(view.Origin.X + offset.X, view.Origin.Y + offset.Y, 0);
            max = new Point3D(min.X + view.Width, min.Y + view.Height, 0);
            return true;
        }
        min = new Point3D();
        max = new Point3D();
        return false;
    }

    /// <summary>Parse a sheet region 'minX,minY,maxX,maxY' (paper mm, any corner order).</summary>
    public static bool TryParseRegion(string? text, out Point3D min, out Point3D max, out string error)
    {
        min = new Point3D();
        max = new Point3D();
        error = "";
        var parts = (text ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var values = new List<double>();
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                double.IsNaN(value) || double.IsInfinity(value))
            {
                error = "Region value '" + part + "' is not a number.";
                return false;
            }
            values.Add(value);
        }
        if (values.Count != 4)
        {
            error = "region needs 4 numbers 'minX,minY,maxX,maxY' in sheet paper mm, got " + values.Count + ".";
            return false;
        }
        min = new Point3D(Math.Min(values[0], values[2]), Math.Min(values[1], values[3]), 0);
        max = new Point3D(Math.Max(values[0], values[2]), Math.Max(values[1], values[3]), 0);
        if (max.X - min.X < 1 || max.Y - min.Y < 1)
        {
            error = "region must be at least 1 x 1 mm.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The capture pixels showing sheet rectangle <paramref name="min"/>..<paramref name="max"/>
    /// grown by <paramref name="marginMm"/>, clipped to the <paramref name="pictureWidth"/>×<paramref name="pictureHeight"/>
    /// capture. Null when none of it is on the picture.
    /// </summary>
    public static Crop? ToCrop(
        SheetPixelMapping mapping, double sheetHeightMm, int pictureWidth, int pictureHeight,
        Point3D min, Point3D max, double marginMm)
    {
        var s = mapping.PixelsPerMm;
        if (!mapping.Found || !(s > 0)) return null;
        double Px(double x) => mapping.SheetLeftPx + x * s;
        double Py(double y) => mapping.SheetTopPx + (sheetHeightMm - y) * s;

        var margin = Math.Max(0, marginMm);
        var left = (int)Math.Floor(Px(min.X - margin));
        var right = (int)Math.Ceiling(Px(max.X + margin)) + 1;
        var top = (int)Math.Floor(Py(max.Y + margin));
        var bottom = (int)Math.Ceiling(Py(min.Y - margin)) + 1;

        var crop = new Crop
        {
            Left = Math.Max(0, left),
            Top = Math.Max(0, top),
            Right = Math.Min(pictureWidth, right),
            Bottom = Math.Min(pictureHeight, bottom),
        };
        if (crop.Width < 2 || crop.Height < 2) return null;

        // Clipped = the rectangle itself (not its margin) runs off the picture, by more than a pixel.
        crop.Clipped = Px(min.X) < -1 || Px(max.X) > pictureWidth + 1 || Py(max.Y) < -1 || Py(min.Y) > pictureHeight + 1;
        crop.ShownMin = new Point3D(
            Math.Round((crop.Left - mapping.SheetLeftPx) / s, 2),
            Math.Round(sheetHeightMm - (crop.Bottom - mapping.SheetTopPx) / s, 2), 0);
        crop.ShownMax = new Point3D(
            Math.Round((crop.Right - mapping.SheetLeftPx) / s, 2),
            Math.Round(sheetHeightMm - (crop.Top - mapping.SheetTopPx) / s, 2), 0);
        return crop;
    }

    /// <summary>Pixel → sheet mm for the returned picture, which is <paramref name="crop"/> scaled to <paramref name="imageWidth"/>.</summary>
    public static DrawingImageMapping ImageMapping(SheetPixelMapping mapping, double sheetHeightMm, Crop crop, int imageWidth)
    {
        var imageScale = crop.Width > 0 && imageWidth > 0 ? (double)imageWidth / crop.Width : 1;
        return new DrawingImageMapping
        {
            MmPerPixel = Math.Round(1 / (mapping.PixelsPerMm * imageScale), 5),
            OriginX = Math.Round((crop.Left - mapping.SheetLeftPx) / mapping.PixelsPerMm, 3),
            OriginY = Math.Round(sheetHeightMm - (crop.Top - mapping.SheetTopPx) / mapping.PixelsPerMm, 3),
        };
    }

    /// <summary>Sheet rectangle on the returned picture, clipped to it; null when it is not on the picture.</summary>
    public static PixelBox? ToImage(DrawingImageMapping map, int imageWidth, int imageHeight, Point3D min, Point3D max)
    {
        if (!(map.MmPerPixel > 0)) return null;
        var left = (int)Math.Floor((min.X - map.OriginX) / map.MmPerPixel);
        var right = (int)Math.Ceiling((max.X - map.OriginX) / map.MmPerPixel);
        var top = (int)Math.Floor((map.OriginY - max.Y) / map.MmPerPixel);
        var bottom = (int)Math.Ceiling((map.OriginY - min.Y) / map.MmPerPixel);
        var box = new PixelBox
        {
            Left = Math.Max(0, left),
            Top = Math.Max(0, top),
            Right = Math.Min(imageWidth, right),
            Bottom = Math.Min(imageHeight, bottom),
        };
        return box.Right > box.Left && box.Bottom > box.Top ? box : null;
    }

    /// <summary>
    /// Which of Tekla's MDI windows is the drawing editor. Tekla 2021 titles it with the drawing
    /// type letter and mark ("G   [268]") next to model views titled "View 1 - 3D-view"; the
    /// mark is tried first, then elimination of the windows that are model views. Returns -1
    /// with <paramref name="error"/> when neither tells exactly one window apart.
    /// </summary>
    public static int PickEditorWindow(
        IReadOnlyList<string> windowTitles, string? drawingMark, Func<string, bool> isModelView, out string error)
    {
        error = "";
        var mark = (drawingMark ?? "").Trim();
        var all = Enumerable.Range(0, windowTitles.Count).ToList();
        if (mark.Length > 0)
        {
            var byMark = all.Where(i => (windowTitles[i] ?? "").IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (byMark.Count > 1) byMark = byMark.Where(i => !isModelView(windowTitles[i] ?? "")).ToList();
            if (byMark.Count == 1) return byMark[0];
        }

        var rest = all.Where(i => !isModelView(windowTitles[i] ?? "")).ToList();
        if (rest.Count == 1) return rest[0];

        var titles = windowTitles.Count == 0 ? "none" : string.Join(", ", windowTitles.Select(t => "'" + t + "'"));
        error = rest.Count == 0
            ? "The drawing editor's window was not found among Tekla's windows (" + titles + ")."
            : "Several Tekla windows could be the drawing editor (" + titles + "); none carries the drawing mark '" +
              mark + "' in its title.";
        return -1;
    }
}
