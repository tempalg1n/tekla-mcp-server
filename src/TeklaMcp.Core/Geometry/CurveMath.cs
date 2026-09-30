using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Geometry;

/// <summary>
/// Backend-agnostic geometry behind <c>tekla_get_part_curve_geometry</c> (issue #15). Backends
/// hand over the raw centerline pieces — on the live backend Tekla's own
/// <c>PolyBeam.GetCenterLinePolycurve()</c> arcs — and everything derived happens here: sweep,
/// chord, sagitta, bends merged across Tekla's split arcs, round-section inner/outer arcs, the
/// view projection and output rounding. The mock and the live backend therefore compute
/// identically, and all of it is unit-tested without Tekla.
///
/// Conventions (verified against live Tekla 2023 arcs, see docs/tekla-api-notes.md): an arc is
/// start → mid → end with mid the ANGULAR midpoint, and its normal is the right-handed rotation
/// axis, normalize((start − center) × (mid − center)).
/// </summary>
public static class CurveMath
{
    /// <summary>Positional tolerance, mm, for "same point" / "same circle" decisions.</summary>
    public const double Tolerance = 0.01;

    /// <summary>A part LENGTH further than this (mm) from the centerline length is flagged.</summary>
    public const double LengthMismatchTolerance = 0.5;

    private const double FullTurn = 2 * Math.PI;

    // ---------------------------------------------------------------- pieces ----

    /// <summary>A straight centerline piece.</summary>
    public static CurveSegmentInfo Line(Point3D start, Point3D end)
    {
        var length = Distance(start, end);
        return new CurveSegmentInfo
        {
            Kind = "line",
            Start = Copy(start),
            End = Copy(end),
            Length = length,
            ChordLength = length,
        };
    }

    /// <summary>
    /// Circular arc from <paramref name="start"/> through <paramref name="mid"/> to
    /// <paramref name="end"/>. <paramref name="mid"/> may be any point strictly between the ends;
    /// the result's Mid is always the angular midpoint. <paramref name="center"/> defaults to the
    /// circumcenter of the three points. <paramref name="normalHint"/> (Tekla's <c>Arc.Normal</c>)
    /// is used only when the points cannot orient the arc — a full circle, whose midpoint sits
    /// diametrically opposite its start. Returns null for degenerate input (coincident or
    /// collinear points) — callers fall back to <see cref="Line"/> and say so.
    /// </summary>
    public static CurveSegmentInfo? Arc(
        Point3D start, Point3D mid, Point3D end, Point3D? center = null, Point3D? normalHint = null)
    {
        var c = center ?? Circumcenter(start, mid, end);
        if (c == null) return null;

        var u = Sub(start, c);
        var radius = Norm(u);
        if (radius <= Tolerance) return null;

        var w = Sub(mid, c);
        var normal = Normalize(Cross(u, w));
        if (normal == null)
        {
            // mid coincides with start, or sits diametrically opposite (a full turn).
            return normalHint != null && Norm(w) > Tolerance
                ? ArcAround(c, start, end, normalHint)
                : null;
        }

        // Measured around the provisional normal, the interior point is at (0, π). If the end
        // comes BEFORE it, the arc actually turns the other way round.
        var q = Sub(end, c);
        double sweep;
        if (Distance(start, end) <= Tolerance)
        {
            sweep = FullTurn;
        }
        else
        {
            var toMid = PositiveAngle(u, w, normal);
            var toEnd = PositiveAngle(u, q, normal);
            if (toEnd >= toMid)
            {
                sweep = toEnd;
            }
            else
            {
                normal = Scale(normal, -1);
                sweep = FullTurn - toEnd;
            }
        }

        return BuildArc(c, start, end, radius, normal, sweep);
    }

