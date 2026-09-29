using System;
using System.Collections;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using TS = Tekla.Structures;
using TSD = Tekla.Structures.Drawing;
using TSG = Tekla.Structures.Geometry3d;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla;

/// <summary>
/// Live half of <c>tekla_get_part_curve_geometry</c> (issue #15). Reads what Tekla itself knows
/// about a part's centerline — for a PolyBeam <c>GetCenterLinePolycurve()</c>, whose exact
/// <c>LineSegment</c>/<c>Arc</c> pieces replace the segmented polyline of <c>GetCenterLine</c> —
/// plus the contour with chamfers, PROFILE_TYPE and PROFILE.DIAMETER. Everything derived (bends,
/// chord, sagitta, inner/outer arcs, view projection, rounding) is <see cref="CurveMath"/>.
///
/// Semantics verified live on Tekla 2023 (model 3219, 2026-09-29): Arc.Angle is radians,
/// Arc.ArcMiddlePoint is the angular midpoint, Arc.Normal = (start − center) × (mid − center)
/// normalized; the polycurve is the true centerline (offset from the reference line by the
/// part position), it splits an ARC_POINT bend into two arcs, and it does NOT include boolean
/// parts / fittings (LENGTH can differ — CurveMath warns). See docs/tekla-api-notes.md.
/// </summary>
public sealed partial class TeklaModelService
{
    public PartCurveGeometry GetPartCurveGeometry(PartCurveGeometryRequest request)
    {
        request = request ?? new PartCurveGeometryRequest();
        var result = new PartCurveGeometry { Guid = request.Guid ?? "", Backend = BackendName };
        try
        {
            if (!Guid.TryParse(request.Guid, out var guid))
            {
                result.Message = "Not a valid GUID: '" + request.Guid + "'.";
                return result;
            }

            var model = GetConnectedModel();
            InGlobalWorkPlane(model, () =>
            {
                var mo = model.SelectModelObject(new TS.Identifier(guid));
                if (mo == null)
                {
                    result.Message = "Object not found.";
                    return false;
                }

                ReadCurveGeometry(mo, result);
                CurveMath.Complete(result);
                // Inside the global work plane on purpose: the content tools convert
                // coordinateSpace=model points under the same plane.
                if (request.WantsView)
                    ProjectIntoView(result, request);
                return true;
            });
        }
        catch (Exception ex)
        {
            result.Message = AppendMessage(result.Message, ErrorText.Flatten(ex));
        }

        CurveMath.Round(result);
        return result;
    }

    private static void ReadCurveGeometry(TSM.ModelObject mo, PartCurveGeometry result)
    {
        result.Found = true;
        result.Guid = mo.Identifier.GUID.ToString();
        result.Id = mo.Identifier.ID;
        result.Type = mo.GetType().Name;

        var part = mo as TSM.Part;
        if (part != null)
        {
            result.Name = part.Name ?? "";
            result.Profile = part.Profile?.ProfileString ?? "";
            var profileType = "";
            if (part.GetReportProperty("PROFILE_TYPE", ref profileType))
                result.ProfileType = profileType ?? "";
            var length = 0.0;
            if (part.GetReportProperty("LENGTH", ref length))
                result.ReportedLength = length;
            result.Section = ReadSection(part, result.ProfileType);
        }

        switch (mo)
        {
            case TSM.PolyBeam polyBeam:
                ReadContour(polyBeam.Contour, result);
                ReadPolycurve(polyBeam, result);
                break;
            case TSM.ContourPlate plate:
                ReadContour(plate.Contour, result);
                result.Message = "A ContourPlate has no centerline: only its contour and chamfers are returned.";
                break;
            case TSM.Beam beam:
                ReadStraightCenterLine(beam, result);
                break;
            default:
                result.Message = result.Type +
                                 (part == null ? " is not a part with a centerline." : " has no supported centerline.") +
                                 " Curve geometry covers PolyBeam and Beam (ContourPlate: contour only).";
                break;
        }
    }

    private static CurveSectionInfo ReadSection(TSM.Part part, string profileType)
    {
        if (!CurveMath.IsRoundProfileType(profileType))
            return new CurveSectionInfo
            {
                Shape = string.IsNullOrWhiteSpace(profileType) ? "unknown" : "other",
                Note = "Inner/outer arcs are computed for round sections (PROFILE_TYPE RO/RU) only.",
            };

        // PROFILE.DIAMETER, never HEIGHT: on a bent PolyBeam HEIGHT is not the profile height —
        // a bent D30 anchor reports HEIGHT = 2655.98 while PROFILE.DIAMETER = 30 (Tekla 2023).
        var diameter = 0.0;
        if (part.GetReportProperty("PROFILE.DIAMETER", ref diameter) && diameter > 0)
            return new CurveSectionInfo { Shape = "round", Diameter = diameter, Source = "PROFILE.DIAMETER" };

        return new CurveSectionInfo
        {
            Shape = "round",
            Note = "PROFILE.DIAMETER is not available for this profile, so no inner/outer arcs.",
        };
    }

