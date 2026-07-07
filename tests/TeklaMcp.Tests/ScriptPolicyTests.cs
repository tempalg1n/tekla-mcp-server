using System.Linq;
using TeklaMcp.Scripting;
using Xunit;

namespace TeklaMcp.Tests;

public class ScriptPolicyTests
{
    private const string InnocentScript =
        "var model = new Model();\n" +
        "var e = model.GetModelObjectSelector().GetAllObjectsWithType(ModelObject.ModelObjectEnum.BEAM);\n" +
        "var n = 0;\n" +
        "while (e.MoveNext()) n++;\n" +
        "Print(n);\n" +
        "new { Beams = n }\n";

    [Fact]
    public void Allows_innocent_read_script()
    {
        Assert.Empty(ScriptPolicy.Validate(InnocentScript, allowMutations: false));
    }

    [Theory]
    [InlineData("File.ReadAllText(\"/etc/passwd\")")]
    [InlineData("System.IO.File.Delete(\"x\")")]
    [InlineData("var p = new Process();")]
    [InlineData("Environment.Exit(1);")]
    [InlineData("var c = new HttpClient();")]
    [InlineData("typeof(int).Assembly.GetTypes();")]
    [InlineData("Console.WriteLine(\"breaks the MCP protocol\");")]
    [InlineData("var t = new Thread(() => {});")]
    public void Bans_dangerous_identifiers(string code)
    {
        Assert.NotEmpty(ScriptPolicy.Validate(code, allowMutations: false));
    }

    [Theory]
    [InlineData("#r \"System.Net.Http\"\nvar x = 1;")]
    [InlineData("#load \"other.csx\"\nvar x = 1;")]
    public void Bans_preprocessor_directives(string code)
    {
        Assert.Contains(ScriptPolicy.Validate(code, allowMutations: false),
            v => v.Contains("Preprocessor"));
    }

    [Fact]
    public void Bans_await()
    {
        Assert.Contains(ScriptPolicy.Validate("await System.Something.Do();", allowMutations: false),
            v => v.Contains("await"));
    }

    [Fact]
    public void Bans_disallowed_usings()
    {
        var violations = ScriptPolicy.Validate("using System.Xml;\nvar x = 1;", allowMutations: false);
        Assert.Contains(violations, v => v.Contains("using System.Xml"));
    }

    [Fact]
    public void Allows_tekla_usings()
    {
        Assert.Empty(ScriptPolicy.Validate(
            "using Tekla.Structures.Model;\nusing System.Linq;\nvar x = 1;\nx", allowMutations: false));
    }

    [Fact]
    public void Bans_mutations_by_default_with_actionable_message()
    {
        var violations = ScriptPolicy.Validate("var b = new Beam(); b.Insert();", allowMutations: false);
        var v = Assert.Single(violations);
        Assert.Contains("Insert", v);
        Assert.Contains("allowMutations", v);
        // The message must route the agent through user confirmation, not around it.
        Assert.Contains("go-ahead", v);
    }

    [Fact]
    public void Allows_mutations_when_flag_set()
    {
        Assert.Empty(ScriptPolicy.Validate(
            "var b = new Beam(); b.Insert(); new Model().CommitChanges();", allowMutations: true));
    }

    [Fact]
    public void Rejects_empty_and_oversized_scripts()
    {
        Assert.NotEmpty(ScriptPolicy.Validate("", allowMutations: false));
        Assert.NotEmpty(ScriptPolicy.Validate(new string('x', ScriptPolicy.MaxCodeLength + 1), allowMutations: false));
    }

    [Fact]
    public void Violations_are_deduplicated_and_capped()
    {
        var code = string.Concat(Enumerable.Repeat("File.Exists(\"x\");\n", 50));
        var violations = ScriptPolicy.Validate(code, allowMutations: false);
        Assert.Single(violations); // same message reported once
    }
}
