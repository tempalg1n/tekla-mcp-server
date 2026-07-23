using System.Linq;
using TeklaMcp.Scripting;
using Xunit;

namespace TeklaMcp.Tests;

public class ScriptGlobalsTests
{
    [Fact]
    public void Snapshot_is_a_copy_not_host_storage()
    {
        var globals = new ScriptGlobals();
        globals.Print("host-owned");

        var first = globals.SnapshotOutput();
        first.Clear();
        first.Add("injected");

        Assert.Equal(new[] { "host-owned" }, globals.SnapshotOutput());
    }

    [Fact]
    public void Print_caps_lines_and_total_characters()
    {
        var globals = new ScriptGlobals();
        for (var i = 0; i < 1_000; i++)
            globals.Print(new string('x', 2_000));

        var output = globals.SnapshotOutput();
        Assert.True(output.Count <= 500);
        Assert.True(output.Sum(line => line.Length) <= 64_000);
        Assert.Contains(output, line => line.Contains("output capped"));
    }

    private sealed class Throwing
    {
        public override string ToString() => throw new System.InvalidOperationException("boom");
    }

    [Fact]
    public void Print_survives_throwing_tostring()
    {
        var globals = new ScriptGlobals();
        globals.Print(new Throwing());

        Assert.Contains("ToString threw", Assert.Single(globals.SnapshotOutput()));
    }
}
