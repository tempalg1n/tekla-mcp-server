using System.Collections.Generic;
using System.ComponentModel;

namespace TeklaMcp.Core.Models;

/// <summary>Declarative drawing to create.</summary>
public sealed class DrawingSpec
{
    /// <summary>assembly, single_part, cast_unit or ga.</summary>
    [Description("assembly, single_part, cast_unit, or ga.")]
    public string Type { get; set; } = "";
    [Description("Source model object GUID; empty only for a GA drawing.")]
    public string ModelGuid { get; set; } = "";
    [Description("Saved drawing attributes filename; empty uses defaults.")]
    public string AttributeFile { get; set; } = "";
    [Description("Optional Tekla sheet number.")]
    public int? SheetNumber { get; set; }
    [Description("Optional drawing name.")]
    public string Name { get; set; } = "";
}

/// <summary>Optional drawing-list fields to change; null means keep the existing value.</summary>
public sealed class DrawingModification
{
    public string? Name { get; set; }
    public string? Title1 { get; set; }
    public string? Title2 { get; set; }
    public string? Title3 { get; set; }
    public bool? IsFrozen { get; set; }
    public bool? IsLocked { get; set; }
    public bool? IsMasterDrawing { get; set; }
    public bool? IsReadyForIssue { get; set; }
}

