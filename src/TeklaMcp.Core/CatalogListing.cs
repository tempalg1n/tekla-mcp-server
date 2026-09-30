using System;
using System.Collections.Generic;
using System.Globalization;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core;

/// <summary>
/// Paging and filtering for <c>tekla_list_catalog</c>, shared by both backends so a mock page and
/// a live page behave the same. Backends hand over a lazy stream of raw catalog items plus a cheap
/// name reader; only items that make it into the page are mapped (the expensive part live: every
/// property read is a remoting call). The cursor is a catalog OFFSET — pages cover disjoint slices,
/// as in every other scan here — and a filtered page says how much it scanned.
/// </summary>
public static class CatalogListing
{
    public const int MaxLimit = 1000;

    public static readonly IReadOnlyList<string> Kinds = new[]
    {
        "profiles", "parametric_profiles", "materials", "components", "uda_definitions",
    };

    /// <summary>Canonical kind, or null with a message listing the valid ones.</summary>
    public static string? NormalizeKind(string? kind, out string? error)
    {
        error = null;
        var k = (kind ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        switch (k)
        {
            case "profiles": case "profile": case "library_profiles": return "profiles";
            case "parametric_profiles": case "parametric": return "parametric_profiles";
            case "materials": case "material": return "materials";
            case "components": case "component": return "components";
            case "uda_definitions": case "udas": case "uda": case "user_properties": return "uda_definitions";
        }
        error = $"Unknown catalog kind '{kind}'. Use one of: {string.Join(", ", Kinds)}.";
        return null;
    }

    public static CatalogListResult Page<T>(
        string kind,
        IEnumerable<T> source,
        Func<T, IEnumerable<string>> names,
        Func<T, CatalogItemInfo> map,
        CatalogQuery query,
        string backend)
    {
        var result = new CatalogListResult { Kind = kind, Backend = backend };
        if (!Aggregation.TryParseCursor(query.Cursor, out var skip, out var cursorError))
        {
            result.Message = cursorError;
            return result;
        }

        var limit = query.Limit <= 0 ? 200 : Math.Min(query.Limit, MaxLimit);
        var needle = (query.NameContains ?? "").Trim();
        long offset = 0;

        foreach (var item in source)
        {
            if (offset++ < skip) continue;

            if (result.Items.Count >= limit)
            {
                // One more entry exists: this page is full and the next one starts here.
                result.Truncated = true;
                result.NextCursor = (offset - 1).ToString(CultureInfo.InvariantCulture);
                break;
            }

            result.Scanned++;
            if (needle.Length > 0 && !AnyContains(names(item), needle)) continue;
            result.Items.Add(map(item));
        }

        if (result.Truncated)
            result.Message = $"Page full ({result.Items.Count} items). Repeat with cursor=nextCursor for more.";
        else if (skip > 0 && result.Scanned == 0)
            result.Message = "Cursor points at or beyond the end of the catalog.";
        else if (result.Items.Count == 0 && needle.Length > 0)
            result.Message = $"No {kind} entry contains '{needle}' (searched {result.Scanned} entries — the whole catalog" +
                             (skip > 0 ? " after the cursor)." : ").");
        return result;
    }

    private static bool AnyContains(IEnumerable<string> values, string needle)
    {
        foreach (var value in values)
            if (!string.IsNullOrEmpty(value) && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }
}
