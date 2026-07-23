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
    [InlineData("Type.GetType(\"System.Console\").GetMethod(\"WriteLine\").Invoke(null, null);")]
    [InlineData("dynamic value = GetSomething();")]
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
    public void Allows_tekla_assembly_and_task_type_names()
    {
        Assert.Empty(ScriptPolicy.Validate(
            "var a = new Tekla.Structures.Model.Assembly();\n" +
            "var t = new Tekla.Structures.Model.Task();\n" +
            "new { a, t }",
            allowMutations: false));
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
    public void Reports_mutating_members_even_when_allowed()
    {
        var analysis = ScriptPolicy.Analyze(
            "var b = new Beam(); b.Insert(); new Model().CommitChanges();",
            allowMutations: true);

        Assert.Empty(analysis.Violations);
        Assert.Equal(new[] { "CommitChanges", "Insert" }, analysis.MutatingMembers);
    }

    [Theory]
    [InlineData("new DrawingHandler().SaveActiveDrawing();", "SaveActiveDrawing")]
    [InlineData("new DrawingHandler().IssueDrawing(drawing);", "IssueDrawing")]
    [InlineData("handler.CreateDimensionSet(view, points, vector, 10.0);", "CreateDimensionSet")]
    [InlineData("hideable.HideFromDrawing();", "HideFromDrawing")]
    [InlineData("task.AddObjectsToTask(ids);", "AddObjectsToTask")]
    [InlineData("obj.SetUserProperties(strings, doubles, ints);", "SetUserProperties")]
    [InlineData("new ModelHandler().Save(\"\", \"\");", "Save")]
    [InlineData("ModelObjectEnumerator.AutoFetch = false;", "AutoFetch")]
    [InlineData("mark.MergeMarks(other);", "MergeMarks")]
    [InlineData("drawingObject.Scale(2.0);", "Scale")]
    public void Detects_drawing_and_model_mutations(string code, string expectedMember)
    {
        var analysis = ScriptPolicy.Analyze(code, allowMutations: false);

        Assert.Contains(expectedMember, analysis.MutatingMembers);
        Assert.Contains(analysis.Violations, v => v.Contains(expectedMember));
    }

    [Fact]
    public void Drawing_namespace_is_not_a_global_import()
    {
        Assert.DoesNotContain("Tekla.Structures.Drawing", ScriptEngine.DefaultImports);
    }

    [Fact]
    public void Does_not_treat_property_reads_as_mutations()
    {
        Assert.Empty(ScriptPolicy.Validate(
            "var scale = view.Attributes.Scale;\n" +
            "var autoFetch = ModelObjectEnumerator.AutoFetch;\n" +
            "new { scale, autoFetch }",
            allowMutations: false));
    }

    [Fact]
    public void Code_hash_is_stable_sha256()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ScriptEngine.ComputeCodeSha256("abc"));
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
