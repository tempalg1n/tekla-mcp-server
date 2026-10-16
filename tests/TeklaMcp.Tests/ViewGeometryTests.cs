using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>Parts' and bolts' geometry in view coordinates and its filters (issue #38).</summary>
public class ViewGeometryTests
{
    private static Point3D P(double x, double y, double z) => new Point3D(x, y, z);

    private static DrawingModelGeometry Plate() => new DrawingModelGeometry
    {
        Name = "PLATE", Profile = "PL6*240", Min = P(0, 0, -3), Max = P(240, 120, 3), Source = "solid vertices",
    };

    [Fact]
    public void Box_comes_from_the_points_given()
    {
        Assert.True(ViewGeometry.TryBox(new[] { P(1, 5, -3), P(241, 6, 21), P(120, 0, 0) }, out var min, out var max));
        Assert.Equal((1.0, 0.0, -3.0), (min.X, min.Y, min.Z));
        Assert.Equal((241.0, 6.0, 21.0), (max.X, max.Y, max.Z));
        Assert.False(ViewGeometry.TryBox(new List<Point3D>(), out _, out _));
    }

    [Fact]
    public void Depth_and_region_filters_need_overlap()
    {
        Assert.True(ViewGeometry.Matches(Plate(), new DrawingObjectQuery { ZMin = 0, ZMax = 10 }));
        Assert.False(ViewGeometry.Matches(Plate(), new DrawingObjectQuery { ZMin = 10, ZMax = 20 }));
        Assert.True(ViewGeometry.Matches(Plate(), new DrawingObjectQuery { RegionMin = P(200, 100, 0), RegionMax = P(300, 300, 0) }));
        Assert.False(ViewGeometry.Matches(Plate(), new DrawingObjectQuery { RegionMin = P(250, 0, 0), RegionMax = P(300, 300, 0) }));
        Assert.True(ViewGeometry.Matches(Plate(), new DrawingObjectQuery { ProfileContains = "pl6" }));
        Assert.False(ViewGeometry.Matches(null, new DrawingObjectQuery { ZMin = 0 }));
        Assert.True(ViewGeometry.Matches(null, new DrawingObjectQuery()));
    }

    [Fact]
    public void Bolts_keep_only_positions_inside_the_depth_range()
    {
        // The field case: bolts on a column web deeper in the section leaked into a plate's chain.
        var bolts = new DrawingModelGeometry
        {
            BoltPositions = new List<Point3D> { P(0, 0, 2), P(80, 0, 2), P(0, 0, -150) },
            Min = P(0, 0, -150), Max = P(80, 0, 2), Source = "bolt positions",
        };

        Assert.True(ViewGeometry.Matches(bolts, new DrawingObjectQuery { ZMin = -3, ZMax = 3 }));
        Assert.Equal(2, bolts.BoltPositions!.Count);
        Assert.Equal(2, bolts.Min!.Z);

        var deep = new DrawingModelGeometry { BoltPositions = new List<Point3D> { P(0, 0, -150) }, Min = P(0, 0, -150), Max = P(0, 0, -150) };
        Assert.False(ViewGeometry.Matches(deep, new DrawingObjectQuery { ZMin = -3, ZMax = 3 }));
    }

    [Fact]
    public void Mock_lists_part_geometry_on_request()
    {
        var model = new MockTeklaModelService();
        var plain = model.FindDrawingObjects(new DrawingObjectQuery { TypeIn = { "Part" } });
        Assert.All(plain, row => Assert.Null(row.ModelGeometry));

        var withGeometry = model.FindDrawingObjects(new DrawingObjectQuery { TypeIn = { "Part" }, IncludeModelGeometry = true });
        var part = withGeometry.First();
        Assert.NotNull(part.ModelGeometry);
        Assert.False(string.IsNullOrEmpty(part.ModelGeometry!.Profile));

        var none = model.FindDrawingObjects(new DrawingObjectQuery
        {
            TypeIn = { "Part" }, IncludeModelGeometry = true, ProfileContains = "NO-SUCH-PROFILE",
        });
        Assert.Empty(none);
    }
}
