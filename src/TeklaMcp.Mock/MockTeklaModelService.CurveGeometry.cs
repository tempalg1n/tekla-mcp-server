using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Mock;

/// <summary>
/// Mock half of <c>tekla_get_part_curve_geometry</c>. The synthetic arch is served the way the
/// live backend receives it from <c>PolyBeam.GetCenterLinePolycurve()</c> — split into two arcs
/// at its ARC_POINT apex — so the merge, the round-section offsets and the view projection run
/// through exactly the same <see cref="CurveMath"/> code as on Tekla.
/// </summary>
public sealed partial class MockTeklaModelService
{
    // The PD168.3*6 arch over the Y=3000 line: chord 6000 mm, rise 1000 mm, hence centerline
    // radius (3000² + 1000²) / (2 × 1000) = 5000 mm about (3000, 3000, 0), sweep 2·asin(0.6)
    // = 73.74°, arc length 6435.011 mm.
    private const string ArchProfile = "PD168.3*6";
    private const double ArchDiameter = 168.3;
    private const double ArchLength = 6435.011;
    private static readonly Point3D ArchStart = new Point3D(0, 3000, 4000);
    private static readonly Point3D ArchApex = new Point3D(3000, 3000, 5000);
    private static readonly Point3D ArchEnd = new Point3D(6000, 3000, 4000);
    private static readonly Point3D ArchCenter = new Point3D(3000, 3000, 0);
    private static readonly Point3D ArchAxis = new Point3D(0, 1, 0);

    public PartCurveGeometry GetPartCurveGeometry(PartCurveGeometryRequest request)
    {
        request = request ?? new PartCurveGeometryRequest();
        var result = new PartCurveGeometry { Guid = request.Guid ?? "", Backend = BackendName };

        var obj = _objects.FirstOrDefault(o => Eq(o.Guid, request.Guid));
        if (obj == null)
        {
            result.Message = "Object not found.";
            return result;
        }

        result.Found = true;
        result.Guid = obj.Guid;
        result.Id = obj.Id;
        result.Type = obj.Type;
        result.Name = obj.Name;
        result.Profile = obj.Profile;
        result.ProfileType = MockProfileType(obj.Profile);
        result.ReportedLength = obj.LengthMm;
        result.Section = MockSection(result.ProfileType, obj.Profile);

        switch (obj.Type)
        {
            case "PolyBeam" when obj.Profile == ArchProfile:
                result.CurveSource = "PolyBeam.GetCenterLinePolycurve";
                result.Segments.Add(CurveMath.ArcAround(ArchCenter, ArchStart, ArchApex, ArchAxis)!);
                result.Segments.Add(CurveMath.ArcAround(ArchCenter, ArchApex, ArchEnd, ArchAxis)!);
                result.Contour.Add(new ContourPointInfo { Point = Clone(ArchStart) });
                result.Contour.Add(new ContourPointInfo { Point = Clone(ArchApex), Chamfer = "arc_point" });
                result.Contour.Add(new ContourPointInfo { Point = Clone(ArchEnd) });
                break;

            case "Beam" when obj.StartX.HasValue && obj.EndX.HasValue:
                result.CurveSource = "Part.GetCenterLine";
                result.Segments.Add(CurveMath.Line(
                    new Point3D(obj.StartX.Value, obj.StartY ?? 0, obj.StartZ ?? 0),
                    new Point3D(obj.EndX.Value, obj.EndY ?? 0, obj.EndZ ?? 0)));
                break;

            case "ContourPlate":
                // The base plates are PL20*400 squares centred on their column.
                var cx = obj.CenterX ?? 0;
                var cy = obj.CenterY ?? 0;
                var cz = obj.CenterZ ?? 0;
                foreach (var (dx, dy) in new[] { (-200.0, -200.0), (200.0, -200.0), (200.0, 200.0), (-200.0, 200.0) })
                    result.Contour.Add(new ContourPointInfo { Point = new Point3D(cx + dx, cy + dy, cz) });
                result.Message = "A ContourPlate has no centerline: only its contour and chamfers are returned.";
                break;

            case "PolyBeam":
                result.Message = "The mock has no curve data for this PolyBeam.";
                break;

            default:
                result.Message = obj.Type + " is not a part with a centerline. Curve geometry covers " +
                                 "PolyBeam and Beam (ContourPlate: contour only).";
                break;
        }

        CurveMath.Complete(result);
        if (request.WantsView)
            ProjectIntoMockView(result, request);
        CurveMath.Round(result);
        return result;
    }

