using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

// DTOs behind tekla_render_schematic. The backends deliver raw geometry (SchematicScene); the
// picture itself is drawn by TeklaMcp.Core.Rendering.SchematicRenderer, so the mock and the live
// backend produce the same kind of image from the same code.

/// <summary>An axis-aligned box in global model coordinates, millimetres.</summary>
public sealed class Box3D
{
    public Point3D Min { get; set; } = new Point3D();
    public Point3D Max { get; set; } = new Point3D();

    public Box3D() { }

    public Box3D(Point3D min, Point3D max)
    {
        Min = min;
        Max = max;
    }
}

/// <summary>What to put into a schematic: focus objects, context around them, grids.</summary>
public sealed class SchematicSceneRequest
{
    /// <summary>
    /// Focus objects — drawn in colour and numbered. An empty query (no filter, no GUIDs, no
    /// selection) means "no focus": then everything in <see cref="Region"/> — or the whole
    /// model when no region is given — is drawn as an overview.
    /// </summary>
    public ObjectQuery Query { get; set; } = new ObjectQuery();

    /// <summary>Cap on focus objects; more matches set <see cref="SchematicScene.FocusTruncated"/>.</summary>
    public int MaxFocusObjects { get; set; } = 300;

    /// <summary>Draw surrounding parts (thin, grey) inside the focus box grown by <see cref="ContextMarginMm"/>.</summary>
    public bool IncludeContext { get; set; } = true;

    /// <summary>How far around the focus objects context parts are collected, mm.</summary>
    public double ContextMarginMm { get; set; } = 3000;

    /// <summary>
    /// Cap on context objects; more set <see cref="SchematicScene.ContextTruncated"/>. Live cost is
    /// roughly 0.3 ms per part (20 000 parts ≈ 6 s on Tekla 2023, model 3219).
    /// </summary>
    public int MaxContextObjects { get; set; } = 20000;

    /// <summary>
    /// Explicit region (global mm). Limits the focus to objects touching it and replaces the
    /// focus box for context collection.
    /// </summary>
    public Box3D? Region { get; set; }

    /// <summary>Also draw bolt positions. Off by default: bolts are numerous and small.</summary>
    public bool IncludeBolts { get; set; }

    /// <summary>Read the model grids (axes and levels).</summary>
    public bool IncludeGrids { get; set; } = true;

    /// <summary>Optional cap on objects examined by a filter scan (large models).</summary>
    public int? MaxScanObjects { get; set; }
}

/// <summary>One object reduced to drawable geometry.</summary>
public sealed class SchematicItem
{
    public string Guid { get; set; } = "";
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Material { get; set; } = "";
    public string Class { get; set; } = "";
    public string? AssemblyPos { get; set; }

    /// <summary>True for objects the caller asked about; false for surrounding context.</summary>
    public bool Focus { get; set; }

    /// <summary>
    /// "line" (open polyline: reference line or centerline), "polygon" (closed contour),
    /// "points" (bolt positions) or "box" (solid AABB fallback).
    /// </summary>
    public string Shape { get; set; } = "line";

    /// <summary>Polyline / contour / point positions in global mm (empty for "box").</summary>
    public List<Point3D> Points { get; set; } = new List<Point3D>();

    /// <summary>World AABB for <see cref="Shape"/> = "box".</summary>
    public Box3D? Box { get; set; }

    /// <summary>Where the geometry came from: reference-line, centerline, contour, bolt-positions, solid-aabb.</summary>
    public string GeometrySource { get; set; } = "";
}

/// <summary>One labelled grid line (or level) of a <see cref="SchematicGrid"/>, in grid-local mm.</summary>
public sealed class GridAxisLine
{
    public string Label { get; set; } = "";

    /// <summary>Position along the grid's own axis (X, Y) or absolute level (Z), local mm.</summary>
    public double Position { get; set; }
}

