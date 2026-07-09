using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using TeklaMcp.Server.Tools;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Step 1 feedback fixes: friendly type aliases (Bolt => BoltArray), the AttributeNotEquals
/// filter, and grouping/distinct over an arbitrary attribute (e.g. BOLT_GRADE). Exercised
/// against the mock, whose bolts mirror the live backend (Type "BoltArray", grade in BOLT_GRADE).
/// </summary>
public class ObjectQueryFilterTests
{
    private readonly MockTeklaModelService _service = new();

    // ---- TeklaTypeAliases (pure) ---------------------------------------------------------

    [Theory]
    [InlineData("BoltArray", "Bolt", true)]
    [InlineData("BoltGroup", "Bolt", true)]
    [InlineData("BoltArray", "bolt", true)]      // case-insensitive
    [InlineData("ContourPlate", "Plate", true)]
    [InlineData("Beam", "Bolt", false)]
    [InlineData("BoltArray", "Beam", false)]
    [InlineData("Beam", "Beam", true)]           // exact still works
    [InlineData("BoltArray", "BoltArray", true)] // concrete name still works
    public void TypeMatches_honors_aliases(string concrete, string queried, bool expected)
        => Assert.Equal(expected, TeklaTypeAliases.TypeMatches(concrete, queried));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TypeMatches_blank_query_matches_anything(string? queried)
        => Assert.True(TeklaTypeAliases.TypeMatches("BoltArray", queried));

    // ---- Type alias through the mock -----------------------------------------------------

    [Fact]
    public void FindObjects_type_Bolt_matches_BoltArray_objects()
    {
        var byAlias = _service.FindObjects(new ObjectQuery { Type = "Bolt" }, limit: 1000);
        var byConcrete = _service.FindObjects(new ObjectQuery { Type = "BoltArray" }, limit: 1000);

        Assert.NotEmpty(byAlias);
        Assert.All(byAlias, o => Assert.Equal("BoltArray", o.Type));
        Assert.Equal(byConcrete.Count, byAlias.Count);
    }

    [Fact]
    public void FindObjects_type_Plate_matches_ContourPlate_objects()
    {
        var plates = _service.FindObjects(new ObjectQuery { Type = "Plate" }, limit: 1000);

        Assert.NotEmpty(plates);
        Assert.All(plates, o => Assert.Equal("ContourPlate", o.Type));
    }

    // ---- AttributeNotEquals --------------------------------------------------------------

    [Fact]
    public void FindObjects_attributeNotEquals_excludes_matching_grade()
    {
        var allBolts = _service.FindObjects(new ObjectQuery { Type = "Bolt" }, limit: 1000);
        var not88 = _service.FindObjects(
            new ObjectQuery { Type = "Bolt", AttributeName = "BOLT_GRADE", AttributeNotEquals = "88" },
            limit: 1000);

        Assert.NotEmpty(not88);
        Assert.All(not88, o => Assert.NotEqual("88", GradeOf(o)));
        // The mock alternates 8.8 / 10.9, so "not 88" is a strict, non-empty subset.
        Assert.True(not88.Count < allBolts.Count);
    }

    [Fact]
    public void FindObjects_attributeName_alone_requires_attribute_present()
    {
        // Bolts have BOLT_GRADE set; beams do not.
        var withGrade = _service.FindObjects(new ObjectQuery { AttributeName = "BOLT_GRADE" }, limit: 1000);

        Assert.NotEmpty(withGrade);
        Assert.All(withGrade, o => Assert.Equal("BoltArray", o.Type));
    }

    // ---- Distinct / group over an arbitrary attribute ------------------------------------

    [Fact]
    public void ListDistinctValues_over_BOLT_GRADE_returns_grade_buckets()
    {
        var rows = ModelWorkflowTools.ListDistinctValues(_service, field: "BOLT_GRADE", type: "Bolt");

        var keys = rows.Select(r => r.Key).ToList();
        Assert.Contains("88", keys);
        Assert.Contains("109", keys);
        var totalBolts = _service.FindObjects(new ObjectQuery { Type = "Bolt" }, limit: 1000).Count;
        Assert.Equal(totalBolts, rows.Sum(r => r.Count));
    }

    private string GradeOf(ModelObjectInfo o)
        => _service.GetProperties(o.Guid, new[] { "BOLT_GRADE" }).Udas.TryGetValue("BOLT_GRADE", out var v) ? v : "";
}
