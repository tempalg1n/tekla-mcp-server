using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

public class DrawingSafetyTests
{
    [Fact]
    public void Batch_created_view_and_object_indices_are_contiguous_and_ids_are_nonzero()
    {
        var model = new MockTeklaModelService();
        var initialViewCount = model.GetDrawingViews().Count;

        var views = model.CreateDrawingViews(new[]
        {
            new DrawingViewSpec { Type = "front" },
            new DrawingViewSpec { Type = "3d" },
        }, apply: true);

        Assert.Equal(2, views.CreatedCount);
        Assert.Equal(
            new[] { initialViewCount, initialViewCount + 1 },
            views.ViewPreview.Select(view => view.Index).ToArray());
        Assert.All(views.ViewPreview, view => Assert.NotEqual(0, view.ViewId));
        Assert.Equal(
            2,
            views.ViewPreview.Select(view => view.ViewId).Distinct().Count());

        var target = views.ViewPreview[0];
        var initialObjectCount =
            model.FindDrawingObjects(new DrawingObjectQuery()).Count;
        var objects = model.CreateDrawingObjects(new[]
        {
            new DrawingObjectSpec
            {
                Kind = "text",
                ViewId = target.ViewId,
                ViewId2 = target.ViewId2,
                Text = "one",
                Points = new List<Point3D> { new Point3D(1, 2, 0) },
            },
            new DrawingObjectSpec
            {
                Kind = "text",
                ViewId = target.ViewId,
                ViewId2 = target.ViewId2,
                Text = "two",
                Points = new List<Point3D> { new Point3D(3, 4, 0) },
            },
        }, apply: true);

        Assert.Equal(2, objects.CreatedCount);
        Assert.Equal(
            new[] { initialObjectCount, initialObjectCount + 1 },
            objects.ObjectPreview.Select(item => item.Index).ToArray());
        Assert.All(objects.ObjectPreview, item => Assert.NotEqual(0, item.ObjectId));
    }

    [Fact]
    public void Ga_view_requires_ga_drawing_and_explicit_coordinate_context()
    {
        var model = new MockTeklaModelService();
        var spec = new DrawingViewSpec
        {
            Type = "ga_model",
            InsertionPoint = new Point3D(100, 100, 0),
            ViewCoordinateSystem = CoordinateSystem(),
            DisplayCoordinateSystem = CoordinateSystem(),
            RestrictionMin = new Point3D(-1000, -1000, -100),
            RestrictionMax = new Point3D(1000, 1000, 100),
        };

        var wrongDrawing = model.CreateDrawingViews(new[] { spec }, apply: false);
        Assert.Empty(wrongDrawing.ViewPreview);
        Assert.NotEmpty(wrongDrawing.Errors);

        Assert.Equal(
            1,
            model.CloseActiveDrawing(save: false, apply: true).ModifiedCount);
        var ga = Assert.Single(model.FindDrawings(new DrawingQuery { Type = "ga" }));
        Assert.Equal(1, model.OpenDrawing(ga.Key, true, true).ModifiedCount);

        var unsupported = model.CreateDrawingViews(
            new[] { new DrawingViewSpec { Type = "front" } },
            apply: false);
        Assert.Empty(unsupported.ViewPreview);
        Assert.NotEmpty(unsupported.Errors);

        var created = model.CreateDrawingViews(new[] { spec }, apply: true);
        var view = Assert.Single(created.ViewPreview);
        Assert.Equal(1, created.CreatedCount);
        Assert.Equal("GeneralArrangementView", view.Type);
    }

    [Fact]
    public void Sheet_objects_require_explicit_sheet_coordinate_space()
    {
        var model = new MockTeklaModelService();
        var invalid = model.CreateDrawingObjects(new[]
        {
            new DrawingObjectSpec
            {
                Kind = "text",
                ViewIndex = -1,
                CoordinateSpace = "view",
                Points = new List<Point3D> { new Point3D() },
            },
        }, apply: true);
        Assert.Equal(0, invalid.CreatedCount);
        Assert.NotEmpty(invalid.Errors);

        var valid = model.CreateDrawingObjects(new[]
        {
            new DrawingObjectSpec
            {
                Kind = "text",
                ViewIndex = -1,
                CoordinateSpace = "sheet",
                Text = "sheet note",
                Points = new List<Point3D> { new Point3D(20, 30, 0) },
            },
        }, apply: true);
        var created = Assert.Single(valid.ObjectPreview);
        Assert.Equal(1, valid.CreatedCount);
        Assert.Equal(-1, created.ViewIndex);
        Assert.Equal("sheet", created.CoordinateSpace);
    }

    [Fact]
    public void Zero_zero_drawing_identifier_matches_nothing()
    {
        var model = new MockTeklaModelService();
        var rows = model.FindDrawingObjects(new DrawingObjectQuery
        {
            ObjectIds = new List<TeklaIdentifierInfo>
            {
                new TeklaIdentifierInfo { Id = 0, Id2 = 0 },
            },
        });

        Assert.Empty(rows);
    }

    private static CoordinateSystemInfo CoordinateSystem() =>
        new CoordinateSystemInfo
        {
            Origin = new Point3D(),
            AxisX = new Point3D(1, 0, 0),
            AxisY = new Point3D(0, 1, 0),
        };
}