    /// <summary>
    /// Circular arc around <paramref name="center"/> from <paramref name="start"/> to
    /// <paramref name="end"/>, turning right-handed about <paramref name="axis"/> (its sign picks
    /// the way round). For callers that know the rotation axis but have no interior point.
    /// </summary>
    public static CurveSegmentInfo? ArcAround(Point3D center, Point3D start, Point3D end, Point3D axis)
    {
        var normal = Normalize(axis);
        if (normal == null) return null;
        var u = Sub(start, center);
        var radius = Norm(u);
        if (radius <= Tolerance) return null;

        var sweep = Distance(start, end) <= Tolerance
            ? FullTurn
            : PositiveAngle(u, Sub(end, center), normal);
        if (sweep <= 0) return null;
        return BuildArc(center, start, end, radius, normal, sweep);
    }

    /// <summary>
    /// Compare the values Tekla reports for an arc (<c>Arc.Radius</c>, <c>Arc.Angle</c> in
    /// radians, <c>Arc.Length</c>) with the arc derived from its points. A disagreement means
    /// the Open API semantics differ from what this tool assumes on that Tekla version, so it
    /// must reach the caller instead of silently picking one.
    /// </summary>
    public static List<string> CheckReportedArc(
        CurveSegmentInfo arc,
        double? reportedRadius,
        double? reportedAngleRadians,
        double? reportedLength)
    {
        var warnings = new List<string>();
        if (arc.Kind != "arc" || !arc.Radius.HasValue || !arc.SweepAngle.HasValue) return warnings;

        var radius = arc.Radius.Value;
        var lengthTolerance = Math.Max(Tolerance, 1e-6 * radius);
        if (reportedRadius.HasValue && Math.Abs(reportedRadius.Value - radius) > lengthTolerance)
            warnings.Add(Invariant(
                $"Segment {arc.Index}: Tekla reports Arc.Radius={reportedRadius.Value:0.###} mm but its points give {radius:0.###} mm."));
        if (reportedAngleRadians.HasValue &&
            Math.Abs(reportedAngleRadians.Value - ToRadians(arc.SweepAngle.Value)) > 1e-5)
            warnings.Add(Invariant(
                $"Segment {arc.Index}: Tekla reports Arc.Angle={ToDegrees(reportedAngleRadians.Value):0.####}° but its points give {arc.SweepAngle.Value:0.####}°."));
        if (reportedLength.HasValue && Math.Abs(reportedLength.Value - arc.Length) > lengthTolerance)
            warnings.Add(Invariant(
                $"Segment {arc.Index}: Tekla reports Arc.Length={reportedLength.Value:0.###} mm but its points give {arc.Length:0.###} mm."));
        return warnings;
    }

    // ---------------------------------------------------------------- bends ----

    /// <summary>
    /// Merge consecutive arcs that continue on the same circle in the same direction into one
    /// bend. Tekla splits a bend at every contour point on it — an ARC_POINT bend comes back as
    /// two arcs — but a drawing dimensions the whole bend.
    /// </summary>
    public static List<CurveArcInfo> MergeArcs(IReadOnlyList<CurveSegmentInfo> segments)
    {
        var groups = new List<(CurveArcInfo Arc, double Sweep)>();
        CurveSegmentInfo? previous = null;

        foreach (var segment in segments)
        {
            if (!IsCompleteArc(segment))
            {
                previous = null;
                continue;
            }

            var sweep = ToRadians(segment.SweepAngle!.Value);
            if (previous != null && Continues(previous, segment) &&
                groups[groups.Count - 1].Sweep + sweep <= FullTurn + 1e-9)
            {
                var last = groups[groups.Count - 1];
                last.Arc.SegmentIndices.Add(segment.Index);
                last.Arc.End = Copy(segment.End);
                groups[groups.Count - 1] = (last.Arc, last.Sweep + sweep);
            }
            else
            {
                groups.Add((new CurveArcInfo
                {
                    SegmentIndices = new List<int> { segment.Index },
                    Start = Copy(segment.Start),
                    End = Copy(segment.End),
                    Center = Copy(segment.Center!),
                    Normal = Copy(segment.Normal!),
                    Radius = segment.Radius!.Value,
                }, sweep));
            }
            previous = segment;
        }

        return groups.Select(group => FinishBend(group.Arc, group.Sweep)).ToList();
    }

