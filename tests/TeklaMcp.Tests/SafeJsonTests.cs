using System.Collections.Generic;
using TeklaMcp.Scripting;
using Xunit;

namespace TeklaMcp.Tests;

public class SafeJsonTests
{
    [Theory]
    [InlineData(null, "null")]
    [InlineData(true, "true")]
    [InlineData(42, "42")]
    [InlineData(3.5, "3.5")]
    [InlineData("hi", "\"hi\"")]
    public void Renders_primitives(object? value, string expected)
    {
        Assert.Equal(expected, SafeJson.ToJson(value));
    }

    [Fact]
    public void Renders_anonymous_objects_and_lists()
    {
        var json = SafeJson.ToJson(new { Beams = 3, Names = new List<string> { "a", "b" } });
        Assert.Equal("{\"Beams\":3,\"Names\":[\"a\",\"b\"]}", json);
    }

    [Fact]
    public void Renders_dictionaries()
    {
        var json = SafeJson.ToJson(new Dictionary<string, int> { ["IPE300"] = 7 });
        Assert.Equal("{\"IPE300\":7}", json);
    }

    [Fact]
    public void Escapes_control_characters()
    {
        Assert.Equal("\"a\\\"b\\nc\"", SafeJson.ToJson("a\"b\nc"));
    }

    [Fact]
    public void Caps_item_count()
    {
        var many = new List<int>();
        for (var i = 0; i < 500; i++) many.Add(i);
        var json = SafeJson.ToJson(many);
        Assert.Contains("capped", json);
    }

    private sealed class Throwing
    {
        public int Fine => 1;
        public string Boom => throw new System.InvalidOperationException("nope");
    }

    [Fact]
    public void Survives_throwing_properties()
    {
        var json = SafeJson.ToJson(new Throwing());
        Assert.Contains("\"Fine\":1", json);
        Assert.Contains("threw", json);
    }

    private sealed class Cyclic
    {
        public Cyclic? Next { get; set; }
        public override string ToString() => "cyclic";
    }

    [Fact]
    public void Depth_cap_stops_cycles()
    {
        var a = new Cyclic();
        a.Next = a;
        var json = SafeJson.ToJson(a); // must terminate
        Assert.Contains("cyclic", json);
    }

    [Fact]
    public void Total_size_truncation_still_returns_valid_json()
    {
        var manyLargeStrings = new List<string>();
        for (var i = 0; i < 100; i++)
            manyLargeStrings.Add(new string((char)('a' + i % 20), 4_000));

        var json = SafeJson.ToJson(manyLargeStrings);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Contains("Return a smaller", parsed.RootElement.GetProperty("guidance").GetString());
        Assert.True(json.Length < 64_000);
    }
}
