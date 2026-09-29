using TeklaMcp.Scripting;
using Xunit;

namespace TeklaMcp.Tests;

public class ScriptApprovalGateTests
{
    private const string Mutation = "var b = new Beam(); b.Insert(); new Model().CommitChanges();";

    [Fact]
    public void Read_only_run_needs_no_hash()
    {
        Assert.Null(ScriptApprovalGate.Check("1 + 1", allowMutations: false, expectedSha256: null));
    }

    [Fact]
    public void Mutating_run_without_hash_is_rejected_unexecuted()
    {
        var result = ScriptApprovalGate.Check(Mutation, allowMutations: true, expectedSha256: "  ");

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("policy", result.Stage);
        Assert.False(result.ExecutionAttempted);
        Assert.False(result.CompilationAttempted);
        Assert.Contains(result.PolicyViolations, v => v.Contains("requires expectedSha256"));
        Assert.Contains("Insert", result.DetectedMutatingMembers);
        Assert.Equal(ScriptEngine.ComputeCodeSha256(Mutation), result.CodeSha256);
    }

    [Fact]
    public void Mutating_run_with_the_approved_hash_proceeds()
    {
        var hash = ScriptEngine.ComputeCodeSha256(Mutation);

        Assert.Null(ScriptApprovalGate.Check(Mutation, allowMutations: true, expectedSha256: hash));
        Assert.Null(ScriptApprovalGate.Check(Mutation, true, " SHA256:" + hash.ToUpperInvariant() + " "));
    }

    [Fact]
    public void Script_edited_after_approval_is_rejected()
    {
        var approved = ScriptEngine.ComputeCodeSha256(Mutation);
        var edited = Mutation + " // tweak";

        var result = ScriptApprovalGate.Check(edited, allowMutations: true, expectedSha256: approved);

        Assert.NotNull(result);
        Assert.Contains(result!.PolicyViolations, v => v.Contains("does not match"));
        Assert.Equal(ScriptEngine.ComputeCodeSha256(edited), result.CodeSha256);
    }

    [Fact]
    public void Read_only_run_with_a_stale_hash_is_rejected_too()
    {
        var result = ScriptApprovalGate.Check("2 + 2", allowMutations: false,
            expectedSha256: ScriptEngine.ComputeCodeSha256("1 + 1"));

        Assert.NotNull(result);
        Assert.Contains(result!.PolicyViolations, v => v.Contains("does not match"));
    }
}
