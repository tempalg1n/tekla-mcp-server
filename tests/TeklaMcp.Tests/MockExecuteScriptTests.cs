using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

public class MockExecuteScriptTests
{
    private readonly MockTeklaModelService _service = new();

    [Fact]
    public void Valid_script_passes_policy_but_is_not_executed()
    {
        var result = _service.ExecuteScript("var model = new Model();\nnew { Ok = true }");

        Assert.True(result.Success);
        Assert.False(result.Executed);
        Assert.Equal("Mock", result.Backend);
        Assert.Empty(result.PolicyViolations);
        Assert.Contains("NOT executed", result.Guidance);
    }

    [Fact]
    public void Policy_violation_is_reported_with_stage()
    {
        var result = _service.ExecuteScript("File.ReadAllText(\"x\");");

        Assert.False(result.Success);
        Assert.False(result.Executed);
        Assert.Equal("policy", result.Stage);
        Assert.NotEmpty(result.PolicyViolations);
    }

    [Fact]
    public void Mutation_without_flag_is_rejected()
    {
        var result = _service.ExecuteScript("new Beam().Insert();");

        Assert.False(result.Success);
        Assert.Contains(result.PolicyViolations, v => v.Contains("allowMutations"));
    }
}