/// <summary>A Tekla grid in its own coordinate system: origin + axes + labelled lines and levels.</summary>
public sealed class SchematicGrid
{
    public int? Id { get; set; }
    public Point3D Origin { get; set; } = new Point3D();

    /// <summary>Unit direction of the grid's local X axis (global).</summary>
    public Point3D AxisX { get; set; } = new Point3D(1, 0, 0);

    /// <summary>Unit direction of the grid's local Y axis (global).</summary>
    public Point3D AxisY { get; set; } = new Point3D(0, 1, 0);

    /// <summary>Lines of constant local X (they run along local Y).</summary>
    public List<GridAxisLine> LinesX { get; set; } = new List<GridAxisLine>();

    /// <summary>Lines of constant local Y (they run along local X).</summary>
    public List<GridAxisLine> LinesY { get; set; } = new List<GridAxisLine>();

    /// <summary>Levels (local Z), drawn as horizontal lines in elevation views.</summary>
    public List<GridAxisLine> Levels { get; set; } = new List<GridAxisLine>();
}

/// <summary>Raw geometry for one schematic, produced by a backend.</summary>
public sealed class SchematicScene
{
    public string Backend { get; set; } = "";
    public List<SchematicItem> Items { get; set; } = new List<SchematicItem>();
    public List<SchematicGrid> Grids { get; set; } = new List<SchematicGrid>();

    /// <summary>Focus objects that matched (stops counting at the cap + 1 when truncated).</summary>
    public int FocusMatched { get; set; }
    public bool FocusTruncated { get; set; }
    public bool ContextTruncated { get; set; }

    /// <summary>Objects examined by a filter scan (0 when the focus came from GUIDs).</summary>
    public int ScannedObjects { get; set; }
    public bool ScanTruncated { get; set; }

    /// <summary>Matched objects that had no drawable geometry (e.g. solid budget exhausted).</summary>
    public int SkippedNoGeometry { get; set; }

    /// <summary>GUIDs from the query that do not exist in the model.</summary>
    public List<string> MissingGuids { get; set; } = new List<string>();

    /// <summary>Region used to collect context (null when no context was collected).</summary>
    public Box3D? Region { get; set; }

    public string? Message { get; set; }
    public List<string> Warnings { get; set; } = new List<string>();
}

/// <summary>How to draw a <see cref="SchematicScene"/>.</summary>
public sealed class SchematicRenderOptions
{
    /// <summary>
    /// top | bottom | front | back | left | right | iso (default) | iso_sw | iso_ne | iso_nw, or a
    /// viewing direction "dx,dy,dz" — the direction the camera LOOKS along, the same convention
    /// as Tekla's ViewCamera.DirectionVector (so a camera read by tekla_capture_view can be reused).
    /// </summary>
    public string View { get; set; } = "iso";

    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 960;

    /// <summary>number (default) | id | mark | profile | name | none.</summary>
    public string Labels { get; set; } = "number";

    /// <summary>class (Tekla-like class colours, default) | type | profile | material | none.</summary>
    public string ColorBy { get; set; } = "class";

    public bool ShowGrids { get; set; } = true;

    /// <summary>Mark start (yellow) and end (magenta) of focus lines, like Tekla's handles.</summary>
    public bool ShowHandles { get; set; }

    /// <summary>Also number context objects (by default only focus objects get labels).</summary>
    public bool LabelContext { get; set; }

    public int MaxLabels { get; set; } = 150;

    /// <summary>Frame the focus objects when there are any (default); false frames everything.</summary>
    public bool FitToFocus { get; set; } = true;

    /// <summary>Colour context by <see cref="ColorBy"/> even when focus objects exist.</summary>
    public bool ContextColored { get; set; }

    /// <summary>Optional fixed colour "#RRGGBB" for all focus objects (overrides <see cref="ColorBy"/>).</summary>
    public string? HighlightColor { get; set; }

