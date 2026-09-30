using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Grid / geometry context tools. They let an agent translate human axis references
/// ("between axes 1 and 2 along axis Д") into model coordinates before creating geometry.
/// </summary>
[McpServerToolType]
public static class ModelGeometryTools
{
    [McpServerTool(Name = "tekla_list_grids")]
    [Description("List grid lines and levels with their real Tekla labels: axis ('X'/'Y' lines, 'Z' " +
                 "levels), label, coordinate (global mm), gridId, and start/end points of X/Y lines. " +
                 "Use to translate axis references like '1', '2', 'Д' or '+3.300' into coordinates. A " +
                 "model can hold several grids with their own origins and repeated labels — check " +
                 "gridId; for a grid rotated against the global axes (rotated=true) use start/end, " +
                 "not coordinate.")]
    public static IReadOnlyList<GridLineInfo> ListGrids(ITeklaModelService model)
        => model.GetGrids();

    [McpServerTool(Name = "tekla_resolve_point")]
    [Description("Resolve a model point (X,Y,Z mm) from an X-axis label, a Y-axis label and an " +
                 "elevation, using the model grids. Example: axisX='1', axisY='Д', z=6000.")]
    public static PointResult ResolvePoint(
        ITeklaModelService model,
        [Description("X-axis grid label, e.g. '1'.")] string axisX,
        [Description("Y-axis grid label, e.g. 'Д'.")] string axisY,
        [Description("Elevation Z in mm.")] double z)
        => model.ResolvePoint(axisX, axisY, z);

    [McpServerTool(Name = "tekla_get_part_curve_geometry")]
    [Description(
        "Exact centerline geometry of ONE part, for dimensioning curved members (PolyBeam, e.g. " +
        "curved tubes, bent bars, curved monorails) in drawings. Read-only. Returns: 'segments' — " +
        "lines and arcs exactly as Tekla computes them (arc: start/mid/end/center/normal, radius, " +
        "sweepAngle, arc length, chord, sagitta); 'arcs' — one entry per physical bend (Tekla splits " +
        "an ARC_POINT bend into two arcs; these are merged), use THESE for radius, chord, rise and " +
        "arc-length dimensions; for round sections (PROFILE_TYPE RO/RU) each bend also has 'inner' " +
        "and 'outer' arcs at radius ∓ D/2; 'contour' — the modelled input points with chamfers. " +
        "Units: mm and degrees, global model coordinates. Pass viewId/viewId2 (or viewIndex) of the " +
        "ACTIVE drawing to also get every point in that view's coordinates ('view' block, use with " +
        "coordinateSpace=view); model points work directly with coordinateSpace=model in " +
        "tekla_create_radius_dimension / tekla_create_curved_dimension / " +
        "tekla_create_straight_dimension. For a part in a drawing pass the modelGuid from " +
        "tekla_list_drawing_objects. Read 'warnings': cuts, fittings and boolean parts are NOT in " +
        "the centerline, so a LENGTH mismatch is flagged there. Beam: one straight line; " +
        "ContourPlate: contour only.")]
    public static PartCurveGeometry GetPartCurveGeometry(
        ITeklaModelService model,
        [Description("Model part GUID (for a drawing part: its modelGuid).")] string guid,
        [Description("Optional: non-zero View ID in the active drawing to also express the geometry in.")]
        int? viewId = null,
        [Description("Optional: View ID2 of that view.")] int? viewId2 = null,
        [Description("Optional, ephemeral view index; prefer viewId/viewId2.")] int? viewIndex = null)
        => model.GetPartCurveGeometry(new PartCurveGeometryRequest
        {
            Guid = (guid ?? "").Trim(),
            ViewId = viewId,
            ViewId2 = viewId2,
            ViewIndex = viewIndex,
        });

    private const int MaxSolidGuids = 20;

    [McpServerTool(Name = "tekla_get_part_solid")]
    [Description("The actual shape of up to 20 parts: faces (outward normal, the part that produced the " +
                 "face — a cutting part for a cut face), boundary loops and vertices, GLOBAL mm, with cuts, " +
                 "fittings and chamfers applied (solidType NORMAL, default; RAW = the uncut profile sweep). " +
                 "outerLoopIndex marks each face's outer boundary (the largest loop; others are holes). Use it " +
                 "when a bounding box is not enough — cut ends, notches, holes, clash and fit checks. Capped by " +
                 "maxFaces/maxPoints: when truncated=true the face list is NOT the whole shape (faces are left " +
                 "out whole, never cut). The AABB is a box around the solid, never the shape. For thousands of " +
                 "boxes use tekla_export_parts_file (solidAabb) instead. Read-only.")]
    public static IReadOnlyList<PartSolidGeometry> GetPartSolid(
        ITeklaModelService model,
        [Description("Part GUIDs (max 20), comma/semicolon/newline separated.")] string guids,
        [Description("NORMAL (default), RAW, FITTED, HIGH_ACCURACY, PLANECUTTED, NORMAL_WITHOUT_EDGECHAMFERS, " +
                     "NORMAL_WITHOUT_WELDPREPS.")] string? solidType = null,
        [Description("Faces returned per part (default 200, max 5000); the rest are only counted.")] int maxFaces = 200,
        [Description("Vertices returned per part (default 1000, max 50000).")] int maxPoints = 1000)
    {
        var parsed = ToolHelpers.ParseList(guids);
        if (parsed.Count == 0)
            throw new ModelContextProtocol.McpException("Pass at least one part GUID.");
        if (parsed.Count > MaxSolidGuids)
            throw new ModelContextProtocol.McpException(
                $"At most {MaxSolidGuids} parts per call (got {parsed.Count}) — every solid is a GetSolid() call. " +
                "Split the list, or use tekla_export_parts_file with solidAabb for many parts.");
        if (!string.IsNullOrWhiteSpace(solidType) &&
            !System.Linq.Enumerable.Contains(PartSolidRequest.SolidTypes, solidType!.Trim().ToUpperInvariant()))
            throw new ModelContextProtocol.McpException(
                "Unknown solidType '" + solidType + "'. Use one of: " + string.Join(", ", PartSolidRequest.SolidTypes) + ".");
        return model.GetPartSolids(new PartSolidRequest
        {
            Guids = parsed,
            SolidType = solidType,
            MaxFaces = maxFaces,
            MaxPoints = maxPoints,
        });
    }
}