/// <summary>PDF/printer settings for a drawing print operation.</summary>
public sealed class DrawingPrintOptions
{
    public string OutputType { get; set; } = "PDF";
    public string OutputFile { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string ColorMode { get; set; } = "Color";
    public string Orientation { get; set; } = "Auto";
    public string PaperSize { get; set; } = "Auto";
    public string ScalingMethod { get; set; } = "Auto";
    public double ScaleFactor { get; set; } = 1.0;
    public int NumberOfCopies { get; set; } = 1;
    public bool OpenFileWhenFinished { get; set; }
    public bool Overwrite { get; set; }
}

/// <summary>Orthogonal/3D view to create on the currently active drawing.</summary>
public sealed class DrawingViewSpec
{
    /// <summary>front, top, back, bottom, 3d, ga_model, section, curved_section or detail.</summary>
    public string Type { get; set; } = "front";
    public Point3D InsertionPoint { get; set; } = new Point3D();
    /// <summary>Required for ga_model: global model coordinate system of the restriction box.</summary>
    public CoordinateSystemInfo? ViewCoordinateSystem { get; set; }
    /// <summary>Required for ga_model: global model coordinate system of the display plane.</summary>
    public CoordinateSystemInfo? DisplayCoordinateSystem { get; set; }
    /// <summary>Required for ga_model: minimum restriction-box point in ViewCoordinateSystem.</summary>
    public Point3D? RestrictionMin { get; set; }
    /// <summary>Required for ga_model: maximum restriction-box point in ViewCoordinateSystem.</summary>
    public Point3D? RestrictionMax { get; set; }
    public int? SourceViewId { get; set; }
    public int? SourceViewId2 { get; set; }
    public int SourceViewIndex { get; set; } = -1;
    public List<Point3D> Points { get; set; } = new List<Point3D>();
    public double? DepthUp { get; set; }
    public double? DepthDown { get; set; }
    public string AttributeFile { get; set; } = "";
    public string MarkAttributeFile { get; set; } = "";
    public string Name { get; set; } = "";
    public double? Scale { get; set; }
}

/// <summary>Editable properties of one active-drawing view.</summary>
public sealed class DrawingViewModification
{
    public int ViewIndex { get; set; }
    public int? ViewId { get; set; }
    public int? ViewId2 { get; set; }
    public string? Name { get; set; }
    public Point3D? Origin { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? Scale { get; set; }
    public double? RotateXDegrees { get; set; }
    public double? RotateYDegrees { get; set; }
    public double? RotateZDegrees { get; set; }
    public double? RotateOnDrawingPlaneDegrees { get; set; }
    public bool Delete { get; set; }
}

/// <summary>
/// Generic annotation/graphic primitive for the active drawing. Coordinates are in the
/// selected view's coordinate system; paper distances use drawing paper millimetres.
/// </summary>
public sealed class DrawingObjectSpec
{
    /// <summary>text, line, rectangle, circle, arc, polyline, polygon, cloud, dimension or mark.</summary>
    [Description("Object kind: text, line, rectangle, circle, arc, polyline, polygon, cloud, " +
                 "straight_dimension, angle_dimension, radius_dimension, curved_radial, " +
                 "curved_orthogonal, mark, level_mark, or symbol.")]
    public string Kind { get; set; } = "";
    [Description("Ephemeral target view index; use -1 only for the drawing sheet.")]
    public int ViewIndex { get; set; }
    [Description("Preferred non-zero target view session ID.")]
    public int? ViewId { get; set; }
    [Description("Target view session ID2.")]
    public int? ViewId2 { get; set; }
    /// <summary>
    /// view (default: local to the target view), model (global model coordinates transformed
    /// through DisplayCoordinateSystem), or sheet (requires the sheet target).
    /// </summary>
    [Description("view, model, or sheet. sheet requires ViewIndex=-1.")]
    public string CoordinateSpace { get; set; } = "view";
    [Description("Defining points in CoordinateSpace.")]
    public List<Point3D> Points { get; set; } = new List<Point3D>();
    [Description("Text contents for a text object.")]
    public string Text { get; set; } = "";
    [Description("Represented model GUID for a mark.")]
    public string ModelGuid { get; set; } = "";
    [Description("Symbol library filename.")]
    public string SymbolFile { get; set; } = "";
    [Description("Symbol index, normally 0..255.")]
    public int? SymbolIndex { get; set; }
    [Description("Kind-specific subtype such as a level-mark leader type.")]
    public string SubType { get; set; } = "";
    [Description("Saved object attributes filename.")]
    public string AttributeFile { get; set; } = "";
    [Description("Radius for a circle/arc when applicable.")]
    public double? Radius { get; set; }
    [Description("Polyline/polygon/cloud bulge when applicable.")]
    public double? Bulge { get; set; }
    [Description("Object width when applicable.")]
    public double? Width { get; set; }
    [Description("Object height when applicable.")]
    public double? Height { get; set; }
    [Description("Rotation angle in degrees when applicable.")]
    public double? AngleDegrees { get; set; }
    [Description("Dimension up direction in view coordinates.")]
    public Point3D? UpDirection { get; set; }
    [Description("Dimension line distance in paper millimetres.")]
    public double? Distance { get; set; }
}

/// <summary>Batch edit/action over active-drawing objects selected by a query.</summary>
public sealed class DrawingObjectModification
{
    public string? Text { get; set; }
    public Point3D? MoveBy { get; set; }
    /// <summary>drawing, view, show_drawing, show_view, or null to keep.</summary>
    public string? Visibility { get; set; }
    public string? AttributeFile { get; set; }
    public bool Delete { get; set; }
}

/// <summary>Preview/apply result for all drawing-side mutations.</summary>
public sealed class DrawingWriteResult
{
    public string Operation { get; set; } = "";
    public bool Applied { get; set; }
    public int PlannedCount { get; set; }
    public int CreatedCount { get; set; }
    public int ModifiedCount { get; set; }
    public int DeletedCount { get; set; }
    public List<DrawingInfo> DrawingPreview { get; set; } = new List<DrawingInfo>();
    public List<DrawingViewInfo> ViewPreview { get; set; } = new List<DrawingViewInfo>();
    public List<DrawingObjectInfo> ObjectPreview { get; set; } = new List<DrawingObjectInfo>();
    public List<string> OutputFiles { get; set; } = new List<string>();
    public List<string> Errors { get; set; } = new List<string>();
    /// <summary>Non-fatal warnings, including possible partial changes after a failed commit.</summary>
    public List<string> Warnings { get; set; } = new List<string>();
    public string Backend { get; set; } = "";
    public string? Message { get; set; }
}