    /// <summary>Optional red banner, e.g. to mark a mock stand-in.</summary>
    public string? Banner { get; set; }
}

/// <summary>One numbered object on the picture.</summary>
public sealed class SchematicLegendEntry
{
    /// <summary>Text drawn next to the object.</summary>
    public string Label { get; set; } = "";
    public string Guid { get; set; } = "";
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Class { get; set; } = "";
    public string? AssemblyPos { get; set; }
    public bool Focus { get; set; }

    /// <summary>False when no free spot was found for the label (object still drawn).</summary>
    public bool LabelPlaced { get; set; }

    /// <summary>Pixel where the label's leader starts (on the object).</summary>
    public int? X { get; set; }
    public int? Y { get; set; }

    public string GeometrySource { get; set; } = "";
}

/// <summary>Encoded picture returned next to a result DTO (never serialized into JSON).</summary>
public sealed class RenderedImage
{
    public byte[] Bytes { get; set; } = new byte[0];
    public string MimeType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Result of <c>SchematicRenderer.Render</c>: how to read the picture.</summary>
public sealed class SchematicRenderResult
{
    public bool Rendered { get; set; }
    public string Source { get; set; } = "schematic";

    /// <summary>
    /// "beta": the renderer is an early version (it can look cluttered on dense models). Kept in
    /// the result so an agent that did not read the tool description still knows.
    /// </summary>
    public string Stage { get; set; } = "beta";

    public string Backend { get; set; } = "";

    /// <summary>Normalized view name (e.g. "iso") or "custom".</summary>
    public string View { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Direction the camera looks along (global unit vector).</summary>
    public Point3D ViewDirection { get; set; } = new Point3D();

    /// <summary>Global direction of +pixel X (to the right).</summary>
    public Point3D ScreenRight { get; set; } = new Point3D();

    /// <summary>Global direction of "up" on the picture (−pixel Y).</summary>
    public Point3D ScreenUp { get; set; } = new Point3D();

    public double MmPerPixel { get; set; }

    /// <summary>
    /// Model point (on the view plane through the framed centre) at pixel (0,0). A pixel maps
    /// back as origin + px·ScreenRight·MmPerPixel − py·ScreenUp·MmPerPixel; depth along
    /// <see cref="ViewDirection"/> is not recoverable from a picture.
    /// </summary>
    public Point3D PixelOrigin { get; set; } = new Point3D();

    /// <summary>Human-readable pixel → model formula (explicit X/Y/Z for axis views).</summary>
    public string PixelToModel { get; set; } = "";

    /// <summary>Model box of what was framed.</summary>
    public Box3D? Framed { get; set; }

    public string ColorBy { get; set; } = "";

    /// <summary>Colour per category actually drawn, e.g. {"class 2": "#D62020"}.</summary>
    public Dictionary<string, string> Colors { get; set; } = new Dictionary<string, string>();

    public int FocusDrawn { get; set; }
    public int ContextDrawn { get; set; }
    public int LabelsPlaced { get; set; }

    /// <summary>Objects that should have been labelled but were not (label cap or no free space).</summary>
    public int LabelsOmitted { get; set; }

    public int GridLinesDrawn { get; set; }

    // Scene coverage, copied so one JSON answers "is anything missing from this picture?".
    public int FocusMatched { get; set; }
    public bool FocusTruncated { get; set; }
    public bool ContextTruncated { get; set; }
    public int ScannedObjects { get; set; }
    public bool ScanTruncated { get; set; }
    public int SkippedNoGeometry { get; set; }
    public List<string> MissingGuids { get; set; } = new List<string>();

    public List<SchematicLegendEntry> Legend { get; set; } = new List<SchematicLegendEntry>();
    public string? Message { get; set; }
    public List<string> Warnings { get; set; } = new List<string>();

    /// <summary>The PNG. Tools send it as an MCP image block and null it before serializing.</summary>
    public RenderedImage? Image { get; set; }
}