    /// <summary>
    /// The arc concentric with <paramref name="arc"/> at radius + <paramref name="delta"/>, in the
    /// same plane and sweep: delta = −D/2 gives the intrados of a round section, +D/2 the
    /// extrados. Null when the offset radius is not positive.
    /// </summary>
    public static ArcOffsetInfo? OffsetArc(CurveArcInfo arc, double delta)
    {
        var radius = arc.Radius + delta;
        if (arc.Radius <= Tolerance || radius <= Tolerance) return null;

        var k = radius / arc.Radius;
        Point3D Radial(Point3D p) => Add(arc.Center, Scale(Sub(p, arc.Center), k));
        return new ArcOffsetInfo
        {
            Radius = radius,
            Start = Radial(arc.Start),
            Mid = Radial(arc.Mid),
            End = Radial(arc.End),
            Length = arc.Length * k,
            ChordLength = arc.ChordLength * k,
            Sagitta = arc.Sagitta * k,
        };
    }

    /// <summary>
    /// Derive everything from <see cref="PartCurveGeometry.Segments"/>: indices, centerline
    /// length, merged bends, round-section inner/outer arcs, and a warning when the part's
    /// LENGTH says the solid is not what the centerline describes.
    /// </summary>
    public static void Complete(PartCurveGeometry geometry)
    {
        for (var i = 0; i < geometry.Segments.Count; i++)
            geometry.Segments[i].Index = i;
        geometry.CenterlineLength = geometry.Segments.Count > 0
            ? geometry.Segments.Sum(segment => segment.Length)
            : (double?)null;
        geometry.Arcs = MergeArcs(geometry.Segments);

        var section = geometry.Section;
        if (section != null && section.Shape == "round" && section.Diameter.HasValue && section.Diameter.Value > 0)
        {
            var half = section.Diameter.Value / 2;
            foreach (var arc in geometry.Arcs)
            {
                arc.Inner = OffsetArc(arc, -half);
                arc.Outer = OffsetArc(arc, half);
                if (arc.Inner == null)
                    geometry.Warnings.Add(Invariant(
                        $"Bend at segment(s) {string.Join(",", arc.SegmentIndices)}: centerline radius {arc.Radius:0.###} mm is not larger than half the diameter ({half:0.###} mm); no inner arc."));
            }
        }

        if (geometry.ReportedLength.HasValue && geometry.CenterlineLength.HasValue &&
            Math.Abs(geometry.ReportedLength.Value - geometry.CenterlineLength.Value) > LengthMismatchTolerance)
        {
            geometry.Warnings.Add(
                "The part's LENGTH is " + Mm(geometry.ReportedLength.Value) + " mm but its centerline measures " +
                Mm(geometry.CenterlineLength.Value) + " mm: cuts, fittings, boolean parts or end offsets change " +
                "the solid and are NOT part of this centerline. Radii and bends are unaffected; lengths and end points " +
                "of the first/last segments may not match the drawn part.");
        }
    }

    // ---------------------------------------------------------------- view ----

    /// <summary>
    /// Copy <see cref="PartCurveGeometry.Segments"/> and <see cref="PartCurveGeometry.Arcs"/>
    /// through <paramref name="transform"/> (model → view). Lengths, radii and angles are
    /// invariant under the rigid view transform and are copied; normals are recomputed from the
    /// transformed points so they stay right-handed start → mid → end even in a mirrored view.
    /// </summary>
    public static CurveViewProjection Project(PartCurveGeometry geometry, Func<Point3D, Point3D> transform)
    {
        return new CurveViewProjection
        {
            Segments = geometry.Segments.Select(segment => ProjectSegment(segment, transform)).ToList(),
            Arcs = geometry.Arcs.Select(arc => ProjectArc(arc, transform)).ToList(),
        };
    }

