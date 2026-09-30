using System.Linq;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Contract of GetPartCurveGeometry on the mock (issue #15): the PD168.3*6 arch arrives like a
/// Tekla curved beam — two arcs split at its ARC_POINT apex — and must come out as ONE bend with
/// exact radius/chord/rise/arc length, inner and outer arcs of the tube, and a view projection
/// when a view is named.
/// </summary>
public class MockCurveGeometryTests
{
    private static string ArchGuid(MockTeklaModelService model) =>
        Assert.Single(model.FindObjects(new ObjectQuery { Type = "PolyBeam" })).Guid;

    [Fact]
    public void Arch_is_one_bend_with_tube_offsets()
    {
        var model = new MockTeklaModelService();

        var result = model.GetPartCurveGeometry(new PartCurveGeometryRequest { Guid = ArchGuid(model) });

        Assert.True(result.Found);
        Assert.Equal("PolyBeam", result.Type);
        Assert.Equal("RO", result.ProfileType);
        Assert.Equal("PolyBeam.GetCenterLinePolycurve", result.CurveSource);
        Assert.Equal("mm", result.Units);
        Assert.Equal("deg", result.AngleUnits);
        Assert.Equal("model", result.CoordinateSpace);
        Assert.Equal(2, result.Segments.Count);
        Assert.All(result.Segments, segment => Assert.Equal("arc", segment.Kind));

        var bend = Assert.Single(result.Arcs);
        Assert.Equal(new[] { 0, 1 }, bend.SegmentIndices);
        Assert.Equal(5000, bend.Radius);
        Assert.Equal(73.739795, bend.SweepAngle, 5);
        Assert.Equal(6000, bend.ChordLength);
        Assert.Equal(1000, bend.Sagitta);
        Assert.Equal(6435.011, bend.Length);
        Assert.Equal(3000, bend.Mid.X);
        Assert.Equal(5000, bend.Mid.Z);

        Assert.Equal("round", result.Section!.Shape);
        Assert.Equal(168.3, result.Section.Diameter);
        Assert.Equal(4915.85, bend.Inner!.Radius);
        Assert.Equal(5084.15, bend.Outer!.Radius);
        Assert.Equal(983.17, bend.Inner.Sagitta);  // rise of the intrados over its own chord
        Assert.Equal(5899.02, bend.Inner.ChordLength);
        Assert.Equal(6543.312, bend.Outer.Length);

        Assert.Equal(new[] { "none", "arc_point", "none" }, result.Contour.Select(point => point.Chamfer));
        Assert.Equal(6435.011, result.CenterlineLength);
        Assert.Empty(result.Warnings);
        Assert.Null(result.View);
    }

    [Fact]
    public void Arch_projects_into_the_front_view()
    {
        var model = new MockTeklaModelService();

        var result = model.GetPartCurveGeometry(new PartCurveGeometryRequest
        {
            Guid = ArchGuid(model),
            ViewId = 11001,
        });

        var view = result.View;
        Assert.NotNull(view);
        Assert.Equal(11001, view!.ViewId);
        Assert.Equal("view", view.CoordinateSpace);
        var bend = Assert.Single(view.Arcs);
        // FRONT: view X = global X, view Y = global Z, depth = −global Y.
        Assert.Equal(0, bend.Start.X);
        Assert.Equal(4000, bend.Start.Y);
        Assert.Equal(-3000, bend.Start.Z);
        Assert.Equal(5000, bend.Mid.Y);
        Assert.Equal(5000, bend.Radius);
        Assert.Equal(4915.85, bend.Inner!.Radius);
        Assert.Equal(-1, bend.Normal.Z);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Straight_beam_is_one_line()
    {
        var model = new MockTeklaModelService();
        var beam = model.FindObjects(new ObjectQuery { Type = "Beam", Profile = "IPE400" }).First();

        var result = model.GetPartCurveGeometry(new PartCurveGeometryRequest { Guid = beam.Guid });

        var line = Assert.Single(result.Segments);
        Assert.Equal("line", line.Kind);
        Assert.Equal(6000, line.Length);
        Assert.Empty(result.Arcs);
        Assert.Equal("Part.GetCenterLine", result.CurveSource);
        Assert.Equal("other", result.Section!.Shape);
    }

    [Fact]
    public void Unknown_guid_and_non_parts_are_reported_not_thrown()
    {
        var model = new MockTeklaModelService();

        var missing = model.GetPartCurveGeometry(new PartCurveGeometryRequest { Guid = "no-such-guid" });
        Assert.False(missing.Found);
        Assert.NotNull(missing.Message);

        var bolt = model.FindObjects(new ObjectQuery { Type = "Bolt" }, limit: 1).Single();
        var boltResult = model.GetPartCurveGeometry(new PartCurveGeometryRequest { Guid = bolt.Guid });
        Assert.True(boltResult.Found);
        Assert.Empty(boltResult.Segments);
        Assert.Contains("not a part", boltResult.Message);
    }

    [Fact]
    public void View_problems_are_warnings_next_to_the_model_geometry()
    {
        var model = new MockTeklaModelService();
        var guid = ArchGuid(model);

        var unknownView = model.GetPartCurveGeometry(new PartCurveGeometryRequest { Guid = guid, ViewId = 999 });
        Assert.Null(unknownView.View);
        Assert.Single(unknownView.Arcs);
        Assert.Contains("View not found", Assert.Single(unknownView.Warnings));

        Assert.Equal(1, model.CloseActiveDrawing(save: false, apply: true).ModifiedCount);
        var noDrawing = model.GetPartCurveGeometry(new PartCurveGeometryRequest { Guid = guid, ViewIndex = 0 });
        Assert.Null(noDrawing.View);
        Assert.Single(noDrawing.Arcs);
        Assert.Contains("No active drawing", Assert.Single(noDrawing.Warnings));
    }

    [Fact]
    public void Contour_plate_returns_its_contour_but_no_centerline()
    {
        var model = new MockTeklaModelService();
        var plate = model.FindObjects(new ObjectQuery { Type = "ContourPlate" }, limit: 1).Single();

        var result = model.GetPartCurveGeometry(new PartCurveGeometryRequest { Guid = plate.Guid });

        Assert.True(result.Found);
        Assert.Empty(result.Segments);
        Assert.Null(result.CenterlineLength);
        Assert.Equal(4, result.Contour.Count);
        Assert.Contains("no centerline", result.Message);
    }

    [Fact]
    public void Zero_view_id_means_no_projection()
    {
        Assert.False(new PartCurveGeometryRequest { Guid = "x", ViewId = 0 }.WantsView);
        Assert.False(new PartCurveGeometryRequest { Guid = "x", ViewIndex = -1 }.WantsView);
        Assert.True(new PartCurveGeometryRequest { Guid = "x", ViewIndex = 0 }.WantsView);
        Assert.True(new PartCurveGeometryRequest { Guid = "x", ViewId = 0, ViewId2 = 7 }.WantsView);
    }
}
