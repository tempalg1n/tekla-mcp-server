using System.Collections.Generic;
using System.Text.Json;
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

        using var parsed = JsonDocument.Parse(json);
        var node = parsed.RootElement;
        while (node.ValueKind == JsonValueKind.Object)
            node = node.GetProperty("Next");
        // Bottoms out in the explicit marker, not the object's ToString() ("cyclic").
        Assert.Equal("[depth limit reached: Cyclic]", node.GetString());
    }

    [Fact]
    public void Size_cap_bounds_self_referencing_fan_out()
    {
        // 100 self-references per level is 100^6 paths within the depth cap; the total-size
        // cap must still end the walk quickly with valid JSON.
        var list = new List<object>();
        for (var i = 0; i < 100; i++) list.Add(list);

        var json = SafeJson.ToJson(list);

        using var parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Renders_nested_coordinate_lists_as_numbers()
    {
        // Common script shape: parts → segments → coordinates, five container levels. Under the
        // old 4-level cap the coordinate lists came back as "System.Collections.Generic.List`1[…]".
        var parts = new List<object>
        {
            new
            {
                Part = "B1",
                Segments = new List<object>
                {
                    new { Start = new List<double> { 0, 0, 0 }, End = new[] { 6000.5, 0, 3000 } },
                },
            },
        };

        Assert.Equal(
            "[{\"Part\":\"B1\",\"Segments\":[{\"Start\":[0,0,0],\"End\":[6000.5,0,3000]}]}]",
            SafeJson.ToJson(parts));
    }

    [Fact]
    public void Depth_cap_is_six_container_levels()
    {
        object nested = new List<double> { 1.5 };
        for (var level = 1; level < 6; level++)
            nested = new List<object> { nested };

        Assert.Equal("[[[[[[1.5]]]]]]", SafeJson.ToJson(nested));
        Assert.Equal(
            "[[[[[[\"[depth limit reached: List<Double> with 1 item]\"]]]]]]",
            SafeJson.ToJson(new List<object> { nested }));
    }

    [Fact]
    public void Depth_cap_marks_containers_but_keeps_leaves()
    {
        var segment = new
        {
            Start = new List<double> { 0, 0, 0 },
            End = new[] { 6000.5, 0, 3000 },
            Meta = new { Source = "contour" },
            Length = 6000.5,
            Name = "S1",
            Day = System.DayOfWeek.Monday,
            Id = System.Guid.Empty,
        };
        // root → Model → Parts → part → Segments → segment: its members sit past the cap.
        var root = new { Model = new { Parts = new List<object> { new { Segments = new List<object> { segment } } } } };

        var json = SafeJson.ToJson(root);

        using var parsed = JsonDocument.Parse(json);
        var rendered = parsed.RootElement
            .GetProperty("Model").GetProperty("Parts")[0].GetProperty("Segments")[0];
        Assert.Equal(
            "{\"Start\":\"[depth limit reached: List<Double> with 3 items]\"," +
            "\"End\":\"[depth limit reached: Double[] with 3 items]\"," +
            "\"Meta\":\"[depth limit reached: anonymous object]\"," +
            "\"Length\":6000.5,\"Name\":\"S1\",\"Day\":\"Monday\"," +
            "\"Id\":\"00000000-0000-0000-0000-000000000000\"}",
            rendered.GetRawText());
        // Never a CLR type name or ToString() output masquerading as data.
        Assert.DoesNotContain("System.", json);
        Assert.DoesNotContain("AnonymousType", json);
    }

    [Fact]
    public void Report_is_clean_for_a_complete_value()
    {
        SafeJson.ToJson(new { Rows = new List<int> { 1, 2, 3 }, Name = "ok" }, out var report);

        Assert.False(report.Truncated);
        Assert.Empty(report.Notes());
    }

    [Fact]
    public void Report_flags_item_cap_that_the_json_alone_hides()
    {
        // Field report: 141 rows came back as the first 100 and were accepted as the full audit.
        var rows = new List<int>();
        for (var i = 0; i < 141; i++) rows.Add(i);

        var json = SafeJson.ToJson(rows, out var report);

        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(json).RootElement.ValueKind);
        Assert.True(report.Truncated);
        Assert.Equal(1, report.CappedCollections);
        Assert.Equal(141, report.LargestCappedCollection);
        Assert.Contains(report.Notes(), n => n.Contains("first 100 items") && n.Contains("141"));
    }

    [Fact]
    public void Report_does_not_enumerate_lazy_sequences_for_their_size()
    {
        SafeJson.ToJson(System.Linq.Enumerable.Range(0, 1_000_000), out var report);

        Assert.Equal(1, report.CappedCollections);
        Assert.Equal(-1, report.LargestCappedCollection);
    }

    [Fact]
    public void Report_counts_cut_strings_with_original_length()
    {
        SafeJson.ToJson(new[] { new string('x', 4_500), "short", new string('y', 9_000) }, out var report);

        Assert.True(report.Truncated);
        Assert.Equal(2, report.TruncatedStrings);
        Assert.Equal(9_000, report.LongestTruncatedString);
    }

    [Fact]
    public void Report_counts_depth_markers()
    {
        object nested = new List<double> { 1.5 };
        for (var level = 0; level < 6; level++)
            nested = new List<object> { nested };

        SafeJson.ToJson(nested, out var report);

        Assert.True(report.Truncated);
        Assert.Equal(1, report.DepthLimitHits);
    }

    private sealed class Wide
    {
        public int P01 => 1; public int P02 => 2; public int P03 => 3; public int P04 => 4; public int P05 => 5;
        public int P06 => 6; public int P07 => 7; public int P08 => 8; public int P09 => 9; public int P10 => 10;
        public int P11 => 11; public int P12 => 12; public int P13 => 13; public int P14 => 14; public int P15 => 15;
        public int P16 => 16; public int P17 => 17; public int P18 => 18; public int P19 => 19; public int P20 => 20;
        public int P21 => 21; public int P22 => 22; public int P23 => 23; public int P24 => 24; public int P25 => 25;
        public int P26 => 26;
    }

    [Fact]
    public void Report_flags_property_cap()
    {
        var json = SafeJson.ToJson(new Wide(), out var report);

        Assert.Equal(25, JsonDocument.Parse(json).RootElement.EnumerateObject().Count());
        Assert.Equal(1, report.PropertyCappedObjects);
        Assert.True(report.Truncated);
    }

    [Fact]
    public void Report_tells_size_envelope_apart_from_a_script_returning_truncated_true()
    {
        SafeJson.ToJson(new { truncated = true }, out var honest);
        Assert.False(honest.Truncated);

        var big = new List<string>();
        for (var i = 0; i < 100; i++) big.Add(new string('a', 3_000));
        SafeJson.ToJson(big, out var cut);
        Assert.True(cut.SizeCapExceeded);
        Assert.True(cut.Truncated);
    }

    [Fact]
    public void Throwing_getters_are_noted_but_not_truncation()
    {
        SafeJson.ToJson(new Throwing(), out var report);

        Assert.False(report.Truncated);
        Assert.Equal(1, report.ThrowingProperties);
        Assert.Single(report.Notes());
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

    [Fact]
    public void Renders_value_tuples_as_Item_members()
    {
        // Element names (Name, Coords) are compile-time only; at runtime a ValueTuple has public
        // FIELDS Item1..ItemN and no properties, so it used to come back as its ToString():
        // "(B1, System.Collections.Generic.List`1[System.Double])".
        var coords = new List<double> { 1, 2 };

        Assert.Equal("{\"Item1\":\"B1\",\"Item2\":[1,2]}", SafeJson.ToJson((Name: "B1", Coords: coords)));
        Assert.Equal(
            "[{\"Item1\":\"B1\",\"Item2\":[1,2]},{\"Item1\":\"B2\",\"Item2\":[1,2]}]",
            SafeJson.ToJson(new[] { "B1", "B2" }.Select(b => (b, coords))));
    }

    [Fact]
    public void Flattens_long_tuples_into_consecutive_Item_members()
    {
        // The 8th element onwards lives in a nested Rest tuple; C# exposes it as Item8, Item9, ….
        Assert.Equal(
            "{\"Item1\":1,\"Item2\":2,\"Item3\":3,\"Item4\":4,\"Item5\":5,\"Item6\":6," +
            "\"Item7\":7,\"Item8\":8,\"Item9\":9}",
            SafeJson.ToJson((1, 2, 3, 4, 5, 6, 7, 8, 9)));
    }

    /// <summary>Shaped like Tekla's Geometry3d.Point: X/Y/Z are public fields, no properties.</summary>
    private class FieldPoint
    {
        public double X, Y, Z;

        public FieldPoint(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        // Rounded text like Tekla's (which also uses the current culture: "(1000,000, …)").
        public override string ToString() => FormattableString.Invariant($"({X:F3}, {Y:F3}, {Z:F3})");
    }

    /// <summary>Shaped like Tekla's ContourPoint: inherits the X/Y/Z fields, adds a property.</summary>
    private sealed class FieldContourPoint : FieldPoint
    {
        public FieldContourPoint(double x, double y, double z) : base(x, y, z) { }

        public string Chamfer { get; set; } = "none";
    }

    [Fact]
    public void Renders_public_fields_as_numbers()
    {
        Assert.Equal("{\"X\":1000,\"Y\":2000.5004,\"Z\":0}", SafeJson.ToJson(new FieldPoint(1000, 2000.5004, 0)));
    }

    [Fact]
    public void Renders_inherited_fields_after_properties()
    {
        // Properties alone gave {"Chamfer":"none"}: the coordinates were silently dropped.
        Assert.Equal(
            "{\"Chamfer\":\"none\",\"X\":10,\"Y\":20,\"Z\":30}",
            SafeJson.ToJson(new FieldContourPoint(10, 20, 30)));
    }

    private sealed class ManyMembers
    {
        public int P1 => 1;
        public int P2 => 2;
#pragma warning disable CS0649 // never assigned: only the member count matters
        public int F01, F02, F03, F04, F05, F06, F07, F08, F09, F10,
                   F11, F12, F13, F14, F15, F16, F17, F18, F19, F20,
                   F21, F22, F23, F24, F25, F26, F27, F28, F29, F30;
#pragma warning restore CS0649
    }

    [Fact]
    public void Member_cap_spans_properties_and_fields()
    {
        var json = SafeJson.ToJson(new ManyMembers(), out var report);

        using var parsed = JsonDocument.Parse(json);
        var names = parsed.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(25, names.Count);
        Assert.Equal(new[] { "P1", "P2", "F01" }, names.Take(3));
        Assert.Equal("F23", names[names.Count - 1]);
        // Dropped fields are reported exactly like dropped properties.
        Assert.Equal(1, report.PropertyCappedObjects);
        Assert.True(report.Truncated);
        Assert.Contains(report.Notes(), n => n.Contains("public members"));
    }

    private class BaseWithField
    {
        public int Value = 1;
    }

    private sealed class HidesField : BaseWithField
    {
        public new string Value => "derived";
    }

    [Fact]
    public void Hidden_members_are_not_repeated()
    {
        // Reflection returns both the property and the base field it hides; C# sees only the property.
        Assert.Equal("{\"Value\":\"derived\"}", SafeJson.ToJson(new HidesField()));
    }

    private sealed class FieldCyclic
    {
        public FieldCyclic? Next;
    }

    [Fact]
    public void Depth_cap_stops_cycles_through_fields()
    {
        var a = new FieldCyclic();
        a.Next = a;
        var json = SafeJson.ToJson(a); // must terminate

        using var parsed = JsonDocument.Parse(json);
        var node = parsed.RootElement;
        while (node.ValueKind == JsonValueKind.Object)
            node = node.GetProperty("Next");
        Assert.Equal("[depth limit reached: FieldCyclic]", node.GetString());
    }

    private sealed class FaultsMidSequence
    {
        public int Fine => 1;
        public IEnumerable<int> LazyProperty => Faulty();
        public IEnumerable<int> LazyField = Faulty();

        private static IEnumerable<int> Faulty()
        {
            yield return 1;
            throw new System.InvalidOperationException("remoting died");
        }
    }

    [Fact]
    public void Member_faulting_mid_value_keeps_json_valid()
    {
        // A lazy value (LINQ over model objects, a Tekla enumerator) can fault after part of it
        // was written; that partial "[1" must not stay in front of the error marker.
        var json = SafeJson.ToJson(new FaultsMidSequence(), out var report);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal(1, root.GetProperty("Fine").GetInt32());
        Assert.Equal("<threw: remoting died>", root.GetProperty("LazyProperty").GetString());
        Assert.Equal("<threw: remoting died>", root.GetProperty("LazyField").GetString());
        // Noted, but not truncation: the failure is visible in place.
        Assert.Equal(2, report.ThrowingProperties);
        Assert.False(report.Truncated);
    }

    private sealed class Opaque
    {
        public override string ToString() => "opaque";
    }

    [Fact]
    public void Falls_back_to_ToString_only_without_members()
    {
        Assert.Equal("\"opaque\"", SafeJson.ToJson(new Opaque()));
    }
}
