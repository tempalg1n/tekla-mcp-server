using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Dimension conventions applied before Tekla sees the request: paper mm → view units for
/// straight dimensions (issue #26) and the clockwise point order of radius dimensions (issue #27).
/// </summary>
public class DimensionMathTests
{
    private static Point3D P(double x, double y) => new Point3D(x, y, 0);

    private static Point3D OnCircle(double radius, double degrees) =>
        P(radius * Math.Cos(degrees * Math.PI / 180), radius * Math.Sin(degrees * Math.PI / 180));

    [Theory]
    [InlineData(8, 50, 400)]   // the issue #26 field case: 8 paper mm at 1:50
    [InlineData(8, 100, 800)]
    [InlineData(10, 1, 10)]    // the sheet
    [InlineData(10, 0, 10)]    // unknown scale counts as 1, never as 0
    public void Paper_distance_becomes_view_units(double paper, double scale, double expected) =>
        Assert.Equal(expected, DimensionMath.PaperToViewDistance(paper, scale), 9);

    [Fact]
    public void Counter_clockwise_arc_is_reversed()
    {
        // The issue #27 field case: R1715 running counter-clockwise from start to end.
        var start = OnCircle(1715, 10);
        var mid = OnCircle(1715, 55);
        var end = OnCircle(1715, 100);

        var ordered = DimensionMath.OrderArcClockwise(start, mid, end, out var reversed);

        Assert.True(reversed);
        Assert.NotNull(ordered);
        Assert.Same(end, ordered![0]);
        Assert.Same(mid, ordered[1]);
        Assert.Same(start, ordered[2]);
    }

    [Fact]
    public void Clockwise_arc_is_kept()
    {
        var start = OnCircle(2500, 100);
        var mid = OnCircle(2500, 55);
        var end = OnCircle(2500, 10);

        var ordered = DimensionMath.OrderArcClockwise(start, mid, end, out var reversed);

        Assert.False(reversed);
        Assert.Equal(new[] { start, mid, end }, ordered);
    }

    [Fact]
    public void Arc_longer_than_a_half_circle_keeps_its_middle_point()
    {
        // 0° → 270° counter-clockwise through 135°: clockwise from 270° to 0° through 135°.
        var ordered = DimensionMath.OrderArcClockwise(
            OnCircle(1000, 0), OnCircle(1000, 135), OnCircle(1000, 270), out var reversed);

        Assert.True(reversed);
        Assert.Equal(270, Math.Round(Math.Atan2(ordered![0].Y, ordered[0].X) * 180 / Math.PI + 360) % 360);
    }

    [Theory]
    [InlineData(0, 0, 50, 0, 100, 0)]       // collinear
    [InlineData(0, 0, 0, 0, 100, 0)]        // coincident
    [InlineData(0, 0, 50, 0.001, 100, 0)]   // a straight line as far as a drawing is concerned
    public void Points_that_span_no_arc_are_refused(
        double x1, double y1, double x2, double y2, double x3, double y3)
    {
        Assert.Null(DimensionMath.OrderArcClockwise(P(x1, y1), P(x2, y2), P(x3, y3), out var reversed));
        Assert.False(reversed);
    }

    [Fact]
    public void Mock_radius_dimension_reorders_like_the_live_backend()
    {
        var model = new MockTeklaModelService();
        var spec = new DrawingObjectSpec
        {
            Kind = "radius_dimension",
            ViewId = 11001,
            ViewId2 = 0,
            Points = new List<Point3D> { OnCircle(1715, 10), OnCircle(1715, 55), OnCircle(1715, 100) },
        };

        var preview = model.CreateDrawingObjects(new[] { spec }, apply: false);

        var item = Assert.Single(preview.ObjectPreview);
        Assert.Equal(100, Math.Round(Math.Atan2(item.Points[0].Y, item.Points[0].X) * 180 / Math.PI));
        Assert.Contains(preview.Warnings, w => w.Contains("swapped"));
    }

    [Fact]
    public void Straight_dimension_reports_its_distance_in_both_units()
    {
        var model = new MockTeklaModelService();
        var view = model.GetDrawingViews().First(v => v.ViewId == 11001);
        var spec = new DrawingObjectSpec
        {
            Kind = "straight_dimension", ViewId = 11001, ViewId2 = 0, Distance = 8, Placing = "fixed",
            Points = new List<Point3D> { P(0, 0), P(1000, 0) },
        };

        var created = Assert.Single(model.CreateDrawingObjects(new[] { spec }, apply: true).ObjectPreview);

        Assert.Equal(8, created.DimensionDistancePaperMm);
        Assert.Equal(8 * (view.Scale ?? 1), created.DimensionDistance);
        Assert.Equal("Fixed", created.DimensionPlacing);
    }

    [Fact]
    public void Mock_radius_dimension_on_a_straight_line_is_an_error()
    {
        var model = new MockTeklaModelService();
        var spec = new DrawingObjectSpec
        {
            Kind = "radius_dimension",
            ViewId = 11001,
            ViewId2 = 0,
            Points = new List<Point3D> { P(0, 0), P(50, 0), P(100, 0) },
        };

        var result = model.CreateDrawingObjects(new[] { spec }, apply: true);

        Assert.Equal(0, result.CreatedCount);
        Assert.Contains(result.Errors, e => e.Contains("straight line"));
    }
}
