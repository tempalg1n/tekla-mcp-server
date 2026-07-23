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
}
