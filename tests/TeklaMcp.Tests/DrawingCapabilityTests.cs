using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

public class DrawingCapabilityTests
{
    private const string AssemblyGuid = "00000000-0000-0000-0000-000000001000";
    private const string BeamGuid = "00000000-0000-0000-0000-000000001004";

    [Fact]
    public void Status_list_selection_and_summary_inputs_are_deterministic()
    {
        var model = new MockTeklaModelService();

        var status = model.GetDrawingStatus();
        Assert.True(status.Connected);
        Assert.True(status.AnyDrawingOpen);
        Assert.Equal("Mock", status.Backend);
        Assert.Equal("A-1", status.ActiveDrawing?.Mark);
        Assert.True(status.ActiveDrawing?.IsActive);

        var all = model.FindDrawings(new DrawingQuery());
        Assert.Equal(3, all.Count);
        Assert.Equal(2, model.FindDrawings(new DrawingQuery { SelectedOnly = true }).Count);
        Assert.Single(model.FindDrawings(new DrawingQuery(), limit: 1));

        var assembly = Assert.Single(model.FindDrawings(new DrawingQuery { Type = "assembly" }));
        Assert.Equal(AssemblyGuid, assembly.AssociatedModelGuid);
        Assert.Same(assembly, Assert.Single(model.FindDrawings(
            new DrawingQuery { KeyIn = new List<string> { assembly.Key.ToUpperInvariant() } })));
        Assert.Equal("G-101", Assert.Single(model.FindDrawings(
            new DrawingQuery { MarkContains = "g-10" })).Mark);
        Assert.Equal("G-101", Assert.Single(model.FindDrawings(
            new DrawingQuery { NameContains = "plan" })).Mark);
        Assert.Equal("G-101", Assert.Single(model.FindDrawings(
            new DrawingQuery { TitleContains = "framing" })).Mark);
        Assert.Equal("A-1", Assert.Single(model.FindDrawings(
            new DrawingQuery { AssociatedModelGuid = AssemblyGuid })).Mark);
        Assert.Equal("G-101", Assert.Single(model.FindDrawings(
            new DrawingQuery { UpToDateStatusContains = "parts" })).Mark);
        Assert.Equal("G-101", Assert.Single(model.FindDrawings(
            new DrawingQuery { IsIssued = true })).Mark);
        Assert.Equal("B-1", Assert.Single(model.FindDrawings(
            new DrawingQuery { IsLocked = true })).Mark);
        Assert.Equal("A-1", Assert.Single(model.FindDrawings(
            new DrawingQuery { IsReadyForIssue = true })).Mark);

        // These are the service rows consumed by tekla_get_drawing_summary and drawing QA.
        Assert.Equal(1, all.Count(d => d.IsIssued));
        Assert.Equal(1, all.Count(d => d.IsIssuedButModified));
        Assert.Equal(1, all.Count(d => d.IsLocked));
        Assert.Equal(1, all.Count(d => d.IsReadyForIssue));
        Assert.Equal(2, all.Count(d => !string.Equals(
            d.UpToDateStatus, "DrawingIsUpToDate", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            new[] { "AssemblyDrawing", "GeneralArrangementDrawing", "SinglePartDrawing" },
            all.Select(d => d.Type).OrderBy(value => value).ToArray());
    }

    [Fact]
    public void Drawing_model_references_work_with_key_mark_and_missing_address()
    {
        var model = new MockTeklaModelService();
        var assembly = Assert.Single(model.FindDrawings(
            new DrawingQuery { AssociatedModelGuid = AssemblyGuid }));

        var byKeyResult = model.GetDrawingModelObjects(assembly.Key);
        var byMarkResult = model.GetDrawingModelObjects(assembly.Mark);
        var byKey = Assert.Single(byKeyResult.Items);
        var byMark = Assert.Single(byMarkResult.Items);
        Assert.Equal(AssemblyGuid, byKey.Guid);
        Assert.Equal(byKey.Id, byMark.Id);
        Assert.Equal(byKey.Id + ":0", byKey.Key);
        Assert.True(byKey.Id > 0);
        Assert.Equal(1, byKeyResult.TotalCount);
        Assert.False(byKeyResult.Truncated);

        var generalArrangement = Assert.Single(model.FindDrawings(
            new DrawingQuery { Type = "ga" }));
        var gaObjects = model.GetDrawingModelObjects(generalArrangement.Key, offset: 2, limit: 3);
        Assert.Equal(8, gaObjects.TotalCount);
        Assert.Equal(3, gaObjects.ReturnedCount);
        Assert.True(gaObjects.Truncated);
        Assert.Equal(
            gaObjects.Items.Count,
            gaObjects.Items.Select(item => item.Key).Distinct().Count());
        Assert.Empty(model.GetDrawingModelObjects("missing-drawing").Items);
    }

    [Fact]
    public void Views_objects_ids_queries_and_selection_are_exposed()
    {
        var model = new MockTeklaModelService();

        var views = model.GetDrawingViews();
        Assert.Equal(2, views.Count);
        Assert.Equal(new[] { 11001, 11002 }, views.Select(view => view.ViewId).ToArray());
        Assert.All(views, view => Assert.True(view.Scale > 0));

        var all = model.FindDrawingObjects(new DrawingObjectQuery());
        Assert.Equal(4, all.Count);
        var text = Assert.Single(model.FindDrawingObjects(new DrawingObjectQuery
        {
            TypeIn = new List<string> { "Text" },
            TextContains = "typ",
        }));
        Assert.Equal(1, text.Index);

        Assert.Same(text, Assert.Single(model.FindDrawingObjects(new DrawingObjectQuery
        {
            IndexIn = new List<int> { text.Index },
        })));
        Assert.Same(text, Assert.Single(model.FindDrawingObjects(new DrawingObjectQuery
        {
            ObjectIds = new List<TeklaIdentifierInfo>
            {
                new TeklaIdentifierInfo { Id = text.ObjectId, Id2 = text.ObjectId2 },
            },
        })));
        Assert.Equal(3, model.FindDrawingObjects(
            new DrawingObjectQuery { ViewId = 11001, ViewId2 = 0 }).Count);
        Assert.Single(model.FindDrawingObjects(
            new DrawingObjectQuery { ViewIndex = 1 }));
        Assert.Equal("Part", Assert.Single(model.FindDrawingObjects(
            new DrawingObjectQuery { ModelGuid = AssemblyGuid })).Type);
        Assert.Single(model.FindDrawingObjects(new DrawingObjectQuery(), limit: 1));

        var selectedRows = model.FindDrawingObjects(
            new DrawingObjectQuery { UseSelection = true });
        Assert.Equal(new[] { 0, 1 }, selectedRows.Select(item => item.Index).ToArray());

        var selection = model.SelectDrawingObjects(new DrawingObjectQuery
        {
            TypeIn = new List<string> { "Line" },
            ViewId = 11002,
        });
        Assert.Equal(1, selection.SelectedCount);
        Assert.Equal(2, Assert.Single(selection.Preview).Index);
        Assert.Equal("Mock", selection.Backend);
    }

    [Fact]
    public void Open_save_and_close_previews_do_not_change_editor_state()
    {
        var model = new MockTeklaModelService();
        var original = model.GetDrawingStatus().ActiveDrawing!;
        var ga = Assert.Single(model.FindDrawings(new DrawingQuery { Type = "ga" }));

        var openPreview = model.OpenDrawing(ga.Key, showDrawing: true, apply: false);
        Assert.False(openPreview.Applied);
        Assert.Equal(1, openPreview.PlannedCount);
        Assert.Equal(original.Key, model.GetDrawingStatus().ActiveDrawing?.Key);

        var refused = model.OpenDrawing(ga.Mark, showDrawing: true, apply: true);
        Assert.Equal(0, refused.ModifiedCount);
        Assert.Contains("Close", refused.Message);
        Assert.Equal(original.Key, model.GetDrawingStatus().ActiveDrawing?.Key);

        Assert.Equal(1, model.CloseActiveDrawing(save: false, apply: true).ModifiedCount);
        var opened = model.OpenDrawing(ga.Mark, showDrawing: true, apply: true);
        Assert.True(opened.Applied);
        Assert.Equal(1, opened.ModifiedCount);
        Assert.Equal(ga.Key, model.GetDrawingStatus().ActiveDrawing?.Key);

        var modificationDate = ga.ModificationDate;
        var savePreview = model.SaveActiveDrawing(apply: false);
        Assert.Equal(1, savePreview.PlannedCount);
        Assert.Equal(modificationDate, ga.ModificationDate);

        var saved = model.SaveActiveDrawing(apply: true);
        Assert.Equal(1, saved.ModifiedCount);
        Assert.True(ga.ModificationDate > modificationDate);

        var closePreview = model.CloseActiveDrawing(save: true, apply: false);
        Assert.Equal(1, closePreview.PlannedCount);
        Assert.True(model.GetDrawingStatus().AnyDrawingOpen);

        var closed = model.CloseActiveDrawing(save: true, apply: true);
        Assert.Equal(1, closed.ModifiedCount);
        Assert.False(model.GetDrawingStatus().AnyDrawingOpen);
        Assert.Empty(model.GetDrawingViews());
        Assert.Empty(model.FindDrawingObjects(new DrawingObjectQuery()));

        Assert.Equal(0, model.OpenDrawing("not-a-drawing", true, true).ModifiedCount);
    }

    [Fact]
    public void Create_modify_issue_update_print_and_delete_are_preview_gated()
    {
        var model = new MockTeklaModelService();
        var initialCount = model.FindDrawings(new DrawingQuery()).Count;
        var spec = new DrawingSpec
        {
            Type = "single_part",
            ModelGuid = BeamGuid,
            AttributeFile = "standard",
            SheetNumber = 2,
            Name = "MCP BEAM DETAIL",
        };

        var createPreview = model.CreateDrawings(new[] { spec }, apply: false);
        var previewDrawing = Assert.Single(createPreview.DrawingPreview);
        Assert.False(createPreview.Applied);
        Assert.Equal("SinglePartDrawing", previewDrawing.Type);
        Assert.Equal(initialCount, model.FindDrawings(new DrawingQuery()).Count);

        Assert.Equal(1, model.CloseActiveDrawing(save: false, apply: true).ModifiedCount);
        var created = model.CreateDrawings(new[] { spec }, apply: true);
        var createdDrawing = Assert.Single(created.DrawingPreview);
        Assert.Equal(1, created.CreatedCount);
        Assert.Equal(initialCount + 1, model.FindDrawings(new DrawingQuery()).Count);
        Assert.Same(createdDrawing, Assert.Single(model.FindDrawings(new DrawingQuery
        {
            KeyIn = new List<string> { createdDrawing.Key },
        })));

        var target = new DrawingQuery { KeyIn = new List<string> { createdDrawing.Key } };
        var modifyPreview = model.ModifyDrawings(target, new DrawingModification
        {
            Name = "RENAMED",
            Title1 = "REV A",
            IsReadyForIssue = true,
        }, apply: false);
        Assert.Equal(1, modifyPreview.PlannedCount);
        Assert.Equal("MCP BEAM DETAIL", createdDrawing.Name);
        Assert.False(createdDrawing.IsReadyForIssue);

        var modified = model.ModifyDrawings(target, new DrawingModification
        {
            Name = "RENAMED",
            Title1 = "REV A",
            IsReadyForIssue = true,
        }, apply: true);
        Assert.Equal(1, modified.ModifiedCount);
        Assert.Equal("RENAMED", createdDrawing.Name);
        Assert.Equal("REV A", createdDrawing.Title1);
        Assert.True(createdDrawing.IsReadyForIssue);

        var issuePreview = model.OperateDrawings(
            target, "issue", printOptions: null, apply: false);
        Assert.Equal(1, issuePreview.PlannedCount);
        Assert.False(createdDrawing.IsIssued);
        Assert.Equal(1, model.OperateDrawings(
            target, "issue", printOptions: null, apply: true).ModifiedCount);
        Assert.True(createdDrawing.IsIssued);
        Assert.NotNull(createdDrawing.IssuingDate);

        var ga = Assert.Single(model.FindDrawings(new DrawingQuery { Type = "ga" }));
        var staleStatus = ga.UpToDateStatus;
        var gaQuery = new DrawingQuery { KeyIn = new List<string> { ga.Key } };
        model.OperateDrawings(gaQuery, "update", printOptions: null, apply: false);
        Assert.Equal(staleStatus, ga.UpToDateStatus);
        Assert.Equal(1, model.OperateDrawings(
            gaQuery, "update", printOptions: null, apply: true).ModifiedCount);
        Assert.Equal("DrawingIsUpToDate", ga.UpToDateStatus);

        var output = "/tmp/mock-drawing.pdf";
        var printPreview = model.OperateDrawings(
            target,
            "print",
            new DrawingPrintOptions { OutputFile = output },
            apply: false);
        Assert.Equal(output, Assert.Single(printPreview.OutputFiles));
        Assert.Null(createdDrawing.OutputDate);
        var printed = model.OperateDrawings(
            target,
            "print",
            new DrawingPrintOptions { OutputFile = output },
            apply: true);
        Assert.Equal(1, printed.ModifiedCount);
        Assert.Equal(output, Assert.Single(printed.OutputFiles));
        Assert.NotNull(createdDrawing.OutputDate);

        var deletePreview = model.OperateDrawings(
            target, "delete", printOptions: null, apply: false);
        Assert.Equal(1, deletePreview.PlannedCount);
        Assert.Single(model.FindDrawings(target));
        var deleted = model.OperateDrawings(
            target, "delete", printOptions: null, apply: true);
        Assert.Equal(1, deleted.DeletedCount);
        Assert.Empty(model.FindDrawings(target));
    }

    [Fact]
    public void AutoDrawing_rule_preview_is_non_mutating_and_apply_creates_each_drawing()
    {
        var model = new MockTeklaModelService();
        var guids = new[] { AssemblyGuid, BeamGuid };
        var initialCount = model.FindDrawings(new DrawingQuery()).Count;

        var preview = model.CreateDrawingsFromRule("mcp_rule", guids, apply: false);
        Assert.False(preview.Applied);
        Assert.Equal(2, preview.PlannedCount);
        Assert.All(preview.DrawingPreview, drawing =>
        {
            Assert.Equal("AutoDrawingRule", drawing.Type);
            Assert.Equal("mcp_rule", drawing.Name);
        });
        Assert.Equal(initialCount, model.FindDrawings(new DrawingQuery()).Count);

        Assert.Equal(1, model.CloseActiveDrawing(save: false, apply: true).ModifiedCount);
        var applied = model.CreateDrawingsFromRule("mcp_rule", guids, apply: true);
        Assert.True(applied.Applied);
        Assert.Equal(2, applied.CreatedCount);
        Assert.Equal(initialCount + 2, model.FindDrawings(new DrawingQuery()).Count);
        Assert.Equal(2, model.FindDrawings(
            new DrawingQuery { NameContains = "Rule mcp_rule" }).Count);
    }

    [Fact]
    public void Drawing_view_create_modify_and_delete_are_preview_gated()
    {
        var model = new MockTeklaModelService();
        var initialCount = model.GetDrawingViews().Count;
        var spec = new DrawingViewSpec
        {
            Type = "3d",
            Name = "MCP 3D",
            InsertionPoint = new Point3D(300, 200, 0),
            Scale = 20,
        };

        var createPreview = model.CreateDrawingViews(new[] { spec }, apply: false);
        var preview = Assert.Single(createPreview.ViewPreview);
        Assert.Equal("3DView", preview.Type);
        Assert.Equal(initialCount, model.GetDrawingViews().Count);

        var created = model.CreateDrawingViews(new[] { spec }, apply: true);
        var view = Assert.Single(created.ViewPreview);
        Assert.Equal(1, created.CreatedCount);
        Assert.Equal(initialCount + 1, model.GetDrawingViews().Count);
        Assert.Contains(model.GetDrawingViews(), item => item.Index == view.Index);

        var originalName = view.Name;
        var modifyPreview = model.ModifyDrawingViews(new[]
        {
            new DrawingViewModification
            {
                ViewIndex = view.Index,
                Name = "MCP ISO",
                Scale = 25,
                Width = 220,
            },
        }, apply: false);
        Assert.Equal(1, modifyPreview.PlannedCount);
        Assert.Equal(originalName, view.Name);

        var modified = model.ModifyDrawingViews(new[]
        {
            new DrawingViewModification
            {
                ViewIndex = view.Index,
                Name = "MCP ISO",
                Scale = 25,
                Width = 220,
            },
        }, apply: true);
        Assert.Equal(1, modified.ModifiedCount);
        Assert.Equal("MCP ISO", view.Name);
        Assert.Equal(25, view.Scale);
        Assert.Equal(220, view.Width);

        var deletePreview = model.ModifyDrawingViews(new[]
        {
            new DrawingViewModification { ViewIndex = view.Index, Delete = true },
        }, apply: false);
        Assert.Equal(1, deletePreview.PlannedCount);
        Assert.Contains(model.GetDrawingViews(), item => ReferenceEquals(item, view));

        var deleted = model.ModifyDrawingViews(new[]
        {
            new DrawingViewModification { ViewIndex = view.Index, Delete = true },
        }, apply: true);
        Assert.Equal(1, deleted.DeletedCount);
        Assert.DoesNotContain(model.GetDrawingViews(), item => ReferenceEquals(item, view));
    }

    [Fact]
    public void Drawing_object_create_modify_delete_and_id_queries_are_preview_gated()
    {
        var model = new MockTeklaModelService();
        var initialCount = model.FindDrawingObjects(new DrawingObjectQuery()).Count;
        var spec = new DrawingObjectSpec
        {
            Kind = "text",
            ViewId = 11001,
            ViewId2 = 0,
            ViewIndex = 999,
            Text = "MCP NOTE",
            Points = new List<Point3D> { new Point3D(10, 20, 0) },
        };

        var createPreview = model.CreateDrawingObjects(new[] { spec }, apply: false);
        var preview = Assert.Single(createPreview.ObjectPreview);
        Assert.Equal("Text", preview.Type);
        Assert.Equal(initialCount, model.FindDrawingObjects(new DrawingObjectQuery()).Count);

        var created = model.CreateDrawingObjects(new[] { spec }, apply: true);
        var drawingObject = Assert.Single(created.ObjectPreview);
        Assert.Equal(1, created.CreatedCount);
        Assert.Equal(initialCount + 1, model.FindDrawingObjects(new DrawingObjectQuery()).Count);
        Assert.Equal("mcp:create_drawing_object", drawingObject.Udas["MCP_ORIGIN"]);

        var byId = new DrawingObjectQuery
        {
            ObjectIds = new List<TeklaIdentifierInfo>
            {
                new TeklaIdentifierInfo
                {
                    Id = drawingObject.ObjectId,
                    Id2 = drawingObject.ObjectId2,
                },
            },
        };
        Assert.Same(drawingObject, Assert.Single(model.FindDrawingObjects(byId)));

        var originalPoint = new Point3D(
            drawingObject.Points[0].X,
            drawingObject.Points[0].Y,
            drawingObject.Points[0].Z);
        var modifyPreview = model.ModifyDrawingObjects(byId, new DrawingObjectModification
        {
            Text = "MOVED NOTE",
            MoveBy = new Point3D(5, -2, 1),
            Visibility = "view",
        }, apply: false);
        Assert.Equal(1, modifyPreview.PlannedCount);
        Assert.Equal("MCP NOTE", drawingObject.Text);
        Assert.Equal(originalPoint.X, drawingObject.Points[0].X);
        Assert.Null(drawingObject.IsHidden);

        var modified = model.ModifyDrawingObjects(byId, new DrawingObjectModification
        {
            Text = "MOVED NOTE",
            MoveBy = new Point3D(5, -2, 1),
            Visibility = "view",
        }, apply: true);
        Assert.Equal(1, modified.ModifiedCount);
        Assert.Equal("MOVED NOTE", drawingObject.Text);
        Assert.Equal(originalPoint.X + 5, drawingObject.Points[0].X);
        Assert.Equal(originalPoint.Y - 2, drawingObject.Points[0].Y);
        Assert.Equal(originalPoint.Z + 1, drawingObject.Points[0].Z);
        Assert.True(drawingObject.IsHidden);
        Assert.Equal("mcp:modify_drawing_object", drawingObject.Udas["MCP_ORIGIN"]);

        var deletePreview = model.ModifyDrawingObjects(
            byId, new DrawingObjectModification { Delete = true }, apply: false);
        Assert.Equal(1, deletePreview.PlannedCount);
        Assert.Single(model.FindDrawingObjects(byId));

        var deleted = model.ModifyDrawingObjects(
            byId, new DrawingObjectModification { Delete = true }, apply: true);
        Assert.Equal(1, deleted.DeletedCount);
        Assert.Empty(model.FindDrawingObjects(byId));
        Assert.Equal(initialCount, model.FindDrawingObjects(new DrawingObjectQuery()).Count);
    }
}
