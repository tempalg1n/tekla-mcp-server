using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>What <c>tekla_capture_drawing</c> should show: the sheet, one view's frame or a sheet region.</summary>
public sealed class DrawingCaptureRequest
{
    /// <summary>Non-zero session View ID of the view to frame (from <c>tekla_list_drawing_views</c>).</summary>
    public int? ViewId { get; set; }

    /// <summary>Session View ID2; narrows <see cref="ViewId"/>.</summary>
    public int? ViewId2 { get; set; }

    /// <summary>Sheet region in paper mm (lower-left / upper-right). Ignored when a view is given.</summary>
    public Point3D? RegionMin { get; set; }
    public Point3D? RegionMax { get; set; }

    /// <summary>Paper mm added around a view frame or region.</summary>
    public double MarginMm { get; set; } = 5;

    public int MaxWidth { get; set; } = 1600;
    public int MaxHeight { get; set; } = 1600;

    /// <summary>png (default, crisp line work) | jpeg.</summary>
    public string Format { get; set; } = "png";

    /// <summary>Longest wait for two identical frames in a row (Tekla redraws asynchronously).</summary>
    public int SettleMilliseconds { get; set; } = 1500;
}

/// <summary>
/// Where the drawing sheet sits on a capture of Tekla's drawing editor, found from the sheet's
/// edges on the picture (the Open API cannot read the editor's zoom). Pixels are capture pixels:
/// the editor window's client area, before any downscaling.
/// </summary>
public sealed class SheetPixelMapping
{
    public bool Found { get; set; }

    /// <summary>"sheet-edges": the sheet rectangle was found on the picture.</summary>
    public string Method { get; set; } = "";

    /// <summary>Capture pixels per paper millimetre (the editor's zoom).</summary>
    public double PixelsPerMm { get; set; }

    /// <summary>Capture-pixel x of the sheet's left edge (sheet X = 0).</summary>
    public double SheetLeftPx { get; set; }

    /// <summary>Capture-pixel y of the sheet's top edge (sheet Y = sheet height).</summary>
    public double SheetTopPx { get; set; }

    /// <summary>How many of the four sheet edges were seen (a missing one lies on or past the window border).</summary>
    public int EdgesFound { get; set; }

    public string? Message { get; set; }
}

/// <summary>
/// Converts a pixel of the RETURNED image to sheet paper mm:
/// sheetX = OriginX + px * MmPerPixel, sheetY = OriginY - py * MmPerPixel.
/// </summary>
public sealed class DrawingImageMapping
{
    public double MmPerPixel { get; set; }
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public string Formula { get; set; } = "sheetX = originX + px * mmPerPixel; sheetY = originY - py * mmPerPixel";
}

/// <summary>A drawing view on the capture: its frame on the sheet and, when visible, on the picture.</summary>
public sealed class DrawingCaptureView
{
    public int ViewId { get; set; }
    public int ViewId2 { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public double? Scale { get; set; }

    /// <summary>View frame on the sheet, paper mm (null when Tekla did not report it).</summary>
    public Point3D? FrameMin { get; set; }
    public Point3D? FrameMax { get; set; }

    /// <summary>True when (part of) the frame is on the returned picture.</summary>
    public bool OnPicture { get; set; }

    /// <summary>Frame on the returned picture (top-left / bottom-right pixels), clipped to it.</summary>
    public PixelBox? Pixels { get; set; }
}

/// <summary>Pixel rectangle on the returned picture: left/top inclusive, right/bottom exclusive.</summary>
public sealed class PixelBox
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

/// <summary>Result of <c>tekla_capture_drawing</c>.</summary>
public sealed class DrawingCaptureResult
{
    public bool Captured { get; set; }
    public string Backend { get; set; } = "";

    /// <summary>"tekla-drawing-editor" (pixels drawn by Tekla) or "mock-schematic" (mock stand-in).</summary>
    public string Source { get; set; } = "";

    public string DrawingKey { get; set; } = "";
    public string DrawingMark { get; set; } = "";
    public string DrawingName { get; set; } = "";
    public string DrawingType { get; set; } = "";

    /// <summary>Title of the drawing editor window that was captured.</summary>
    public string? WindowTitle { get; set; }

    /// <summary>sheet | view | region | visible (the whole editor window: the sheet could not be located).</summary>
    public string Scope { get; set; } = "";

    public double? SheetWidth { get; set; }
    public double? SheetHeight { get; set; }

    public SheetPixelMapping? SheetMapping { get; set; }

    /// <summary>Sheet area on the picture, paper mm (null when the sheet was not located).</summary>
    public Point3D? ShownMin { get; set; }
    public Point3D? ShownMax { get; set; }

    /// <summary>True when part of the requested sheet/view/region is outside the editor window.</summary>
    public bool Clipped { get; set; }

    public DrawingImageMapping? PixelToSheet { get; set; }

    /// <summary>The requested view, when <c>viewId</c> was given.</summary>
    public DrawingCaptureView? View { get; set; }

    /// <summary>Every view on the sheet with its frame and where it is on the picture.</summary>
    public List<DrawingCaptureView> Views { get; set; } = new List<DrawingCaptureView>();

    public int Width { get; set; }
    public int Height { get; set; }
    public string MimeType { get; set; } = "";

    /// <summary>True when two consecutive frames were identical before the settle budget ran out.</summary>
    public bool Settled { get; set; }
    public long CaptureMilliseconds { get; set; }

    public string? Message { get; set; }
    public List<string> Warnings { get; set; } = new List<string>();

    /// <summary>The picture. Tools send it as an MCP image block and null it before serializing.</summary>
    public RenderedImage? Image { get; set; }
}
