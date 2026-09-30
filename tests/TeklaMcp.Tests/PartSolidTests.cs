using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// tekla_get_part_solid (backlog §5): caps must never produce a half-shape that looks whole, and
/// the outer loop is picked by geometry, not by Tekla's undocumented order.
/// </summary>
public class PartSolidTests
{
    private static readonly Point3D Up = new Point3D(0, 0, 1);

    private static List<Point3D> Square(double size, double z = 0) => new List<Point3D>
    {
        new Point3D(0, 0, z), new Point3D(size, 0, z), new Point3D(size, size, z), new Point3D(0, size, z),
    };

    [Fact]
    public void Loop_area_in_the_face_plane()
    {
        Assert.Equal(10000, SolidMath.LoopArea(Square(100), Up), 6);
        // Winding does not matter.
        Assert.Equal(10000, SolidMath.LoopArea(Enumerable.Reverse(Square(100)).ToList(), Up), 6);
    }

    [Fact]
    public void Outer_loop_is_the_largest_whatever_the_order()
    {
        var builder = new SolidBuilder(10, 100);
        builder.Add(Up, null, () => new[] { Square(20), Square(100) }); // hole first
        var solid = new PartSolidGeometry();
        builder.Fill(solid);

        Assert.Equal(1, Assert.Single(solid.Faces).OuterLoopIndex);
    }

    [Fact]
    public void Face_cap_counts_every_face_and_says_it_is_not_the_whole_shape()
    {
        var builder = new SolidBuilder(maxFaces: 2, maxPoints: 1000);
        for (var i = 0; i < 6; i++) builder.Add(Up, null, () => new[] { Square(10) });
        var solid = new PartSolidGeometry();
        builder.Fill(solid);

        Assert.Equal(6, solid.FaceCount);
        Assert.Equal(2, solid.FacesReturned);
        Assert.True(solid.Truncated);
        Assert.Contains("NOT the whole shape", solid.TruncatedReason);
    }

    [Fact]
    public void Point_cap_leaves_a_face_out_whole_and_stops_reading()
    {
        var reads = 0;
        IEnumerable<IEnumerable<Point3D>> Loops() { reads++; return new[] { Square(10) }; }

        var builder = new SolidBuilder(maxFaces: 100, maxPoints: 6);
        for (var i = 0; i < 4; i++) builder.Add(Up, null, Loops);
        var solid = new PartSolidGeometry();
        builder.Fill(solid);

        Assert.Equal(1, solid.FacesReturned);          // 4 points fit, the next 4 do not
        Assert.Equal(4, solid.PointsReturned);
        Assert.All(solid.Faces, f => Assert.Equal(4, f.Loops[0].Vertices.Count));
        Assert.Equal(2, reads);                        // once full, later faces are not read
        Assert.Equal(4, solid.FaceCount);
        Assert.Contains("maxPoints", solid.TruncatedReason);
    }

    [Fact]
    public void Exactly_at_the_cap_is_not_truncated()
    {
        var builder = new SolidBuilder(maxFaces: 3, maxPoints: 12);
        for (var i = 0; i < 3; i++) builder.Add(Up, null, () => new[] { Square(10) });
        var solid = new PartSolidGeometry();
        builder.Fill(solid);

        Assert.False(solid.Truncated);
        Assert.Null(solid.TruncatedReason);
    }

    [Fact]
    public void Box_faces_point_outward_and_close_the_box()
    {
        var min = new Point3D(0, 0, 0);
        var max = new Point3D(100, 200, 300);
        var faces = SolidMath.BoxFaces(min, max).ToList();

        Assert.Equal(6, faces.Count);
        var area = faces.Sum(f => SolidMath.LoopArea(f.Loop, f.Normal));
        Assert.Equal(2 * (100 * 200 + 100 * 300 + 200 * 300), area, 6);
        foreach (var (normal, loop) in faces)
        {
            // The loop centre lies on the side of the box its normal points to.
            var cx = loop.Average(p => p.X) - 50;
            var cy = loop.Average(p => p.Y) - 100;
            var cz = loop.Average(p => p.Z) - 150;
            Assert.True(cx * normal.X + cy * normal.Y + cz * normal.Z > 0);
        }
    }

    [Fact]
    public void Mock_serves_a_box_and_reports_missing_parts()
    {
        var mock = new MockTeklaModelService();
        var part = mock.FindObjects(new ObjectQuery { Type = "Beam" }, 1).First();

        var result = mock.GetPartSolids(new PartSolidRequest { Guids = { part.Guid, "no-such-guid" } });

        Assert.True(result[0].Found);
        Assert.Equal(6, result[0].FaceCount);
        Assert.False(result[0].Truncated);
        Assert.False(result[1].Found);
        Assert.Equal("Object not found.", result[1].Message);
    }
}