    /// <summary>
    /// Global → local mapping for a coordinate system given as origin + X/Y axes (Z = X × Y),
    /// the same transform as Tekla's <c>MatrixFactory.ByCoordinateSystems(global, cs)</c> for an
    /// orthonormal system. Null when the axes are degenerate.
    /// </summary>
    public static Func<Point3D, Point3D>? ToLocal(CoordinateSystemInfo? coordinateSystem)
    {
        if (coordinateSystem == null) return null;
        var x = Normalize(coordinateSystem.AxisX);
        var yRaw = Normalize(coordinateSystem.AxisY);
        if (x == null || yRaw == null) return null;
        var z = Normalize(Cross(x, yRaw));
        if (z == null) return null;
        var y = Cross(z, x);
        var origin = Copy(coordinateSystem.Origin);
        return p =>
        {
            var d = Sub(p, origin);
            return new Point3D(Dot(d, x), Dot(d, y), Dot(d, z));
        };
    }

    // ---------------------------------------------------------------- output ----

    /// <summary>
    /// Round every number for output: coordinates and lengths to 0.001 mm, angles to 1e-6°,
    /// unit vectors to 1e-6. Call once, after <see cref="Complete"/> and any projection — the
    /// computation itself runs unrounded.
    /// </summary>
    public static void Round(PartCurveGeometry geometry)
    {
        geometry.CenterlineLength = RoundLength(geometry.CenterlineLength);
        geometry.ReportedLength = RoundLength(geometry.ReportedLength);
        if (geometry.Section != null)
            geometry.Section.Diameter = RoundLength(geometry.Section.Diameter);
        foreach (var segment in geometry.Segments) RoundSegment(segment);
        foreach (var arc in geometry.Arcs) RoundArc(arc);
        foreach (var point in geometry.Contour)
        {
            point.Point = RoundPoint(point.Point);
            point.ChamferX = RoundLength(point.ChamferX);
            point.ChamferY = RoundLength(point.ChamferY);
            point.ChamferDz1 = RoundLength(point.ChamferDz1);
            point.ChamferDz2 = RoundLength(point.ChamferDz2);
        }
        if (geometry.View != null)
        {
            foreach (var segment in geometry.View.Segments) RoundSegment(segment);
            foreach (var arc in geometry.View.Arcs) RoundArc(arc);
        }
    }

    /// <summary>Tekla chamfer enum name → output name: CHAMFER_ARC_POINT → arc_point.</summary>
    public static string NormalizeChamferType(string? teklaName)
    {
        var name = (teklaName ?? "").Trim();
        if (name.Length == 0) return "none";
        if (name.StartsWith("CHAMFER_", StringComparison.OrdinalIgnoreCase))
            name = name.Substring("CHAMFER_".Length);
        return name.ToLowerInvariant();
    }

