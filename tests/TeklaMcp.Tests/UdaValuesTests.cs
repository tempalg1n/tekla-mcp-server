using System;
using System.Collections.Generic;
using System.IO;
using TeklaMcp.Core;
using TeklaMcp.Core.FileExchange;
using TeklaMcp.Core.Models;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Typed UDA reads and writes (issue #21). <see cref="Tekla2021Getters"/> reproduces what live
/// Tekla 2021 answers (measured on 300 beams, 2026-10-16): every getter succeeds for a set
/// attribute, only the one matching the stored type has the value.
/// </summary>
public class UdaValuesTests
{
    /// <summary>One stored value (int, double, string) or null = not set; getters as on Tekla 2021.</summary>
    private sealed class Tekla2021Getters : IUdaGetters
    {
        private readonly object? _stored;
        public int StoredKindReads { get; private set; }

        public Tekla2021Getters(object? stored) => _stored = stored;

        public bool TryGetString(out string value)
        {
            value = _stored as string ?? "";
            return _stored != null;
        }

        public bool TryGetInteger(out int value)
        {
            value = _stored is int i ? i : _stored is double d ? (int)d : 0;
            return _stored != null;
        }

        public bool TryGetDouble(out double value)
        {
            value = _stored is double d ? d : _stored is int i ? i : 0;
            return _stored != null;
        }

        public UdaValueKind ReadStoredKind()
        {
            StoredKindReads++;
            return UdaValues.FromStoredValue(_stored);
        }
    }

    private static (bool Found, string Value, UdaValueKind Learned) Read(UdaValueKind kind, object? stored)
    {
        var found = UdaValues.TryRead(kind, new Tekla2021Getters(stored), out var value, out var learned);
        return (found, value, learned);
    }

    [Theory]
    [InlineData(UdaValueKind.Integer)]
    [InlineData(UdaValueKind.Unknown)]
    public void Integer_values_are_not_blank(UdaValueKind kind)
    {
        // The field case: the string getter answered true with "", the integer held 3.
        Assert.Equal((true, "3"), (Read(kind, 3).Found, Read(kind, 3).Value));
        Assert.Equal((true, "1"), (Read(kind, 1).Found, Read(kind, 1).Value));
    }

    [Theory]
    [InlineData(UdaValueKind.Integer)]
    [InlineData(UdaValueKind.Unknown)]
    public void Integer_zero_is_a_value_not_a_blank(UdaValueKind kind)
    {
        var read = Read(kind, 0);
        Assert.True(read.Found);
        Assert.Equal("0", read.Value);
    }

    [Theory]
    [InlineData(UdaValueKind.Double)]
    [InlineData(UdaValueKind.Unknown)]
    public void Doubles_keep_their_fraction(UdaValueKind kind) =>
        Assert.Equal("2.5", Read(kind, 2.5).Value);

    [Theory]
    [InlineData(UdaValueKind.String)]
    [InlineData(UdaValueKind.Unknown)]
    public void Blank_strings_stay_blank(UdaValueKind kind)
    {
        var read = Read(kind, "");
        Assert.True(read.Found);
        Assert.Equal("", read.Value);
    }

    [Fact]
    public void Text_in_a_string_attribute_is_never_a_number() =>
        // Tekla 2021's integer getter answers true/0 for a string: the declared type decides.
        Assert.Equal("STANCHION", Read(UdaValueKind.String, "STANCHION").Value);

    [Theory]
    [InlineData(UdaValueKind.String)]
    [InlineData(UdaValueKind.Integer)]
    [InlineData(UdaValueKind.Double)]
    [InlineData(UdaValueKind.Unknown)]
    public void Unset_attributes_are_not_found(UdaValueKind kind) =>
        Assert.False(Read(kind, null).Found);

    [Fact]
    public void A_number_written_into_a_string_attribute_still_reads()
    {
        // Servers up to v0.8.1 wrote "123" into a string UDA as an integer.
        Assert.Equal("123", Read(UdaValueKind.String, 123).Value);
    }

    [Fact]
    public void Undeclared_names_learn_their_stored_type_once()
    {
        var getters = new Tekla2021Getters(7);
        Assert.True(UdaValues.TryRead(UdaValueKind.Unknown, getters, out var value, out var learned));
        Assert.Equal("7", value);
        Assert.Equal(UdaValueKind.Integer, learned);
        Assert.Equal(1, getters.StoredKindReads);

        // Non-empty text needs no stored-type read at all.
        var text = new Tekla2021Getters("mcp:create");
        Assert.True(UdaValues.TryRead(UdaValueKind.Unknown, text, out _, out learned));
        Assert.Equal(UdaValueKind.String, learned);
        Assert.Equal(0, text.StoredKindReads);
    }

    [Fact]
    public void Declared_reads_never_pay_for_the_stored_type()
    {
        foreach (var (kind, stored) in new (UdaValueKind, object?)[]
                 {
                     (UdaValueKind.Integer, 0), (UdaValueKind.Double, 0.0), (UdaValueKind.String, ""),
                     (UdaValueKind.String, null),
                 })
        {
            var getters = new Tekla2021Getters(stored);
            UdaValues.TryRead(kind, getters, out _, out _);
            Assert.Equal(0, getters.StoredKindReads);
        }
    }

    [Theory]
    [InlineData("TYPE_INT", UdaValueKind.Integer)]
    [InlineData("TYPE_DOUBLE", UdaValueKind.Double)]
    [InlineData("TYPE_STRING", UdaValueKind.String)]
    [InlineData("", UdaValueKind.Unknown)]
    public void Catalog_types_map(string type, UdaValueKind expected) =>
        Assert.Equal(expected, UdaValues.FromCatalogType(type));

    [Fact]
    public void Writes_follow_the_declared_type()
    {
        Assert.True(UdaValues.TryConvertForWrite(UdaValueKind.String, "USER_FIELD_1", "123", out var s, out _));
        Assert.Equal("123", s); // stays a string — it used to become an integer
        Assert.True(UdaValues.TryConvertForWrite(UdaValueKind.Integer, "cm_kat", " 5 ", out var i, out _));
        Assert.Equal(5, i);
        Assert.True(UdaValues.TryConvertForWrite(UdaValueKind.Double, "SPAN", "6000.5", out var d, out _));
        Assert.Equal(6000.5, d);
        Assert.True(UdaValues.TryConvertForWrite(UdaValueKind.Unknown, "X", "12", out var guess, out _));
        Assert.Equal(12, guess);
    }

    [Theory]
    [InlineData(UdaValueKind.Integer, "abc")]
    [InlineData(UdaValueKind.Integer, "2.5")]
    [InlineData(UdaValueKind.Integer, "")]
    [InlineData(UdaValueKind.Double, "n/a")]
    public void Mismatched_writes_are_refused(UdaValueKind kind, string raw)
    {
        Assert.False(UdaValues.TryConvertForWrite(kind, "FIELD", raw, out _, out var error));
        Assert.Contains("FIELD", error);
    }

    [Fact]
    public void Kind_cache_resets_per_model_and_keeps_declared_types()
    {
        var cache = new UdaKindCache();
        cache.ResetIfScopeChanged("A");
        cache.SetDeclared("cm_kat", UdaValueKind.Integer);
        cache.SetDeclared("PLUGIN_X", UdaValueKind.Unknown);
        cache.Learn("cm_kat", UdaValueKind.String);   // a read never overrides the catalog
        cache.Learn("PLUGIN_X", UdaValueKind.Double);

        Assert.True(cache.TryGet("cm_kat", out var declared));
        Assert.Equal(UdaValueKind.Integer, declared);
        Assert.True(cache.TryGet("PLUGIN_X", out var learned));
        Assert.Equal(UdaValueKind.Double, learned);
        Assert.False(cache.TryGet("CM_KAT", out _)); // case-sensitive, like Tekla

        cache.ResetIfScopeChanged("A");
        Assert.True(cache.TryGet("cm_kat", out _));
        cache.ResetIfScopeChanged("B");
        Assert.False(cache.TryGet("cm_kat", out _));
    }

    // ---------------------------------------------------------------- import ----

    private sealed class TypedTarget : IUdaImportTarget
    {
        public readonly Dictionary<string, string> Written = new Dictionary<string, string>();

        public object? Resolve(string guid) => guid;
        public UdaImportIdentity Describe(object handle) => new UdaImportIdentity { Guid = (string)handle, Type = "Beam" };
        public string? ReadUda(object handle, string name) => name == "cm_kat" ? "0" : null;

        public string? CheckValue(string name, string value) =>
            UdaValues.TryConvertForWrite(
                name == "cm_kat" ? UdaValueKind.Integer : UdaValueKind.String, name, value, out _, out var error)
                ? null
                : error;

        public int WriteUdas(object handle, IReadOnlyList<KeyValuePair<string, string>> values)
        {
            foreach (var pair in values) Written[pair.Key] = pair.Value;
            return values.Count;
        }

        public void Commit() { }
    }

    [Fact]
    public void Import_leaves_out_values_of_the_wrong_type_and_says_so()
    {
        var root = Path.Combine(Path.GetTempPath(), "tekla-mcp-uda-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "udas.csv");
            File.WriteAllText(file,
                "guid,cm_kat,USER_FIELD_1\n" +
                "11111111-1111-1111-1111-111111111111,abc,X\n" +
                "22222222-2222-2222-2222-222222222222,oops,\n");
            var target = new TypedTarget();

            var result = UdaImportRunner.Run(
                new FilePathPolicy(new[] { root }),
                new UdaFileWriteRequest { Path = file, Apply = true, OverwriteNonEmpty = true },
                "Test",
                target);

            Assert.Equal(2, result.InvalidValues);
            Assert.Contains("is not an integer", result.Message);
            Assert.False(target.Written.ContainsKey("cm_kat"));
            Assert.Equal("X", target.Written["USER_FIELD_1"]);
            Assert.Contains(result.Sample, s => s.Action == "invalid");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Integer_zero_counts_as_non_empty_for_the_overwrite_guard()
    {
        var root = Path.Combine(Path.GetTempPath(), "tekla-mcp-uda-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "udas.csv");
            File.WriteAllText(file, "guid,cm_kat\n11111111-1111-1111-1111-111111111111,5\n");
            var target = new TypedTarget();

            var result = UdaImportRunner.Run(
                new FilePathPolicy(new[] { root }),
                new UdaFileWriteRequest { Path = file, Apply = true },
                "Test",
                target);

            Assert.Equal(1, result.SkippedNonEmpty);
            Assert.Empty(target.Written);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
