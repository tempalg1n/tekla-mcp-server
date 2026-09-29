using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>Input of <see cref="ITeklaModelService.GetPartCurveGeometry"/>.</summary>
public sealed class PartCurveGeometryRequest
{
    /// <summary>Model part GUID. For a part shown in a drawing, use the drawing object's ModelGuid.</summary>
    public string Guid { get; set; } = "";

    /// <summary>
    /// Optional active-drawing view to ALSO express the geometry in (view-local coordinates),
    /// addressed exactly like the drawing tools: non-zero <see cref="ViewId"/>/<see cref="ViewId2"/>
    /// preferred, <see cref="ViewIndex"/> as an ephemeral fallback. None set = model space only.
    /// </summary>
    public int? ViewIndex { get; set; }

    public int? ViewId { get; set; }

    public int? ViewId2 { get; set; }

    /// <summary>True when a view projection was asked for.</summary>
    public bool WantsView =>
        (ViewId.HasValue && (ViewId.Value != 0 || (ViewId2 ?? 0) != 0)) ||
        (ViewIndex.HasValue && ViewIndex.Value >= 0);
}

/// <summary>
/// Exact centerline geometry of one part, for dimensioning curved members in drawings: straight
/// and circular segments as Tekla computes them, arcs merged per physical bend with radius,
/// sweep, chord, sagitta and arc length, plus the inner/outer arcs of round sections.
/// Everything is in <see cref="CoordinateSpace"/> (global model millimetres) unless it sits in
/// <see cref="View"/>.
/// </summary>
public sealed class PartCurveGeometry
{
    public bool Found { get; set; }
    public string Guid { get; set; } = "";
    public int Id { get; set; }

    /// <summary>Object type, e.g. PolyBeam, Beam, ContourPlate.</summary>
    public string Type { get; set; } = "";

    public string Name { get; set; } = "";
    public string Profile { get; set; } = "";

    /// <summary>Tekla PROFILE_TYPE report property: RO = round tube, RU = round bar, I, L, B, M, ...</summary>
    public string ProfileType { get; set; } = "";

    /// <summary>Length unit of every coordinate, radius and length in this result.</summary>
    public string Units { get; set; } = "mm";

    /// <summary>Unit of every sweep angle in this result.</summary>
    public string AngleUnits { get; set; } = "deg";

    /// <summary>Coordinate space of <see cref="Segments"/>, <see cref="Arcs"/> and <see cref="Contour"/>.</summary>
    public string CoordinateSpace { get; set; } = "model";

    /// <summary>
    /// Where the curve came from: <c>PolyBeam.GetCenterLinePolycurve</c> (exact lines and arcs),
    /// <c>Part.GetCenterLine</c> (straight part), or empty when no curve is available.
    /// </summary>
    public string CurveSource { get; set; } = "";

    /// <summary>Sum of segment lengths along the centerline.</summary>
    public double? CenterlineLength { get; set; }

    /// <summary>The part's LENGTH report property, for comparison with <see cref="CenterlineLength"/>.</summary>
    public double? ReportedLength { get; set; }

    public CurveSectionInfo? Section { get; set; }

    /// <summary>Centerline pieces in Tekla's order. An ARC_POINT bend arrives as two arcs.</summary>
    public List<CurveSegmentInfo> Segments { get; set; } = new List<CurveSegmentInfo>();

    /// <summary>One entry per physical bend: consecutive co-circular arcs merged. Use these for dimensions.</summary>
    public List<CurveArcInfo> Arcs { get; set; } = new List<CurveArcInfo>();

    /// <summary>
    /// The modelled input points with their chamfers (PolyBeam / ContourPlate), i.e. the reference
    /// line as entered — NOT the centerline. Empty for other types.
    /// </summary>
    public List<ContourPointInfo> Contour { get; set; } = new List<ContourPointInfo>();

    /// <summary>The same segments and arcs in an active-drawing view, when one was requested.</summary>
    public CurveViewProjection? View { get; set; }

    public List<string> Warnings { get; set; } = new List<string>();
    public string? Message { get; set; }
    public string Backend { get; set; } = "";
}

/// <summary>Cross-section facts the inner/outer arcs are derived from.</summary>
public sealed class CurveSectionInfo
{
    /// <summary><c>round</c> (RO/RU profiles), <c>other</c>, or <c>unknown</c>.</summary>
    public string Shape { get; set; } = "unknown";

