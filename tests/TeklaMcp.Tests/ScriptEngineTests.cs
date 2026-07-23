using Microsoft.CodeAnalysis.CSharp.Scripting;
using TeklaMcp.Core.Models;
using TeklaMcp.Scripting;
using Xunit;

namespace TeklaMcp.Tests;

public class ScriptEngineTests
{
    [Fact]
    public void Runtime_exception_is_still_reported_as_execution_attempt()
    {
        var script = CSharpScript.Create<object>(
            "throw new System.InvalidOperationException(\"boom\");",
            globalsType: typeof(ScriptGlobals));
        var result = new ScriptResult();

        ScriptEngine.Run(script, new ScriptGlobals(), timeoutSeconds: 5, result);

        Assert.True(result.ExecutionAttempted);
        Assert.True(result.Executed);
        Assert.False(result.Success);
        Assert.Contains("InvalidOperationException", result.Error);
    }

    [Fact]
    public void Successful_return_is_serialized_in_execution_worker()
    {
        var script = CSharpScript.Create<object>(
            "new { Answer = 42 }",
            globalsType: typeof(ScriptGlobals));
        var result = new ScriptResult();

        ScriptEngine.Run(script, new ScriptGlobals(), timeoutSeconds: 5, result);

        Assert.True(result.Success);
        Assert.True(result.ExecutionAttempted);
        Assert.True(result.Executed);
        Assert.Equal("{\"Answer\":42}", result.ReturnValueJson);
    }
}
