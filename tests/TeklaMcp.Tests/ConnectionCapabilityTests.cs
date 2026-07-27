using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Covers the connection orientation/replacement contract added after the v0.7.0 field report.
/// The mock deliberately reproduces Tekla's auto-direction quirk (a written UpVector is only
/// stored under AUTODIR_NA), so these tests pin the workaround rather than a mock convenience.
/// </summary>
public class ConnectionCapabilityTests
{
    private static (MockTeklaModelService Model, ComponentInfo Connection) WithConnection(
        string autoDirection = "NA")
    {
        var model = new MockTeklaModelService();
        var parts = model.GetAllObjects().Take(3).ToList();
        var created = model.CreateConnections(new[]
        {
            new ConnectionSpec
            {
                Name = "Разовый узел фахверка ГК",
                Number = -1,
                PrimaryGuid = parts[0].Guid,
                SecondaryGuids = new List<string> { parts[2].Guid },
                UpVector = new Point3D(0, 0, 1),
                AutoDirection = autoDirection,
            },
        }, apply: true);

        var guid = Assert.Single(created.CreatedGuids);
        var connection = model.GetConnections(parts[0].Guid).Single(c => c.Guid == guid);
        return (model, connection);
    }

    [Fact]
    public void Setting_up_vector_switches_auto_direction_to_na_so_the_write_sticks()
    {
        var (model, connection) = WithConnection(autoDirection: "BASIC");
        Assert.Equal("AUTODIR_BASIC", connection.AutoDirection);

        var result = model.ModifyConnections(new[]
        {
            new ConnectionModification
            {
                Guid = connection.Guid,
                UpVector = new Point3D(1000, 0, 0),
            },
        }, apply: true);

        Assert.Equal(1, result.ModifiedCount);
        Assert.Equal("AUTODIR_NA", connection.AutoDirection);
        Assert.Equal(1000, connection.UpVector!.X);
    }

    [Fact]
    public void Explicit_non_na_auto_direction_is_honored_and_discards_the_vector_like_tekla()
    {
        var (model, connection) = WithConnection();
        var originalUpVector = connection.UpVector;

        model.ModifyConnections(new[]
        {
            new ConnectionModification
            {
                Guid = connection.Guid,
                UpVector = new Point3D(1000, 0, 0),
                AutoDirection = "BASIC",
            },
        }, apply: true);

        Assert.Equal("AUTODIR_BASIC", connection.AutoDirection);
        Assert.Same(originalUpVector, connection.UpVector);
    }

    [Fact]
    public void Modify_connections_is_preview_by_default()
    {
        var (model, connection) = WithConnection();

        var preview = model.ModifyConnections(new[]
        {
            new ConnectionModification
            {
                Guid = connection.Guid,
                UpVector = new Point3D(0, 1000, 0),
            },
        }, apply: false);

        Assert.False(preview.Applied);
        Assert.Equal(0, preview.ModifiedCount);
        Assert.Equal(1, preview.PlannedCount);
        // The plan shows the intended orientation without touching the model.
        Assert.Equal("AUTODIR_NA", Assert.Single(preview.ComponentPreview).AutoDirection);
        Assert.Equal(1, connection.UpVector!.Z);
    }

    [Fact]
    public void Unknown_connection_is_reported_not_silently_skipped()
    {
        var model = new MockTeklaModelService();

        var result = model.ModifyConnections(new[]
        {
            new ConnectionModification { Guid = "00000000-0000-0000-0000-000000000000" },
        }, apply: true);

        Assert.Equal(0, result.ModifiedCount);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Replace_existing_swaps_the_node_type_on_an_occupied_pair()
    {
        var model = new MockTeklaModelService();
        var parts = model.GetAllObjects().Take(3).ToList();
        var pair = new ConnectionSpec
        {
            Name = "Стойка-ригель",
            Number = -1,
            PrimaryGuid = parts[0].Guid,
            SecondaryGuids = new List<string> { parts[2].Guid },
        };
        model.CreateConnections(new[] { pair }, apply: true);

        var before = model.GetConnections(parts[0].Guid).Count;
        var replaced = model.CreateConnections(new[]
        {
            new ConnectionSpec
            {
                Name = "40К2 т0100",
                Number = -1,
                PrimaryGuid = parts[0].Guid,
                SecondaryGuids = new List<string> { parts[2].Guid },
                ReplaceExisting = true,
            },
        }, apply: true);

        Assert.Equal(1, replaced.CreatedCount);
        Assert.Equal(1, replaced.DeletedCount);

        var after = model.GetConnections(parts[0].Guid);
        Assert.Equal(before, after.Count);
        Assert.DoesNotContain(after, c => c.Name == "Стойка-ригель");
        Assert.Contains(after, c => c.Name == "40К2 т0100");
    }

    [Fact]
    public void Replace_existing_preview_reports_the_deletion_without_performing_it()
    {
        var model = new MockTeklaModelService();
        var parts = model.GetAllObjects().Take(3).ToList();
        model.CreateConnections(new[]
        {
            new ConnectionSpec
            {
                Name = "Стойка-ригель",
                PrimaryGuid = parts[0].Guid,
                SecondaryGuids = new List<string> { parts[2].Guid },
            },
        }, apply: true);
        var before = model.GetConnections(parts[0].Guid).Count;

        var preview = model.CreateConnections(new[]
        {
            new ConnectionSpec
            {
                Name = "40К2 т0100",
                PrimaryGuid = parts[0].Guid,
                SecondaryGuids = new List<string> { parts[2].Guid },
                ReplaceExisting = true,
            },
        }, apply: false);

        Assert.False(preview.Applied);
        Assert.Equal(0, preview.DeletedCount);
        Assert.Contains("would be deleted", preview.Message);
        Assert.Equal(before, model.GetConnections(parts[0].Guid).Count);
    }

    [Fact]
    public void Get_properties_separates_unknown_names_from_empty_values()
    {
        var model = new MockTeklaModelService();
        var part = model.GetAllObjects().First();

        var result = model.GetProperties(part.Guid, new[] { "NAME", "NOT_A_REAL_PROPERTY" });

        Assert.True(result.Udas.ContainsKey("NAME"));
        Assert.Equal("NOT_A_REAL_PROPERTY", Assert.Single(result.NotFound));
        // Some names resolved, so no "nothing matched" guidance is emitted.
        Assert.True(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public void Get_properties_explains_itself_when_nothing_resolves()
    {
        var model = new MockTeklaModelService();
        var part = model.GetAllObjects().First();

        var result = model.GetProperties(part.Guid, new[] { "NOPE_1", "NOPE_2" });

        Assert.Empty(result.Udas);
        Assert.Equal(2, result.NotFound.Count);
        Assert.Contains("tekla_find_attributes_by_value", result.Message);
    }
}