    /// <summary>PROFILE_TYPE of a round section: RO (tube) or RU (solid bar).</summary>
    public static bool IsRoundProfileType(string? profileType)
    {
        var type = (profileType ?? "").Trim();
        return string.Equals(type, "RO", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "RU", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- vectors ----

    public static double Distance(Point3D a, Point3D b) => Norm(Sub(a, b));

    public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    public static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

    /// <summary>Circumcenter of three points; null when they are (nearly) collinear.</summary>
    public static Point3D? Circumcenter(Point3D a, Point3D b, Point3D c)
    {
        var u = Sub(b, a);
        var v = Sub(c, a);
        var w = Cross(u, v);
        var w2 = Dot(w, w);
        if (w2 <= 1e-24 * Dot(u, u) * Dot(v, v) || w2 == 0) return null;
        var numerator = Add(Scale(Cross(v, w), Dot(u, u)), Scale(Cross(w, u), Dot(v, v)));
        return Add(a, Scale(numerator, 1.0 / (2 * w2)));
    }

    private static Point3D Copy(Point3D p) => new Point3D(p.X, p.Y, p.Z);

    private static Point3D Add(Point3D a, Point3D b) => new Point3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static Point3D Sub(Point3D a, Point3D b) => new Point3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static Point3D Scale(Point3D a, double k) => new Point3D(a.X * k, a.Y * k, a.Z * k);

    private static double Dot(Point3D a, Point3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Point3D Cross(Point3D a, Point3D b) => new Point3D(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    private static double Norm(Point3D a) => Math.Sqrt(Dot(a, a));

    private static Point3D? Normalize(Point3D a)
    {
        var length = Norm(a);
        return length <= 1e-12 ? null : Scale(a, 1.0 / length);
    }

    /// <summary>Angle from a to b turning right-handed about unit axis n, in [0, 2π).</summary>
    private static double PositiveAngle(Point3D a, Point3D b, Point3D n)
    {
        var angle = Math.Atan2(Dot(n, Cross(a, b)), Dot(a, b));
        return angle < 0 ? angle + FullTurn : angle;
    }

    /// <summary>center + radius·(cos φ·û + sin φ·(n × û)), û the unit start direction.</summary>
    private static Point3D Rotate(Point3D center, Point3D start, Point3D normal, double angle)
    {
        var radial = Sub(start, center);
        var radius = Norm(radial);
        var u = Scale(radial, 1.0 / radius);
        var v = Cross(normal, u);
        return Add(center, Add(Scale(u, radius * Math.Cos(angle)), Scale(v, radius * Math.Sin(angle))));
    }

    private static CurveSegmentInfo BuildArc(
        Point3D center, Point3D start, Point3D end, double radius, Point3D normal, double sweep)
    {
        return new CurveSegmentInfo
        {
            Kind = "arc",
            Start = Copy(start),
            End = Copy(end),
            Mid = Rotate(center, start, normal, sweep / 2),
            Center = Copy(center),
            Normal = normal,
            Radius = radius,
            SweepAngle = ToDegrees(sweep),
            Length = radius * sweep,
            ChordLength = Distance(start, end),
            Sagitta = radius * (1 - Math.Cos(sweep / 2)),
        };
    }

    private static CurveArcInfo FinishBend(CurveArcInfo arc, double sweep)
    {
        arc.SweepAngle = ToDegrees(sweep);
        arc.Mid = Rotate(arc.Center, arc.Start, arc.Normal, sweep / 2);
        arc.Length = arc.Radius * sweep;
        arc.ChordLength = Distance(arc.Start, arc.End);
        arc.Sagitta = arc.Radius * (1 - Math.Cos(sweep / 2));
        return arc;
    }

    private static bool IsCompleteArc(CurveSegmentInfo segment) =>
        segment.Kind == "arc" && segment.Center != null && segment.Normal != null &&
        segment.Radius.HasValue && segment.SweepAngle.HasValue;

    private static bool Continues(CurveSegmentInfo previous, CurveSegmentInfo next)
    {
        var tolerance = Math.Max(Tolerance, 1e-6 * previous.Radius!.Value);
        return Distance(previous.End, next.Start) <= tolerance &&
               Distance(previous.Center!, next.Center!) <= tolerance &&
               Math.Abs(previous.Radius.Value - next.Radius!.Value) <= tolerance &&
               Dot(previous.Normal!, next.Normal!) >= 1 - 1e-6;
    }

    private static Point3D? RecomputedNormal(Point3D center, Point3D start, Point3D mid) =>
        Normalize(Cross(Sub(start, center), Sub(mid, center)));

    private static CurveSegmentInfo ProjectSegment(CurveSegmentInfo segment, Func<Point3D, Point3D> transform)
    {
        var projected = new CurveSegmentInfo
        {
            Index = segment.Index,
            Kind = segment.Kind,
            Start = transform(segment.Start),
            End = transform(segment.End),
            Length = segment.Length,
            ChordLength = segment.ChordLength,
            Mid = segment.Mid == null ? null : transform(segment.Mid),
            Center = segment.Center == null ? null : transform(segment.Center),
            Radius = segment.Radius,
            SweepAngle = segment.SweepAngle,
            Sagitta = segment.Sagitta,
        };
        if (projected.Center != null && projected.Mid != null)
            projected.Normal = RecomputedNormal(projected.Center, projected.Start, projected.Mid);
        return projected;
    }

    private static CurveArcInfo ProjectArc(CurveArcInfo arc, Func<Point3D, Point3D> transform)
    {
        var projected = new CurveArcInfo
        {
            SegmentIndices = arc.SegmentIndices.ToList(),
            Start = transform(arc.Start),
            Mid = transform(arc.Mid),
            End = transform(arc.End),
            Center = transform(arc.Center),
            Radius = arc.Radius,
            SweepAngle = arc.SweepAngle,
            Length = arc.Length,
            ChordLength = arc.ChordLength,
            Sagitta = arc.Sagitta,
            Inner = ProjectOffset(arc.Inner, transform),
            Outer = ProjectOffset(arc.Outer, transform),
        };
        projected.Normal = RecomputedNormal(projected.Center, projected.Start, projected.Mid) ?? Copy(arc.Normal);
        return projected;
    }

    private static ArcOffsetInfo? ProjectOffset(ArcOffsetInfo? offset, Func<Point3D, Point3D> transform) =>
        offset == null
            ? null
            : new ArcOffsetInfo
            {
                Radius = offset.Radius,
                Start = transform(offset.Start),
                Mid = transform(offset.Mid),
                End = transform(offset.End),
                Length = offset.Length,
                ChordLength = offset.ChordLength,
                Sagitta = offset.Sagitta,
            };

    private static void RoundSegment(CurveSegmentInfo segment)
    {
        segment.Start = RoundPoint(segment.Start);
        segment.End = RoundPoint(segment.End);
        segment.Mid = segment.Mid == null ? null : RoundPoint(segment.Mid);
        segment.Center = segment.Center == null ? null : RoundPoint(segment.Center);
        segment.Normal = segment.Normal == null ? null : RoundUnit(segment.Normal);
        segment.Length = Math.Round(segment.Length, 3);
        segment.ChordLength = Math.Round(segment.ChordLength, 3);
        segment.Radius = RoundLength(segment.Radius);
        segment.SweepAngle = segment.SweepAngle.HasValue ? Math.Round(segment.SweepAngle.Value, 6) : (double?)null;
        segment.Sagitta = RoundLength(segment.Sagitta);
    }

    private static void RoundArc(CurveArcInfo arc)
    {
        arc.Start = RoundPoint(arc.Start);
        arc.Mid = RoundPoint(arc.Mid);
        arc.End = RoundPoint(arc.End);
        arc.Center = RoundPoint(arc.Center);
        arc.Normal = RoundUnit(arc.Normal);
        arc.Radius = Math.Round(arc.Radius, 3);
        arc.SweepAngle = Math.Round(arc.SweepAngle, 6);
        arc.Length = Math.Round(arc.Length, 3);
        arc.ChordLength = Math.Round(arc.ChordLength, 3);
        arc.Sagitta = Math.Round(arc.Sagitta, 3);
        RoundOffset(arc.Inner);
        RoundOffset(arc.Outer);
    }

    private static void RoundOffset(ArcOffsetInfo? offset)
    {
        if (offset == null) return;
        offset.Radius = Math.Round(offset.Radius, 3);
        offset.Start = RoundPoint(offset.Start);
        offset.Mid = RoundPoint(offset.Mid);
        offset.End = RoundPoint(offset.End);
        offset.Length = Math.Round(offset.Length, 3);
        offset.ChordLength = Math.Round(offset.ChordLength, 3);
        offset.Sagitta = Math.Round(offset.Sagitta, 3);
    }

    private static Point3D RoundPoint(Point3D p) =>
        new Point3D(Clean(Math.Round(p.X, 3)), Clean(Math.Round(p.Y, 3)), Clean(Math.Round(p.Z, 3)));

    private static Point3D RoundUnit(Point3D p) =>
        new Point3D(Clean(Math.Round(p.X, 6)), Clean(Math.Round(p.Y, 6)), Clean(Math.Round(p.Z, 6)));

    private static double? RoundLength(double? value) =>
        value.HasValue ? Math.Round(value.Value, 3) : (double?)null;

    /// <summary>Turn −0 into 0 so rounded output never prints "-0".</summary>
    private static double Clean(double value) => value == 0 ? 0 : value;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Mm(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
