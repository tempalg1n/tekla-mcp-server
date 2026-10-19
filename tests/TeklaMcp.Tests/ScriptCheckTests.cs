using System;
using TeklaMcp.Scripting;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>Approval by reference and a whitespace-tolerant hash (issue #40).</summary>
public class ScriptCheckTests
{
    [Theory]
    [InlineData("var a = 1;\nreturn a;", "var a = 1;\r\nreturn a;\r\n")]
    [InlineData("var a = 1;\nreturn a;", "var a = 1;   \nreturn a;\n\n")]
    [InlineData("var a = 1;\nreturn a;", "var a = 1;\rreturn a;\t")]
    public void Line_endings_and_trailing_whitespace_do_not_change_the_hash(string a, string b) =>
        Assert.Equal(ScriptEngine.ComputeCodeSha256(a), ScriptEngine.ComputeCodeSha256(b));

    [Theory]
    [InlineData("var a = 1;", "var a = 2;")]
    [InlineData("var a = 1;", "var  a = 1;")]   // inner whitespace counts
    [InlineData("var a = 1;", "  var a = 1;")]  // indentation counts
    public void Everything_else_changes_the_hash(string a, string b) =>
        Assert.NotEqual(ScriptEngine.ComputeCodeSha256(a), ScriptEngine.ComputeCodeSha256(b));

    [Fact]
    public void Checked_scripts_are_found_by_id_until_they_expire()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var store = new ScriptCheckStore(TimeSpan.FromHours(1), () => now);
        var id = store.Add("return 1;");

        Assert.StartsWith("chk-", id);
        Assert.Equal("return 1;", store.Get(id));
        Assert.Equal(id, store.Add("return 1;\n")); // same normalized text, same id
        Assert.Null(store.Get("chk-0000000000000000"));
        Assert.Null(store.Get(""));

        now = now.AddHours(2);
        Assert.Null(store.Get(id));
    }

    [Fact]
    public void The_store_keeps_at_most_its_capacity()
    {
        var store = new ScriptCheckStore();
        var first = store.Add("return 0;");
        for (var i = 1; i <= ScriptCheckStore.Capacity; i++) store.Add("return " + i + ";");
        Assert.Null(store.Get(first));
    }

    [Theory]
    [InlineData("var e = model.GetModelObjectSelector().GetAllObjects();\nwhile (e.MoveNext()) Print(e.Current.GetType().Name);")]
    [InlineData("var t = obj.GetType().FullName;\nt")]
    public void Reading_a_type_name_is_allowed(string code) =>
        Assert.Empty(ScriptPolicy.Validate(code, allowMutations: false));

    [Theory]
    [InlineData("obj.GetType().Assembly")]
    [InlineData("obj.GetType().GetMethod(\"X\")")]
    [InlineData("var t = obj.GetType();\nt.Name")]
    [InlineData("Type.GetType(\"System.Console\").Name")]
    public void Other_uses_of_GetType_stay_banned(string code) =>
        Assert.NotEmpty(ScriptPolicy.Validate(code, allowMutations: false));
}
