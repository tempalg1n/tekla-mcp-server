using System.Linq;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Placing GA views by frame (issue #33) and loading a view attributes file while keeping the
/// view's scale, restriction box and origin (issue #28).
/// </summary>
public class ViewPlacementTests
{
    private static Point3D P(double x, double y) => new Point3D(x, y, 0);

    [Theory]
    [InlineData("origin", ViewPlacement.Origin)]
    [InlineData("frameLowerLeft", ViewPlacement.FrameLowerLeft)]
    [InlineData("frame-center", ViewPlacement.FrameCenter)]
    [InlineData("FRAME_CENTRE", ViewPlacement.FrameCenter)]
    [InlineData("", ViewPlacement.Origin)]
    [InlineData("frame", null)]
    public void PlaceBy_spellings(string raw, string? expected) =>
        Assert.Equal(expected, ViewPlacement.Normalize(raw));

    [Fact]
    public void The_field_case_frame_lands_where_asked()
    {
        // Plan X 55 200…62 600 at 1:50, origin 720,150: the frame starts 1 104 mm right of it.
        ViewPlacement.EstimateFrame(P(720, 150), P(55200, 0), P(62600, 8000), 50, out var min, out var max);
        Assert.Equal(720 + 1104, min.X, 6);

        var origin = ViewPlacement.OriginForFrame(ViewPlacement.FrameLowerLeft, P(30, 40), P(720, 150), min, max);
        Assert.Equal(30 - 1104, origin.X, 6); // the user's formula: target − restrictionMin / scale
        Assert.Equal(40, origin.Y, 6);

        ViewPlacement.EstimateFrame(origin, P(55200, 0), P(62600, 8000), 50, out min, out max);
        Assert.True(ViewPlacement.IsPlaced(ViewPlacement.FrameLowerLeft, P(30, 40), min, max));
    }

    [Fact]
    public void Frame_center_moves_the_frame_middle()
    {
        var origin = ViewPlacement.OriginForFrame(
            ViewPlacement.FrameCenter, P(420, 297), P(0, 0), P(100, 100), P(300, 200));
        Assert.Equal((220.0, 147.0), (origin.X, origin.Y));
        Assert.True(ViewPlacement.IsPlaced(ViewPlacement.FrameCenter, P(420, 297), P(320, 247), P(520, 347)));
    }

    private static DrawingViewSpec GaSpec(string placeBy) => new DrawingViewSpec
    {
        Type = "ga_model",
        InsertionPoint = P(30, 40),
        ViewCoordinateSystem = new CoordinateSystemInfo { AxisX = new Point3D(1, 0, 0), AxisY = new Point3D(0, 1, 0) },
        DisplayCoordinateSystem = new CoordinateSystemInfo { AxisX = new Point3D(1, 0, 0), AxisY = new Point3D(0, 1, 0) },
        RestrictionMin = new Point3D(55200, 0, -100),
        RestrictionMax = new Point3D(62600, 8000, 100),
        Scale = 50,
        PlaceBy = placeBy,
    };

    private static MockTeklaModelService WithActiveGaDrawing()
    {
        var model = new MockTeklaModelService();
        model.CloseActiveDrawing(save: false, apply: true);
        var ga = Assert.Single(model.FindDrawings(new DrawingQuery { Type = "ga" }));
        model.OpenDrawing(ga.Key, true, true);
        return model;
    }

    [Fact]
    public void Mock_ga_view_is_placed_by_its_frame()
    {
        var model = WithActiveGaDrawing();

        var created = Assert.Single(model.CreateDrawingViews(new[] { GaSpec("frame_lower_left") }, apply: true).ViewPreview);
        Assert.Equal(30, created.FrameMin!.X, 6);
        Assert.Equal(40, created.FrameMin.Y, 6);

        var byOrigin = Assert.Single(model.CreateDrawingViews(new[] { GaSpec("origin") }, apply: false).ViewPreview);
        Assert.Equal(30, byOrigin.Origin!.X, 6);
        Assert.True(byOrigin.FrameMin!.X > 1000);

        var bad = model.CreateDrawingViews(new[] { GaSpec("somewhere") }, apply: false);
        Assert.Empty(bad.ViewPreview);
        Assert.Contains(bad.Errors, e => e.Contains("placeBy"));
    }

    [Fact]
    public void Mock_view_attribute_file_keeps_scale_by_default()
    {
        var model = WithActiveGaDrawing();
        var view = Assert.Single(model.CreateDrawingViews(new[] { GaSpec("origin") }, apply: true).ViewPreview);

        var result = model.ModifyDrawingViews(new[]
        {
            new DrawingViewModification { ViewId = view.ViewId, ViewId2 = view.ViewId2, AttributeFile = "monorail_plan" },
        }, apply: true);

        Assert.Equal(1, result.ModifiedCount);
        Assert.Equal(50, model.GetDrawingViews().Single(v => v.ViewId == view.ViewId).Scale);
        Assert.Contains(result.Warnings, w => w.Contains("not evaluated"));
    }
}
