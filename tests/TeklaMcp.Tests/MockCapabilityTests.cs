using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

public class MockCapabilityTests
{
    [Fact]
    public void Create_and_modify_parts_preserve_explicit_and_matched_position()
    {
        var model = new MockTeklaModelService();
        var source = model.GetAllObjects().First(o => o.Position != null);

        var created = model.CreateParts(new[]
        {
            new PartSpec
            {
                Kind = "beam",
                Start = new Point3D(0, 0, 5000),
                End = new Point3D(6000, 0, 5000),
                Profile = "IPE300",
                MatchPositionGuid = source.Guid,
                Position = new PartPosition { Plane = "LEFT", PlaneOffset = 25 },
            },
        }, apply: true);

        var beam = model.GetObjectByGuid(Assert.Single(created.CreatedGuids));
        Assert.NotNull(beam);
        Assert.Equal("LEFT", beam!.Position!.Plane);
        Assert.Equal(25, beam.Position.PlaneOffset);
        Assert.Equal(source.Position!.Rotation, beam.Position.Rotation);

        var modified = model.ModifyParts(new[]
        {
            new PartModification
            {
                Guid = beam.Guid,
                Position = new PartPosition { Depth = "FRONT", DepthOffset = 10 },
            },
        }, apply: true);
        Assert.Equal(1, modified.ModifiedCount);
        Assert.Equal("FRONT", beam.Position.Depth);
        Assert.Equal(10, beam.Position.DepthOffset);
        Assert.Equal("LEFT", beam.Position.Plane);
    }

    [Fact]
    public void Reference_geometry_returns_ifc_semantics_faces_and_aabb()
    {
        var model = new MockTeklaModelService();
        var item = Assert.Single(model.GetReferenceGeometry(new[] { 90001 }, false, 10));

        Assert.Equal("IFCWINDOW", item.Entity);
        Assert.NotEmpty(item.ExternalGuid);
        Assert.Equal(1200, item.OverallWidth);
        Assert.Equal(1500, item.OverallHeight);
        Assert.Single(item.Faces);
        Assert.Equal(5400, item.MinX);
        Assert.Equal(2400, item.MaxZ);
        Assert.NotEmpty(item.AabbSource);
        Assert.NotNull(item.PlacementOrigin);
        Assert.NotNull(item.PlacementXAxis);
        Assert.NotNull(item.PlacementZAxis);
    }

    [Fact]
    public void Reference_geometry_addressable_by_external_ifc_guid()
    {
        var model = new MockTeklaModelService();
        var byGuid = model.GetReferenceGeometry(
            new List<int>(), externalGuids: new[] { "2X_m0ckWindowGuid" });

        var item = Assert.Single(byGuid);
        Assert.Equal("2X_m0ckWindowGuid", item.ExternalGuid);
        Assert.Equal("IFCWINDOW", item.Entity);
        Assert.NotNull(item.PlacementOrigin);

        // Unknown IFC GUIDs resolve to nothing rather than fabricated objects.
        Assert.Empty(model.GetReferenceGeometry(
            new List<int>(), externalGuids: new[] { "0unknownGuid0000000000" }));
    }

    [Fact]
    public void Connection_create_is_preview_by_default_and_listable_after_apply()
    {
        var model = new MockTeklaModelService();
        var parts = model.GetAllObjects().Take(3).ToList();
        var spec = new ConnectionSpec
        {
            Name = "Колонна-фахверк",
            Number = -1,
            PrimaryGuid = parts[0].Guid,
            SecondaryGuids = new List<string> { parts[2].Guid },
            UpVector = new Point3D(0, 0, 1),
        };

        var before = model.GetConnections(parts[0].Guid).Count;
        var preview = model.CreateConnections(new[] { spec }, apply: false);
        Assert.False(preview.Applied);
        Assert.Equal(before, model.GetConnections(parts[0].Guid).Count);
        Assert.Single(preview.ComponentPreview);

        var applied = model.CreateConnections(new[] { spec }, apply: true);
        Assert.Equal(1, applied.CreatedCount);
        Assert.Equal(before + 1, model.GetConnections(parts[0].Guid).Count);
        Assert.Contains(model.GetConnections(parts[0].Guid), c => c.Name == "Колонна-фахверк");
    }

