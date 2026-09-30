using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// tekla_create_component (backlog §5): a malformed spec is refused before Tekla is touched, and
/// the mock never pretends a plugin ran.
/// </summary>
public class ComponentSpecTests
{
    private static ComponentInputSpec Point(double x) =>
        new ComponentInputSpec { Type = "point", Points = { new Point3D(x, 0, 0) } };

    private static ComponentSpec Plugin(params ComponentInputSpec[] inputs) =>
        new ComponentSpec { Name = "KXMp_Handrail", Inputs = inputs.ToList() };

    [Fact]
    public void Valid_plugin_spec_passes()
    {
        var spec = Plugin(
            new ComponentInputSpec { Type = "two_points", Points = { new Point3D(0, 0, 0), new Point3D(1000, 0, 0) } },
            new ComponentInputSpec { Type = "object", Guid = "abc" });
        spec.Attributes.Add(new ComponentAttributeSpec { Name = "height", Value = "1100.5", Type = "double" });
        Assert.Empty(ComponentSpecs.Validate(spec));
    }

    [Theory]
    [InlineData("point", 2)]
    [InlineData("two_points", 1)]
    [InlineData("polygon", 2)]
    public void Wrong_point_counts_are_refused(string type, int points)
    {
        var input = new ComponentInputSpec { Type = type, Points = Enumerable.Range(0, points).Select(i => new Point3D(i, 0, 0)).ToList() };
        Assert.Single(ComponentSpecs.Validate(Plugin(input)));
    }

    [Fact]
    public void Every_problem_is_listed_at_once()
    {
        var spec = new ComponentSpec
        {
            Kind = "system",
            Inputs = { new ComponentInputSpec { Type = "object" }, new ComponentInputSpec { Type = "spline" } },
            Attributes = { new ComponentAttributeSpec { Name = "n", Value = "1,5", Type = "double" } },
        };
        var errors = ComponentSpecs.Validate(spec);

        Assert.Contains(errors, e => e.Contains("number > 0"));
        Assert.Contains(errors, e => e.Contains("inputs[0]") && e.Contains("guid"));
        Assert.Contains(errors, e => e.Contains("inputs[1]") && e.Contains("spline"));
        Assert.Contains(errors, e => e.Contains("'.'"));
    }

    [Fact]
    public void Empty_inputs_are_refused()
    {
        Assert.Contains(ComponentSpecs.Validate(new ComponentSpec { Name = "X" }), e => e.Contains("inputs is empty"));
    }

    [Theory]
    [InlineData("Two-Points", "two_points")]
    [InlineData("position", "point")]
    [InlineData("part", "object")]
    public void Input_type_aliases(string raw, string expected)
    {
        Assert.Equal(expected, ComponentSpecs.NormalizeInputType(raw));
    }

    [Fact]
    public void Mock_records_the_component_but_does_not_invent_children()
    {
        var mock = new MockTeklaModelService();
        var host = mock.FindObjects(new ObjectQuery { Type = "Beam" }, 1).First();
        var spec = Plugin(Point(0), new ComponentInputSpec { Type = "object", Guid = host.Guid });

        var preview = mock.CreateComponents(new[] { spec }, apply: false);
        Assert.Equal(0, preview.CreatedCount);
        Assert.Equal("(preview)", preview.ComponentPreview.Single().Guid);

        var applied = mock.CreateComponents(new[] { spec }, apply: true);
        var component = applied.ComponentPreview.Single();
        Assert.Equal(1, applied.CreatedCount);
        Assert.Equal(0, component.ChildCount);
        Assert.Equal(host.Guid, component.PrimaryGuid);
        Assert.Contains("no plugin code runs", applied.Message);
    }

    [Fact]
    public void Mock_refuses_missing_input_objects()
    {
        var result = new MockTeklaModelService().CreateComponents(
            new[] { Plugin(new ComponentInputSpec { Type = "object", Guid = "nope" }) }, apply: true);
        Assert.Equal(0, result.CreatedCount);
        Assert.Contains("not found", Assert.Single(result.Errors));
    }
}