    private static void ReadContour(TSM.Contour? contour, PartCurveGeometry result)
    {
        if (contour?.ContourPoints == null) return;
        foreach (var item in (IEnumerable)contour.ContourPoints)
        {
            if (!(item is TSG.Point point)) continue;
            var info = new ContourPointInfo { Point = RawPoint(point) };
            var chamfer = (item as TSM.ContourPoint)?.Chamfer;
            if (chamfer != null)
            {
                info.Chamfer = CurveMath.NormalizeChamferType(chamfer.Type.ToString());
                if (info.Chamfer != "none")
                {
                    info.ChamferX = chamfer.X;
                    info.ChamferY = chamfer.Y;
                    info.ChamferDz1 = chamfer.DZ1;
                    info.ChamferDz2 = chamfer.DZ2;
                }
            }
            result.Contour.Add(info);
        }
    }

    private static void ReadPolycurve(TSM.PolyBeam polyBeam, PartCurveGeometry result)
    {
        TSG.Polycurve? curve;
        try
        {
            curve = polyBeam.GetCenterLinePolycurve();
        }
        catch (Exception ex)
        {
            // TODO(windows): verified on Tekla 2023 only; report per-version failures here.
            result.Warnings.Add("PolyBeam.GetCenterLinePolycurve() failed: " + ErrorText.Flatten(ex));
            return;
        }
        if (curve == null)
        {
            result.Warnings.Add("PolyBeam.GetCenterLinePolycurve() returned no curve.");
            return;
        }

        result.CurveSource = "PolyBeam.GetCenterLinePolycurve";
        foreach (var piece in curve)
        {
            switch (piece)
            {
                case TSG.Arc arc:
                    var segment = CurveMath.Arc(
                        RawPoint(arc.StartPoint),
                        RawPoint(arc.ArcMiddlePoint),
                        RawPoint(arc.EndPoint),
                        RawPoint(arc.CenterPoint),
                        normalHint: new Point3D(arc.Normal.X, arc.Normal.Y, arc.Normal.Z));
                    if (segment == null)
                    {
                        result.Warnings.Add("Segment " + result.Segments.Count +
                                            ": Tekla returned a degenerate arc; it is kept as a straight line.");
                        segment = CurveMath.Line(RawPoint(arc.StartPoint), RawPoint(arc.EndPoint));
                    }
                    else
                    {
                        segment.Index = result.Segments.Count;
                        result.Warnings.AddRange(CurveMath.CheckReportedArc(segment, arc.Radius, arc.Angle, arc.Length));
                    }
                    result.Segments.Add(segment);
                    break;
                case TSG.LineSegment line:
                    result.Segments.Add(CurveMath.Line(RawPoint(line.StartPoint), RawPoint(line.EndPoint)));
                    break;
                default:
                    result.Warnings.Add("Segment " + result.Segments.Count + ": unsupported curve piece " +
                                        (piece == null ? "null" : piece.GetType().Name) +
                                        " was skipped, so the centerline is incomplete.");
                    break;
            }
        }
    }

    private static void ReadStraightCenterLine(TSM.Part part, PartCurveGeometry result)
    {
        var points = part.GetCenterLine(false)?.OfType<TSG.Point>().ToList();
        if (points == null || points.Count < 2)
        {
            result.Warnings.Add("Part.GetCenterLine(false) returned fewer than two points.");
            return;
        }
        result.CurveSource = "Part.GetCenterLine";
        result.Segments.Add(CurveMath.Line(RawPoint(points[0]), RawPoint(points[points.Count - 1])));
    }

    private static void ProjectIntoView(PartCurveGeometry result, PartCurveGeometryRequest request)
    {
        try
        {
            var handler = GetDrawingHandler();
            var active = TryGetActiveDrawing(handler);
            if (active == null)
            {
                result.Warnings.Add("No active drawing: the view projection was skipped (model coordinates only).");
                return;
            }

            var views = EnumerateViews(active);
            var view = ResolveView(views, request.ViewIndex ?? -1, request.ViewId, request.ViewId2);
            if (view == null)
            {
                result.Warnings.Add(
                    "View not found in the active drawing (viewId=" + (request.ViewId?.ToString() ?? "-") +
                    ", viewId2=" + (request.ViewId2?.ToString() ?? "-") +
                    ", viewIndex=" + (request.ViewIndex?.ToString() ?? "-") +
                    "); list views with tekla_list_drawing_views. Model coordinates only.");
                return;
            }

            var toView = GlobalToViewMatrix(view);
            var projection = CurveMath.Project(result, p => RawPoint(toView.Transform(ToPoint(p))));
            var identifier = GetOwnViewIdentifier(view);
            projection.ViewIndex = views.IndexOf(view);
            projection.ViewId = identifier.ID;
            projection.ViewId2 = identifier.ID2;
            projection.ViewName = view.Name ?? "";
            projection.ViewType = view.ViewType.ToString();
            projection.Transform =
                "global model -> View.DisplayCoordinateSystem (the same matrix as coordinateSpace=model in the drawing content tools)";
            result.View = projection;
        }
        catch (Exception ex)
        {
            // The drawing layer is experimental; the model geometry above stays valid.
            result.Warnings.Add("View projection failed, model coordinates are still valid: " + ErrorText.Flatten(ex));
        }
    }

    /// <summary>Unrounded copy — CurveMath derives from full precision and rounds once at the end.</summary>
    private static Point3D RawPoint(TSG.Point point) => new Point3D(point.X, point.Y, point.Z);
}
