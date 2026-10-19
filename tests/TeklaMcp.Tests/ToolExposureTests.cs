using TeklaMcp.Core;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// The operator switches TEKLA_MCP_READ_ONLY and TEKLA_MCP_TOOLSETS. Read-only is a safety switch,
/// so anything that is not clearly "off" must turn it on; a misspelled toolset must stop the
/// server instead of hiding tools. (Which tools each mode serves is checked by the MCP smoke.)
/// </summary>
public class ToolExposureTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("No")]
    [InlineData("OFF")]
    public void Read_only_is_off_when_unset_or_clearly_off(string? value)
    {
        Assert.False(ToolExposure.ParseReadOnly(value, out var warning));
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData(" TRUE ")]
    [InlineData("yes")]
    [InlineData("on")]
    public void Read_only_is_on_for_the_on_spellings(string value)
    {
        Assert.True(ToolExposure.ParseReadOnly(value, out var warning));
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("disable")]
    [InlineData("2")]
    public void Unrecognized_read_only_value_fails_closed_with_a_warning(string value)
    {
        Assert.True(ToolExposure.ParseReadOnly(value, out var warning));
        Assert.Contains("read-only mode is ON", warning);
        Assert.Contains(value, warning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    [InlineData("all")]
    [InlineData("drawings,all")]
    public void No_toolset_selection_means_all_tools(string? value)
    {
        Assert.Null(ToolExposure.ParseToolsets(value, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Toolsets_are_case_insensitive_ordered_and_always_include_core()
    {
        var toolsets = ToolExposure.ParseToolsets("Visual; drawings model", out var error);

        Assert.Null(error);
        Assert.Equal(new[] { "core", "model", "drawings", "visual" }, toolsets);
    }

    [Fact]
    public void Unknown_toolset_is_an_error_not_a_silent_omission()
    {
        Assert.Null(ToolExposure.ParseToolsets("model,drawing", out var error));
        Assert.Contains("'drawing'", error);
        Assert.Contains("drawings", error); // the list of known names
    }

    [Fact]
    public void Read_only_refusal_names_the_switch_and_says_nothing_happened()
    {
        var refusal = ToolExposure.ReadOnlyRefusal("tekla_delete_objects");

        Assert.Contains("tekla_delete_objects", refusal);
        Assert.Contains(ToolExposure.ReadOnlyVariable, refusal);
        Assert.Contains("Nothing was written or changed", refusal);
    }

    [Fact]
    public void Mode_notice_is_empty_for_the_full_tool_set()
    {
        Assert.Equal("", ToolExposure.DescribeMode(readOnly: false, toolsets: null));
    }

    [Fact]
    public void Mode_notice_announces_read_only_and_the_missing_toolsets()
    {
        var notice = ToolExposure.DescribeMode(readOnly: true, toolsets: new[] { "core", "drawings" });

        Assert.Contains("READ-ONLY MODE", notice);
        Assert.Contains("tekla_run_csharp", notice);
        Assert.Contains("TEKLA_MCP_TOOLSETS=core,drawings", notice);
        Assert.Contains("model, analytics, files, scripting, visual", notice);
    }
}