    private void ProjectIntoMockView(PartCurveGeometry result, PartCurveGeometryRequest request)
    {
        if (string.IsNullOrWhiteSpace(_activeDrawingKey))
        {
            result.Warnings.Add("No active drawing: the view projection was skipped (model coordinates only).");
            return;
        }

        var views = GetActiveMockDrawingViews();
        DrawingViewInfo? view;
        if (request.ViewId.HasValue)
        {
            view = request.ViewId.Value == 0 && (request.ViewId2 ?? 0) == 0
                ? null
                : views.FirstOrDefault(v => v.ViewId == request.ViewId.Value &&
                                            (!request.ViewId2.HasValue || v.ViewId2 == request.ViewId2.Value));
        }
        else
        {
            view = views.FirstOrDefault(v => v.Index == (request.ViewIndex ?? -1));
        }

        if (view == null)
        {
            result.Warnings.Add(
                "View not found in the active drawing (viewId=" + (request.ViewId?.ToString() ?? "-") +
                ", viewId2=" + (request.ViewId2?.ToString() ?? "-") +
                ", viewIndex=" + (request.ViewIndex?.ToString() ?? "-") +
                "); list views with tekla_list_drawing_views. Model coordinates only.");
            return;
        }

        var toView = CurveMath.ToLocal(view.DisplayCoordinateSystem);
        if (toView == null)
        {
            result.Warnings.Add("View " + view.ViewId + " has no display coordinate system; the projection was skipped.");
            return;
        }

        var projection = CurveMath.Project(result, toView);
        projection.ViewIndex = view.Index;
        projection.ViewId = view.ViewId;
        projection.ViewId2 = view.ViewId2;
        projection.ViewName = view.Name;
        projection.ViewType = view.Type;
        projection.Transform = "global model -> View.DisplayCoordinateSystem (mock views)";
        result.View = projection;
    }

    private static Point3D Clone(Point3D p) => new Point3D(p.X, p.Y, p.Z);

    /// <summary>PROFILE_TYPE as Tekla would report it for the mock catalog.</summary>
    private static string MockProfileType(string profile)
    {
        var p = (profile ?? "").Trim().ToUpperInvariant();
        if (p.StartsWith("PD", StringComparison.Ordinal)) return "RO";
        if (p.StartsWith("PL", StringComparison.Ordinal)) return "B";
        if (p.StartsWith("HEA", StringComparison.Ordinal) || p.StartsWith("IPE", StringComparison.Ordinal)) return "I";
        if (p.StartsWith("L", StringComparison.Ordinal)) return "L";
        if (p.StartsWith("D", StringComparison.Ordinal)) return "RU";
        return "";
    }

    private static CurveSectionInfo MockSection(string profileType, string profile)
    {
        if (!CurveMath.IsRoundProfileType(profileType))
            return new CurveSectionInfo
            {
                Shape = string.IsNullOrEmpty(profileType) ? "unknown" : "other",
                Note = "Inner/outer arcs are computed for round sections (RO/RU) only.",
            };

        return profile == ArchProfile
            ? new CurveSectionInfo { Shape = "round", Diameter = ArchDiameter, Source = "PROFILE.DIAMETER" }
            : new CurveSectionInfo { Shape = "round", Note = "The mock has no diameter for this profile." };
    }
}