    [Fact]
    public void Numbering_status_is_read_only_and_reports_unknown_guids()
    {
        var model = new MockTeklaModelService();
        var beam = model.FindObjects(new ObjectQuery { Type = "Beam" }, 1).First();

        var status = model.GetNumberingStatus(new[] { beam.Guid, "00000000-0000-0000-0000-000000000001" });

        Assert.True(status.UpToDateAll);
        Assert.Equal(1, status.NotFound);
        Assert.Collection(status.Objects,
            found => Assert.True(found.Found && found.UpToDate == true),
            missing => Assert.False(missing.Found));
        Assert.Empty(model.GetNumberingStatus(null).Objects);
    }

    [Fact]
    public void Standalone_detail_and_section_marks_are_drawing_objects()
    {
        var model = new MockTeklaModelService();
        var result = model.CreateDrawingObjects(new[]
        {
            new DrawingObjectSpec
            {
                Kind = "detail_mark", ViewId = 11001, ViewId2 = 0, Text = "4", Radius = 300,
                Points = new List<Point3D> { new Point3D(0, 0, 0), new Point3D(400, 400, 0) },
            },
            new DrawingObjectSpec
            {
                Kind = "section_mark", ViewId = 11001, ViewId2 = 0, Text = "6",
                Points = new List<Point3D> { new Point3D(0, -500, 0), new Point3D(0, 500, 0) },
            },
        }, apply: true);

        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(new[] { "DetailMark", "SectionMark" }, result.ObjectPreview.Select(o => o.Type));
        Assert.Equal(new[] { "4", "6" }, result.ObjectPreview.Select(o => o.Text));
    }

    [Fact]
    public void Marks_take_custom_or_copied_content()
    {
        var model = new MockTeklaModelService();
        var beam = model.FindObjects(new ObjectQuery { Type = "Beam" }, 1).First();
        var bolt = model.CreateDrawingObjects(new[]
        {
            new DrawingObjectSpec
            {
                Kind = "mark", ViewId = 11001, ViewId2 = 0, ModelGuid = beam.Guid,
                ContentText = "Bolt M20 (8 pcs)", LeaderStart = new Point3D(10, 10, 0),
            },
        }, apply: true).ObjectPreview.Single();
        Assert.Equal("Bolt M20 (8 pcs)", bolt.Text);

        var copy = model.CreateDrawingObjects(new[]
        {
            new DrawingObjectSpec
            {
                Kind = "mark", ViewId = 11001, ViewId2 = 0, ModelGuid = beam.Guid,
                CopyContentFromObjectId = bolt.ObjectId + ":" + bolt.ObjectId2,
            },
        }, apply: true).ObjectPreview.Single();
        Assert.Equal("Bolt M20 (8 pcs)", copy.Text);

        var edited = model.ModifyDrawingObjects(
            new DrawingObjectQuery { ObjectIds = { new TeklaIdentifierInfo { Id = copy.ObjectId, Id2 = copy.ObjectId2 } } },
            new DrawingObjectModification { ContentText = "Bolt M20 (4 pcs)" }, apply: true);
        Assert.Equal(1, edited.ModifiedCount);
        Assert.Contains(model.FindDrawingObjects(new DrawingObjectQuery { TypeIn = { "Mark" } }),
            m => m.Text == "Bolt M20 (4 pcs)");
    }

    [Fact]
    public void Marks_list_their_content_and_empty_marks_can_be_found()
    {
        var model = new MockTeklaModelService();
        var beam = model.FindObjects(new ObjectQuery { Type = "Beam" }, 1).First();
        DrawingObjectSpec Mark(string guid) => new DrawingObjectSpec
        {
            Kind = "mark", ViewId = 11001, ViewId2 = 0, ModelGuid = guid,
        };
        model.CreateDrawingObjects(
            new[] { Mark(beam.Guid), Mark("00000000-0000-0000-0000-000000000002") }, apply: true);

        var marks = model.FindDrawingObjects(new DrawingObjectQuery { TypeIn = { "Mark" } });
        var full = Assert.Single(marks, m => m.ModelGuid == beam.Guid);
        Assert.False(full.IsEmptyMark);
        Assert.Equal(beam.AssemblyPos ?? "", full.Text);
        Assert.Equal("ASSEMBLY_POS", Assert.Single(full.MarkContent!).Name);

        var empty = model.FindDrawingObjects(new DrawingObjectQuery { EmptyMarksOnly = true });
        Assert.Equal("00000000-0000-0000-0000-000000000002", Assert.Single(empty).ModelGuid);
    }
}
