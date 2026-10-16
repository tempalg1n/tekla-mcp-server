using System;
using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>Drawing-side connection and active-editor state.</summary>
public sealed class DrawingStatusInfo
{
    public bool Connected { get; set; }
    public bool AnyDrawingOpen { get; set; }
    public DrawingInfo? ActiveDrawing { get; set; }
    /// <summary>
    /// Writes this server committed to the open drawing since it was last saved or closed
    /// (issue #41). Edits made by hand in Tekla are not counted.
    /// </summary>
    public int UnsavedMcpWrites { get; set; }
    public string Backend { get; set; } = "";
    public string? Message { get; set; }
}

/// <summary>
/// Active drawing sheet geometry in paper millimetres plus its configured layout size.
/// Actual container dimensions and layout dimensions are kept separate because auto-sized
/// drawings may report different values.
/// </summary>
public sealed class DrawingSheetInfo
{
    public bool Available { get; set; }
    public string DrawingKey { get; set; } = "";
    public Point3D? Origin { get; set; }
    public Point3D? FrameOrigin { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string SizeDefinitionMode { get; set; } = "";
    public string AutoSizeOptions { get; set; } = "";
    public double? LayoutSheetWidth { get; set; }
    public double? LayoutSheetHeight { get; set; }
    public string Backend { get; set; } = "";
    public string? Message { get; set; }
}

/// <summary>
/// Serializable drawing-list row. <see cref="Key"/> is an opaque MCP address derived from the
/// full DrawingInternal identifier when available, with a public-property fallback.
/// </summary>
public sealed class DrawingInfo
{
    public string Key { get; set; } = "";
    public int DrawingId { get; set; }
    public int DrawingId2 { get; set; }
    public string DrawingGuid { get; set; } = "";
    public string Type { get; set; } = "";
    public string Mark { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title1 { get; set; } = "";
    public string Title2 { get; set; } = "";
    public string Title3 { get; set; } = "";
    public string AssociatedModelGuid { get; set; } = "";
    public int? SheetNumber { get; set; }
    public bool IsActive { get; set; }
    public bool IsFrozen { get; set; }
    public bool IsIssued { get; set; }
    public bool IsIssuedButModified { get; set; }
    public bool IsLocked { get; set; }
    public string IsLockedBy { get; set; } = "";
    public bool IsMasterDrawing { get; set; }
    public bool IsReadyForIssue { get; set; }
    public string IsReadyForIssueBy { get; set; } = "";
    public string UpToDateStatus { get; set; } = "";
    public DateTime? CreationDate { get; set; }
    public DateTime? ModificationDate { get; set; }
    public DateTime? IssuingDate { get; set; }
    public DateTime? OutputDate { get; set; }
    public string PlotFileName { get; set; } = "";
}

/// <summary>Filter for the Tekla drawing list.</summary>
public sealed class DrawingQuery
{
    public List<string> KeyIn { get; set; } = new List<string>();
    public string? Type { get; set; }
    public string? MarkContains { get; set; }
    public string? NameContains { get; set; }
    public string? TitleContains { get; set; }
    public string? AssociatedModelGuid { get; set; }
    public string? UpToDateStatusContains { get; set; }
    public bool? IsIssued { get; set; }
    public bool? IsLocked { get; set; }
    public bool? IsReadyForIssue { get; set; }
    public bool SelectedOnly { get; set; }
}

/// <summary>One view placed on the active drawing sheet.</summary>
public sealed class DrawingViewInfo
{
    /// <summary>Zero-based index in the current active drawing; re-list after structural edits.</summary>
    public int Index { get; set; }
    public int ViewId { get; set; }
    public int ViewId2 { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public Point3D? Origin { get; set; }
    public Point3D? FrameOrigin { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>
    /// The view frame on the sheet, paper mm (lower-left / upper-right). In a create preview it
    /// is an ESTIMATE from the restriction box and scale, without Tekla's frame margins.
    /// </summary>
    public Point3D? FrameMin { get; set; }
    public Point3D? FrameMax { get; set; }
    /// <summary>The view label's text (tags A1–A5, unformatted, " | " between tags); "" = empty label.</summary>
    public string? LabelText { get; set; }
    public double? Scale { get; set; }
    public Point3D? RestrictionMin { get; set; }
    public Point3D? RestrictionMax { get; set; }
    public CoordinateSystemInfo? ViewCoordinateSystem { get; set; }
    public CoordinateSystemInfo? DisplayCoordinateSystem { get; set; }
    public int? ModelObjectCount { get; set; }
}

/// <summary>Flat coordinate system: origin and unit/direction axes.</summary>
public sealed class CoordinateSystemInfo
{
    public Point3D Origin { get; set; } = new Point3D();
    public Point3D AxisX { get; set; } = new Point3D();
    public Point3D AxisY { get; set; } = new Point3D();
}

/// <summary>Full Tekla identifier; GUID can be empty while ID/ID2 remain valid.</summary>
public sealed class TeklaIdentifierInfo
{
    public int Id { get; set; }
    public int Id2 { get; set; }
    public string Guid { get; set; } = "";
    public string Key { get; set; } = "";
}

/// <summary>Bounded page of model identifiers represented by one drawing.</summary>
public sealed class DrawingModelObjectResult
{
    public List<TeklaIdentifierInfo> Items { get; set; } = new List<TeklaIdentifierInfo>();
    public int Offset { get; set; }
    public int ReturnedCount { get; set; }
    public int TotalCount { get; set; }
    public bool Truncated { get; set; }
    public string Backend { get; set; } = "";
    public string? Message { get; set; }
}

/// <summary>Flat, agent-friendly snapshot of an object in the active drawing.</summary>
public sealed class DrawingObjectInfo
{
    /// <summary>
    /// Zero-based index in the current active-drawing enumeration. It is intentionally
    /// ephemeral: re-list objects after insert/delete operations.
    /// </summary>
    public int Index { get; set; }
    public int ObjectId { get; set; }
    public int ObjectId2 { get; set; }
    public int ViewIndex { get; set; }
    public int ViewId { get; set; }
    public int ViewId2 { get; set; }
    public string ViewName { get; set; } = "";
    /// <summary>Coordinate space used by Points/Bounding*: view or sheet (paper millimetres).</summary>
    public string CoordinateSpace { get; set; } = "view";
    public string Type { get; set; } = "";
    public string ModelGuid { get; set; } = "";
    public int ModelId { get; set; }
    public int ModelId2 { get; set; }
    public string Text { get; set; } = "";
    public bool? IsHidden { get; set; }
    public List<Point3D> Points { get; set; } = new List<Point3D>();
    public double? Radius { get; set; }
    public double? Bulge { get; set; }
    public Point3D? BoundingMin { get; set; }
    public Point3D? BoundingMax { get; set; }
    public Dictionary<string, string> Udas { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Straight dimension sets (issues #26, #38): the distance of the dimension line as Tekla
    /// reports it — view units (model mm) at the view's CURRENT scale, so it doubles from 1:5 to
    /// 1:10 although the line keeps its place on paper. Do not "correct" it after a rescale.
    /// </summary>
    public double? DimensionDistance { get; set; }

    /// <summary>The same distance in paper mm (DimensionDistance / view scale) — the tools' unit.</summary>
    public double? DimensionDistancePaperMm { get; set; }

    /// <summary>Straight dimension sets: the direction from the points to the dimension line (view coordinates).</summary>
    public Point3D? UpDirection { get; set; }

    /// <summary>Straight dimension sets: Fixed (kept at the distance) or Free (Tekla may move it).</summary>
    public string? DimensionPlacing { get; set; }

    /// <summary>
    /// Parts and bolts with includeModelGeometry (issue #38): the model object's geometry in the
    /// view's DISPLAY coordinates (the coordinateSpace=view of the drawing tools). Null otherwise.
    /// </summary>
    public DrawingModelGeometry? ModelGeometry { get; set; }

    /// <summary>
    /// Marks only (issue #32): the content elements in order — kind "property" (Name = the
    /// property, e.g. ASSEMBLY_POS, Value = what it shows now), "text" (fixed text) or the element
    /// type. Null for other objects. <see cref="Text"/> holds the whole unformatted mark text.
    /// </summary>
    public List<MarkContentElement>? MarkContent { get; set; }

    /// <summary>Marks only: true when the mark shows nothing (blank unformatted text).</summary>
    public bool? IsEmptyMark { get; set; }

    /// <summary>
    /// Marks only: the drawing type of the model object the mark belongs to (Part, Bolt, …);
    /// its model GUID/ID are in <see cref="ModelGuid"/>/<see cref="ModelId"/>.
    /// </summary>
    public string? RelatedObjectType { get; set; }
}

/// <summary>A drawing part's or bolt's model geometry in view display coordinates (issue #38).</summary>
public sealed class DrawingModelGeometry
{
    public string Name { get; set; } = "";
    public string Profile { get; set; } = "";
    public string? AssemblyPos { get; set; }

    /// <summary>Box in view display coordinates: x/y on the view, z = depth (+z toward the viewer).</summary>
    public Point3D? Min { get; set; }
    public Point3D? Max { get; set; }

    /// <summary>Bolts: each bolt position in view display coordinates (filtered by any z range).</summary>
    public List<Point3D>? BoltPositions { get; set; }
    public string? BoltSize { get; set; }

    /// <summary>solid vertices / bolt positions — where the box comes from.</summary>
    public string Source { get; set; } = "";
    public string? Message { get; set; }
}

/// <summary>One element of a mark's content.</summary>
public sealed class MarkContentElement
{
    /// <summary>property, text, or the element's type name (symbol, new line, …).</summary>
    public string Kind { get; set; } = "";

    /// <summary>The property name for kind=property.</summary>
    public string Name { get; set; } = "";

    /// <summary>What the element shows now.</summary>
    public string Value { get; set; } = "";
}

/// <summary>Filter for objects in the currently active drawing.</summary>
public sealed class DrawingObjectQuery
{
    public List<int> IndexIn { get; set; } = new List<int>();
    public List<TeklaIdentifierInfo> ObjectIds { get; set; } = new List<TeklaIdentifierInfo>();
    public List<string> TypeIn { get; set; } = new List<string>();
    public int? ViewIndex { get; set; }
    public int? ViewId { get; set; }
    public int? ViewId2 { get; set; }
    public string? ModelGuid { get; set; }
    /// <summary>
    /// The represented model object's ID. Drawing objects carry only the model ID (no GUID) on
    /// Tekla 2021, so a ModelGuid filter is resolved to this ID before the drawing is searched.
    /// </summary>
    public int? ModelId { get; set; }
    public string? TextContains { get; set; }
    public bool UseSelection { get; set; }
    public bool Recursive { get; set; } = true;
    public bool IncludeGeometry { get; set; } = true;
    public bool IncludeUdas { get; set; }
    /// <summary>Only marks that show nothing (issue #32: Tekla leaves empty marks behind).</summary>
    public bool EmptyMarksOnly { get; set; }
    /// <summary>Read parts' and bolts' model geometry into the view (issue #38) — a solid read per part.</summary>
    public bool IncludeModelGeometry { get; set; }
    /// <summary>With model geometry: depth range (view z) the object must overlap; bolts keep only positions inside.</summary>
    public double? ZMin { get; set; }
    public double? ZMax { get; set; }
    /// <summary>With model geometry: x/y box (view coordinates) the object must overlap.</summary>
    public Point3D? RegionMin { get; set; }
    public Point3D? RegionMax { get; set; }
    /// <summary>With model geometry: substrings of the model object's name / profile.</summary>
    public string? NameContains { get; set; }
    public string? ProfileContains { get; set; }
}

/// <summary>Result of selecting objects in the active drawing editor.</summary>
public sealed class DrawingSelectionResult
{
    public int SelectedCount { get; set; }
    public List<DrawingObjectInfo> Preview { get; set; } = new List<DrawingObjectInfo>();
    public string Backend { get; set; } = "";
    public string? Message { get; set; }
}

/// <summary>Compact drawing-list analytics.</summary>
public sealed class DrawingSummary
{
    /// <summary>Maximum rows requested for the scan.</summary>
    public int Limit { get; set; }
    /// <summary>True when at least one additional drawing existed beyond the returned scan.</summary>
    public bool Truncated { get; set; }
    public int Total { get; set; }
    public int Issued { get; set; }
    public int IssuedButModified { get; set; }
    public int Locked { get; set; }
    public int ReadyForIssue { get; set; }
    public int NotUpToDate { get; set; }
    public Dictionary<string, int> CountByType { get; set; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> CountByStatus { get; set; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public string Backend { get; set; } = "";
}

/// <summary>One drawing QA issue category with sample drawing keys.</summary>
public sealed class DrawingIssueGroup
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public int Count { get; set; }
    public List<string> SampleKeys { get; set; } = new List<string>();
}

/// <summary>Heuristic QA report over drawing-list state.</summary>
public sealed class DrawingQaReport
{
    public int CheckedCount { get; set; }
    public List<DrawingIssueGroup> Issues { get; set; } = new List<DrawingIssueGroup>();
    public string Backend { get; set; } = "";
}