    /// <summary>Outer diameter of a round section.</summary>
    public double? Diameter { get; set; }

    /// <summary>Property the diameter was read from, e.g. PROFILE.DIAMETER.</summary>
    public string? Source { get; set; }

    public string? Note { get; set; }
}

/// <summary>One centerline piece: a straight line or a circular arc.</summary>
public sealed class CurveSegmentInfo
{
    public int Index { get; set; }

    /// <summary><c>line</c> or <c>arc</c>.</summary>
    public string Kind { get; set; } = "line";

    public Point3D Start { get; set; } = new Point3D();
    public Point3D End { get; set; } = new Point3D();

    /// <summary>Length along the curve (the arc length for an arc).</summary>
    public double Length { get; set; }

    /// <summary>Straight distance start–end.</summary>
    public double ChordLength { get; set; }

    /// <summary>Arc only: the point halfway along the arc.</summary>
    public Point3D? Mid { get; set; }

    /// <summary>Arc only.</summary>
    public Point3D? Center { get; set; }

    /// <summary>Arc only: unit rotation axis, right-handed from start through mid to end.</summary>
    public Point3D? Normal { get; set; }

    /// <summary>Arc only.</summary>
    public double? Radius { get; set; }

    /// <summary>Arc only: central angle, degrees.</summary>
    public double? SweepAngle { get; set; }

    /// <summary>Arc only: rise of the arc over its chord.</summary>
    public double? Sagitta { get; set; }
}

/// <summary>A physical bend: one or more consecutive centerline arcs on the same circle.</summary>
public sealed class CurveArcInfo
{
    /// <summary>Indices into <see cref="PartCurveGeometry.Segments"/> that make up this bend.</summary>
    public List<int> SegmentIndices { get; set; } = new List<int>();

    public Point3D Start { get; set; } = new Point3D();
    public Point3D Mid { get; set; } = new Point3D();
    public Point3D End { get; set; } = new Point3D();
    public Point3D Center { get; set; } = new Point3D();
    public Point3D Normal { get; set; } = new Point3D();
    public double Radius { get; set; }

    /// <summary>Central angle, degrees.</summary>
    public double SweepAngle { get; set; }

    /// <summary>Arc length.</summary>
    public double Length { get; set; }

    public double ChordLength { get; set; }
    public double Sagitta { get; set; }

    /// <summary>Round sections only: the intrados (radius − D/2), in the same plane and sweep.</summary>
    public ArcOffsetInfo? Inner { get; set; }

    /// <summary>Round sections only: the extrados (radius + D/2).</summary>
    public ArcOffsetInfo? Outer { get; set; }
}

/// <summary>An arc concentric with a bend's centerline arc, offset in its plane.</summary>
public sealed class ArcOffsetInfo
{
    public double Radius { get; set; }
    public Point3D Start { get; set; } = new Point3D();
    public Point3D Mid { get; set; } = new Point3D();
    public Point3D End { get; set; } = new Point3D();
    public double Length { get; set; }
    public double ChordLength { get; set; }
    public double Sagitta { get; set; }
}

/// <summary>A modelled input point and the chamfer applied at it.</summary>
public sealed class ContourPointInfo
{
    public Point3D Point { get; set; } = new Point3D();

    /// <summary>none, line, rounding, arc, arc_point, square, square_parallel, line_and_arc.</summary>
    public string Chamfer { get; set; } = "none";

    public double? ChamferX { get; set; }
    public double? ChamferY { get; set; }
    public double? ChamferDz1 { get; set; }
    public double? ChamferDz2 { get; set; }
}

/// <summary>Segments and arcs re-expressed in one active-drawing view.</summary>
public sealed class CurveViewProjection
{
    public int ViewIndex { get; set; }
    public int ViewId { get; set; }
    public int ViewId2 { get; set; }
    public string ViewName { get; set; } = "";
    public string ViewType { get; set; } = "";

    /// <summary>Always <c>view</c>: pass these points with coordinateSpace=view.</summary>
    public string CoordinateSpace { get; set; } = "view";

    /// <summary>How the points were produced.</summary>
    public string Transform { get; set; } = "";

    public List<CurveSegmentInfo> Segments { get; set; } = new List<CurveSegmentInfo>();
    public List<CurveArcInfo> Arcs { get; set; } = new List<CurveArcInfo>();
}
