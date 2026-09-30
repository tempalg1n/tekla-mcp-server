using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>What <c>tekla_capture_view</c> should capture and what it may do to the view first.</summary>
public sealed class ViewCaptureRequest
{
    /// <summary>Tekla view name (e.g. "3D"). Empty = the active view.</summary>
    public string? ViewName { get; set; }

    /// <summary>Objects to zoom to / highlight / number.</summary>
    public List<string> Guids { get; set; } = new List<string>();

    /// <summary>Use the current Tekla selection as the targets.</summary>
    public bool UseSelection { get; set; }

    /// <summary>Zoom the view to the targets (when there are targets).</summary>
    public bool Zoom { get; set; } = true;

    /// <summary>Colour the targets (temporary representation).</summary>
    public bool Highlight { get; set; } = true;

    /// <summary>With <see cref="Highlight"/>: make every other object semi-transparent.</summary>
    public bool GhostOthers { get; set; } = true;

    /// <summary>Rotate the camera: the same presets / "dx,dy,dz" convention as the schematic view.</summary>
    public string? Direction { get; set; }

    /// <summary>Draw numbers at the targets as temporary graphics (active view only).</summary>
    public bool Labels { get; set; }

    /// <summary>Put camera and colours back after the capture (default).</summary>
    public bool Restore { get; set; } = true;

    public int MaxWidth { get; set; } = 1280;
    public int MaxHeight { get; set; } = 1024;

    /// <summary>jpeg (default, small) | png.</summary>
    public string Format { get; set; } = "jpeg";

    public int MaxTargets { get; set; } = 500;

    /// <summary>Longest wait for Tekla to redraw after a camera/colour change.</summary>
    public int SettleMilliseconds { get; set; } = 2500;
}

/// <summary>Camera of a Tekla model view as read by <c>ViewCamera.Select()</c>.</summary>
public sealed class ViewCameraInfo
{
    public Point3D Location { get; set; } = new Point3D();

    /// <summary>Direction the camera looks along (global).</summary>
    public Point3D Direction { get; set; } = new Point3D();
    public Point3D Up { get; set; } = new Point3D();

    /// <summary>
    /// Tekla's orthogonal zoom factor. The API documents meters per pixel; live values
    /// (Tekla 2023) look like millimetres per pixel — treat it as a relative measure.
    /// </summary>
    public double ZoomFactor { get; set; }
    public double FieldOfView { get; set; }
    public bool Perspective { get; set; }
}

/// <summary>One numbered/highlighted target on a capture.</summary>
public sealed class CaptureLegendEntry
{
    public string Label { get; set; } = "";
    public string Guid { get; set; } = "";
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Profile { get; set; } = "";
}

/// <summary>Result of <c>tekla_capture_view</c>.</summary>
public sealed class ViewCaptureResult
{
    public bool Captured { get; set; }
    public string Backend { get; set; } = "";

    /// <summary>"tekla-viewport" (pixels rendered by Tekla) or "mock-schematic" (mock stand-in).</summary>
    public string Source { get; set; } = "";

    public string? ViewName { get; set; }
    public string? ActiveViewName { get; set; }

    /// <summary>Model views currently open in Tekla.</summary>
    public List<string> OpenViews { get; set; } = new List<string>();

    public int Width { get; set; }
    public int Height { get; set; }
    public string MimeType { get; set; } = "";

    /// <summary>Camera at capture time (after any zoom/rotation, before restore).</summary>
    public ViewCameraInfo? Camera { get; set; }

    public int TargetsRequested { get; set; }
    public int TargetsFound { get; set; }
    public List<string> MissingGuids { get; set; } = new List<string>();

    public bool CameraChanged { get; set; }
    public bool CameraRestored { get; set; }
    public bool HighlightApplied { get; set; }
    public bool HighlightCleared { get; set; }
    public bool LabelsDrawn { get; set; }
    public bool LabelsCleared { get; set; }

    /// <summary>True when the view stopped changing before the settle budget ran out.</summary>
    public bool Settled { get; set; }
    public long CaptureMilliseconds { get; set; }

    public List<CaptureLegendEntry> Legend { get; set; } = new List<CaptureLegendEntry>();
    public string? Message { get; set; }
    public List<string> Warnings { get; set; } = new List<string>();

    /// <summary>The picture. Tools send it as an MCP image block and null it before serializing.</summary>
    public RenderedImage? Image { get; set; }
}
