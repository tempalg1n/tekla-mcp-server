using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Covers the geometry behind tekla_get_part_curve_geometry (issue #15). The "live" fixtures are
/// real PolyBeam.GetCenterLinePolycurve() arcs read from a live Tekla 2023 project model and moved
/// to a local origin (the geometry is unchanged, the project coordinates are not published): a ROUNDING
/// bend of an I30M monorail, an ARC_POINT bend that Tekla returns as two quarter arcs, and a
/// bent D30 anchor bar whose boolean part makes LENGTH 100 mm longer than its centerline.
/// </summary>
public class CurveMathTests
{
    private static Point3D P(double x, double y, double z) => new Point3D(x, y, z);

    private static void AssertPoint(Point3D expected, Point3D? actual, int precision = 3)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.X, actual!.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }

    [Fact]
    public void Quarter_arc_from_three_points()
    {
        var s = Math.Sqrt(0.5) * 1000;
        var arc = CurveMath.Arc(P(1000, 0, 0), P(s, s, 0), P(0, 1000, 0));

        Assert.NotNull(arc);
        Assert.Equal("arc", arc!.Kind);
        AssertPoint(P(0, 0, 0), arc.Center);
        AssertPoint(P(0, 0, 1), arc.Normal, 9);
        Assert.Equal(1000, arc.Radius!.Value, 6);
        Assert.Equal(90, arc.SweepAngle!.Value, 9);
        Assert.Equal(500 * Math.PI, arc.Length, 6);
        Assert.Equal(1000 * Math.Sqrt(2), arc.ChordLength, 6);
        Assert.Equal(1000 * (1 - Math.Cos(Math.PI / 4)), arc.Sagitta!.Value, 6);
        AssertPoint(P(s, s, 0), arc.Mid, 6);
    }

    [Fact]
    public void Interior_point_off_the_midpoint_still_gives_the_long_way_round()
    {
        // 0° → 270° counter-clockwise, passing 200°. The short way from start to that interior
        // point is clockwise, so the provisional normal is wrong and must be flipped.
        double Deg(double d) => d * Math.PI / 180;
        var interior = P(1000 * Math.Cos(Deg(200)), 1000 * Math.Sin(Deg(200)), 0);
        var arc = CurveMath.Arc(P(1000, 0, 0), interior, P(0, -1000, 0), center: P(0, 0, 0));

        Assert.NotNull(arc);
        Assert.Equal(270, arc!.SweepAngle!.Value, 9);
        AssertPoint(P(0, 0, 1), arc.Normal, 9);
        AssertPoint(P(-Math.Sqrt(0.5) * 1000, Math.Sqrt(0.5) * 1000, 0), arc.Mid, 6);
        Assert.Equal(1000 * (1 - Math.Cos(Deg(135))), arc.Sagitta!.Value, 6); // > radius past 180°
    }

    [Fact]
    public void Collinear_points_are_not_an_arc()
    {
        Assert.Null(CurveMath.Arc(P(0, 0, 0), P(500, 0, 0), P(1000, 0, 0)));
    }

    [Fact]
    public void Full_circle_needs_the_normal_hint()
    {
        // Start = end and the midpoint diametrically opposite: the points alone cannot orient it.
        Assert.Null(CurveMath.Arc(P(500, 0, 0), P(-500, 0, 0), P(500, 0, 0), center: P(0, 0, 0)));

        var circle = CurveMath.Arc(P(500, 0, 0), P(-500, 0, 0), P(500, 0, 0), center: P(0, 0, 0), normalHint: P(0, 0, 1));

        Assert.NotNull(circle);
        Assert.Equal(360, circle!.SweepAngle!.Value, 9);
        Assert.Equal(1000 * Math.PI, circle.Length, 6);
        Assert.Equal(0, circle.ChordLength, 9);
        Assert.Equal(1000, circle.Sagitta!.Value, 6);
        AssertPoint(P(-500, 0, 0), circle.Mid, 6);
    }

    [Fact]
    public void Live_monorail_arc_matches_what_tekla_reports()
    {
        // I30M monorail bend, CHAMFER_ROUNDING X=1100.
        var arc = CurveMath.Arc(
            P(1310.001, 3470.0042, 625),
            P(2087.8184, 3147.8216, 625),
            P(2410.0009, 2370.0042, 625),
            center: P(1310.0009, 2370.0042, 625));

        Assert.NotNull(arc);
        Assert.Equal(1100, arc!.Radius!.Value, 3);
        Assert.Equal(90, arc.SweepAngle!.Value, 4);
        AssertPoint(P(0, 0, -1), arc.Normal, 6);
        Assert.Empty(CurveMath.CheckReportedArc(arc, 1099.9999999978725, 1.5707962944823091, 1727.8759239271981));
    }

    [Fact]
    public void Reported_values_that_disagree_are_surfaced()
    {
        var arc = CurveMath.Arc(P(1000, 0, 0), P(Math.Sqrt(0.5) * 1000, Math.Sqrt(0.5) * 1000, 0), P(0, 1000, 0))!;

        // An Angle reported in degrees instead of radians must not pass silently.
        var warnings = CurveMath.CheckReportedArc(arc, 1000, 90, 500 * Math.PI);

        var warning = Assert.Single(warnings);
        Assert.Contains("Arc.Angle", warning);
    }

    [Fact]
    public void Arc_point_bend_split_in_two_by_tekla_is_merged_into_one()
    {
        // I30M monorail U-bend: contour p1 → p2 (CHAMFER_ARC_POINT) → p3 comes
        // back from GetCenterLinePolycurve as two 90° arcs meeting at the arc point.
        var center = P(7950.0007, 2369.9995, 625);
        var geometry = new PartCurveGeometry
        {
            Segments =
            {
                CurveMath.Line(P(8700.0007, 3469.9999, 625), P(7950.0007, 3469.9999, 625)),
                CurveMath.Arc(P(7950.0007, 3469.9999, 625), P(7172.183, 3147.8173, 625), P(6850.0004, 2369.9995, 625), center)!,
                CurveMath.Arc(P(6850.0004, 2369.9995, 625), P(7172.183, 1592.1818, 625), P(7950.0007, 1269.9992, 625), center)!,
                CurveMath.Line(P(7950.0007, 1269.9992, 625), P(8700.0007, 1269.9992, 625)),
            },
        };

        CurveMath.Complete(geometry);

        var bend = Assert.Single(geometry.Arcs);
        Assert.Equal(new[] { 1, 2 }, bend.SegmentIndices);
        Assert.Equal(180, bend.SweepAngle, 3);
        Assert.Equal(1100, bend.Radius, 2);
        Assert.Equal(1100 * Math.PI, bend.Length, 1);
        Assert.Equal(2200, bend.ChordLength, 2);
        Assert.Equal(1100, bend.Sagitta, 2);
        AssertPoint(P(6850.0004, 2369.9995, 625), bend.Mid, 2); // the modelled arc point
        Assert.Equal(750 + 1100 * Math.PI + 750, geometry.CenterlineLength!.Value, 1);
    }

    [Fact]
    public void Separate_bends_and_s_curves_stay_separate()
    {
        // Same radius, contiguous, but a different center: an S-curve is two bends.
        var geometry = new PartCurveGeometry
        {
            Segments =
            {
                CurveMath.ArcAround(P(0, 0, 0), P(1000, 0, 0), P(0, 1000, 0), P(0, 0, 1))!,
                CurveMath.ArcAround(P(0, 2000, 0), P(0, 1000, 0), P(-1000, 2000, 0), P(0, 0, -1))!,
                CurveMath.Line(P(-1000, 2000, 0), P(-1000, 2016, 0)),
                CurveMath.ArcAround(P(0, 2016, 0), P(-1000, 2016, 0), P(0, 3016, 0), P(0, 0, -1))!,
            },
        };

        CurveMath.Complete(geometry);

        Assert.Equal(3, geometry.Arcs.Count);
        Assert.All(geometry.Arcs, bend => Assert.Equal(90, bend.SweepAngle, 9));
        Assert.Equal(new[] { 3 }, geometry.Arcs[2].SegmentIndices);
    }

    [Fact]
    public void Round_section_gets_inner_and_outer_arcs_and_boolean_length_is_flagged()
    {
        // Bent D30 anchor bar, centerline bend radius 58, with a boolean part on its top end.
        var geometry = new PartCurveGeometry
        {
            Section = new CurveSectionInfo { Shape = "round", Diameter = 30, Source = "PROFILE.DIAMETER" },
            ReportedLength = 1480.1061869540958,
            Segments =
            {
                CurveMath.Line(P(1680, 3900, -418), P(1680, 3900, -1645)),
                CurveMath.Arc(P(1680, 3900, -1645), P(1680, 3883.0122, -1686.0122), P(1680, 3842, -1703), P(1680, 3842, -1645))!,
                CurveMath.Line(P(1680, 3842, -1703), P(1680, 3780, -1703)),
            },
        };

        CurveMath.Complete(geometry);

        var bend = Assert.Single(geometry.Arcs);
        Assert.Equal(58, bend.Radius, 3);
        var inner = bend.Inner!;
        var outer = bend.Outer!;
        Assert.Equal(43, inner.Radius, 6);
        Assert.Equal(73, outer.Radius, 6);
        AssertPoint(P(1680, 3885, -1645), inner.Start);
        AssertPoint(P(1680, 3842, -1688), inner.End);
        Assert.Equal(43 * Math.PI / 2, inner.Length, 3);
        Assert.Equal(43 * Math.Sqrt(2), inner.ChordLength, 3);
        Assert.Equal(43 * (1 - Math.Cos(Math.PI / 4)), inner.Sagitta, 3);
        Assert.Equal(73 * Math.PI / 2, outer.Length, 3);

        Assert.Equal(1380.106, geometry.CenterlineLength!.Value, 3);
        var warning = Assert.Single(geometry.Warnings);
        Assert.Contains("boolean parts", warning);
        Assert.Contains("1480.106", warning); // invariant culture: a point, never a comma
    }

    [Fact]
    public void Bend_tighter_than_the_section_has_no_inner_arc()
    {
        var geometry = new PartCurveGeometry
        {
            Section = new CurveSectionInfo { Shape = "round", Diameter = 40 },
            Segments = { CurveMath.ArcAround(P(0, 0, 0), P(15, 0, 0), P(0, 15, 0), P(0, 0, 1))! },
        };

        CurveMath.Complete(geometry);

        var bend = Assert.Single(geometry.Arcs);
        Assert.Null(bend.Inner);
        Assert.NotNull(bend.Outer);
        Assert.Contains("not larger than half the diameter", Assert.Single(geometry.Warnings));
    }

    [Fact]
    public void Non_round_section_gets_no_offsets()
    {
        var geometry = new PartCurveGeometry
        {
            Section = new CurveSectionInfo { Shape = "other" },
            Segments = { CurveMath.ArcAround(P(0, 0, 0), P(1000, 0, 0), P(0, 1000, 0), P(0, 0, 1))! },
        };

        CurveMath.Complete(geometry);

        var bend = Assert.Single(geometry.Arcs);
        Assert.Null(bend.Inner);
        Assert.Null(bend.Outer);
        Assert.Empty(geometry.Warnings);
    }

    [Fact]
    public void Projection_into_a_front_view_keeps_measures_and_recomputes_the_normal()
    {
        // View X = global X, view Y = global Z, depth Z = X × Y = −global Y.
        var toView = CurveMath.ToLocal(new CoordinateSystemInfo
        {
            Origin = P(0, 0, 0),
            AxisX = P(1, 0, 0),
            AxisY = P(0, 0, 1),
        })!;
        var geometry = new PartCurveGeometry
        {
            Section = new CurveSectionInfo { Shape = "round", Diameter = 168.3 },
            Segments =
            {
                CurveMath.ArcAround(P(3000, 3000, 0), P(0, 3000, 4000), P(3000, 3000, 5000), P(0, 1, 0))!,
                CurveMath.ArcAround(P(3000, 3000, 0), P(3000, 3000, 5000), P(6000, 3000, 4000), P(0, 1, 0))!,
            },
        };
        CurveMath.Complete(geometry);

        var view = CurveMath.Project(geometry, toView);

        var bend = Assert.Single(view.Arcs);
        AssertPoint(P(0, 4000, -3000), bend.Start);
        AssertPoint(P(3000, 5000, -3000), bend.Mid);
        AssertPoint(P(6000, 4000, -3000), bend.End);
        AssertPoint(P(3000, 0, -3000), bend.Center);
        AssertPoint(P(0, 0, -1), bend.Normal, 9);
        Assert.Equal(geometry.Arcs[0].Length, bend.Length, 9);
        Assert.Equal(5000 - 168.3 / 2, bend.Inner!.Radius, 9);
        AssertPoint(P(3000, 5000 - 168.3 / 2, -3000), bend.Inner.Mid);
        Assert.Equal(2, view.Segments.Count);
    }

    [Fact]
    public void Projection_through_a_mirror_keeps_the_normal_right_handed()
    {
        var geometry = new PartCurveGeometry
        {
            Segments = { CurveMath.ArcAround(P(0, 0, 0), P(1000, 0, 0), P(0, 1000, 0), P(0, 0, 1))! },
        };
        CurveMath.Complete(geometry);

        var mirrored = CurveMath.Project(geometry, p => P(-p.X, p.Y, p.Z));

        AssertPoint(P(0, 0, -1), mirrored.Arcs[0].Normal, 9);
        AssertPoint(P(0, 0, -1), mirrored.Segments[0].Normal, 9);
    }

    [Fact]
    public void Degenerate_coordinate_system_has_no_transform()
    {
        Assert.Null(CurveMath.ToLocal(null));
        Assert.Null(CurveMath.ToLocal(new CoordinateSystemInfo { AxisX = P(1, 0, 0), AxisY = P(2, 0, 0) }));
    }

    [Fact]
    public void Rounding_trims_noise_and_negative_zero()
    {
        var geometry = new PartCurveGeometry
        {
            Segments = { CurveMath.Line(P(-0.0000001, 1.23456789, 0), P(10, 0, 0)) },
            Contour = { new ContourPointInfo { Point = P(1.00049, -0.0000004, 2), ChamferX = 57.99999999 } },
        };
        CurveMath.Complete(geometry);

        CurveMath.Round(geometry);

        var segment = Assert.Single(geometry.Segments);
        Assert.Equal(0, segment.Start.X);
        Assert.False(double.IsNegative(segment.Start.X));
        Assert.Equal(1.235, segment.Start.Y);
        Assert.Equal(1, geometry.Contour[0].Point.X);
        Assert.False(double.IsNegative(geometry.Contour[0].Point.Y));
        Assert.Equal(58, geometry.Contour[0].ChamferX);
    }

    [Theory]
    [InlineData("CHAMFER_ROUNDING", "rounding")]
    [InlineData("CHAMFER_ARC_POINT", "arc_point")]
    [InlineData("CHAMFER_NONE", "none")]
    [InlineData("", "none")]
    [InlineData(null, "none")]
    public void Chamfer_names_are_normalized(string? tekla, string expected)
    {
        Assert.Equal(expected, CurveMath.NormalizeChamferType(tekla));
    }

    [Theory]
    [InlineData("RO", true)]
    [InlineData("ru", true)]
    [InlineData(" RO ", true)]
    [InlineData("I", false)]
    [InlineData("M", false)]
    [InlineData(null, false)]
    public void Round_profile_types(string? profileType, bool round)
    {
        Assert.Equal(round, CurveMath.IsRoundProfileType(profileType));
    }
}
