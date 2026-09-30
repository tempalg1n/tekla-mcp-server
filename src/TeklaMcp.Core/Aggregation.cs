using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core;

/// <summary>Parsed group-by key kind for <see cref="ITeklaModelService.AggregateBy"/>.</summary>
public enum GroupKeyMode
{
    /// <summary>Single overall bucket ("(all)").</summary>
    All,
    Type,
    Class,
    Profile,
    Material,
    Name,
    /// <summary>Assembly mark (ASSEMBLY_POS report property).</summary>
    Assembly,
    /// <summary>A named user-defined attribute ("uda:NAME").</summary>
    Uda,
    /// <summary>Any named report/UDA/built-in attribute ("attr:NAME").</summary>
    Attribute,
}

/// <summary>
/// Backend-agnostic pieces of the streaming aggregation contract: group-key parsing, cursor
/// parsing and row shaping. Both backends share these so the tool behaves identically against
/// Mock and live Tekla, and so the logic is unit-testable without Tekla.
/// </summary>
public static class Aggregation
{
    /// <summary>
    /// Parse a groupBy string: 'type', 'class', 'profile', 'material', 'name',
    /// 'assembly'/'assembly_pos'/'mark', 'uda:NAME', 'attr:NAME'/'attribute:NAME', or
    /// null/''/'all'/'none' for a single overall bucket. Returns false with a caller-facing
    /// <paramref name="error"/> for anything else — implementations report it via
    /// <see cref="AggregationResult.Message"/> instead of throwing.
    /// </summary>
    public static bool TryParseGroupKey(
        string? groupBy, out GroupKeyMode mode, out string keyName, out string normalized, out string error)
    {
        mode = GroupKeyMode.All;
        keyName = "";
        error = "";
        var raw = (groupBy ?? "").Trim();
        var lower = raw.ToLowerInvariant();
        normalized = lower;

        if (lower.StartsWith("uda:", StringComparison.Ordinal) ||
            lower.StartsWith("attr:", StringComparison.Ordinal) ||
            lower.StartsWith("attribute:", StringComparison.Ordinal))
        {
            var idx = raw.IndexOf(':');
            keyName = raw.Substring(idx + 1).Trim();
            if (keyName.Length == 0)
            {
                error = $"'{raw}' names no field. Use e.g. 'uda:USER_FIELD_1' or 'attr:ASSEMBLY_POS'.";
                return false;
            }
            mode = lower[0] == 'u' ? GroupKeyMode.Uda : GroupKeyMode.Attribute;
            normalized = (mode == GroupKeyMode.Uda ? "uda:" : "attr:") + keyName;
            return true;
        }

        switch (lower)
        {
            case "":
            case "all":
            case "none": mode = GroupKeyMode.All; normalized = "all"; return true;
            case "type": mode = GroupKeyMode.Type; return true;
            case "class": mode = GroupKeyMode.Class; return true;
            case "profile": mode = GroupKeyMode.Profile; return true;
            case "material": mode = GroupKeyMode.Material; return true;
            case "name": mode = GroupKeyMode.Name; return true;
            case "assembly":
            case "assembly_pos":
            case "mark": mode = GroupKeyMode.Assembly; normalized = "assembly"; return true;
            default:
                error = $"Unsupported groupBy '{raw}'. Use one of: type, class, profile, material, name, assembly, " +
                        "'uda:NAME' (user-defined attribute) or 'attr:NAME' (any report/UDA/built-in name), or 'all'.";
                return false;
        }
    }

    /// <summary>Parse a continuation cursor (today: a plain non-negative offset).</summary>
    public static bool TryParseCursor(string? cursor, out long skip, out string error)
    {
        skip = 0;
        error = "";
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        if (long.TryParse(cursor!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out skip) && skip >= 0)
            return true;
        skip = 0;
        error = $"Invalid cursor '{cursor}'. Pass back the NextCursor value from the previous page unchanged.";
        return false;
    }

    /// <summary>"(none)" for empty group keys, mirroring the other grouped tools.</summary>
    public static string NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? "(none)" : key!;

    /// <summary>
    /// Shape accumulated groups (key → [count, weightSum, withWeight]) into ordered rows,
    /// capping at <paramref name="limit"/> and rolling the remainder into one "(other)" row so
    /// a capped page still accounts for every matched object. Appends a note to
    /// <paramref name="result"/>.Message when groups were rolled up.
    /// </summary>
    public static List<GroupedMetricRow> BuildRows(
        Dictionary<string, double[]> agg, int? limit, AggregationResult result)
    {
        var rows = agg
            .Select(kv => new GroupedMetricRow
            {
                Key = kv.Key,
                Count = (int)kv.Value[0],
                TotalWeightKg = Math.Round(kv.Value[1], 2),
                ObjectsWithWeight = (int)kv.Value[2],
            })
            .OrderByDescending(r => r.TotalWeightKg)
            .ThenByDescending(r => r.Count)
            .ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (limit is int max && max > 0 && rows.Count > max)
        {
            var rest = rows.Skip(max).ToList();
            rows = rows.Take(max).ToList();
            rows.Add(new GroupedMetricRow
            {
                Key = $"(other: {rest.Count} groups)",
                Count = rest.Sum(r => r.Count),
                TotalWeightKg = Math.Round(rest.Sum(r => r.TotalWeightKg), 2),
                ObjectsWithWeight = rest.Sum(r => r.ObjectsWithWeight),
            });
            result.Message = Append(result.Message,
                $"{agg.Count} groups total; showing the top {max}, remainder rolled into '(other)'.");
        }

        return rows;
    }

    /// <summary>Accumulate one object into the shared key → [count, weightSum, withWeight] map.</summary>
    public static void Accumulate(
        Dictionary<string, double[]> agg, string key, double? weightKg)
    {
        if (!agg.TryGetValue(key, out var acc))
        {
            acc = new double[3];
            agg[key] = acc;
        }
        acc[0] += 1;
        if (weightKg is double w)
        {
            acc[1] += w;
            acc[2] += 1;
        }
    }

    /// <summary>Join two message fragments with a space, tolerating either being empty.</summary>
    public static string Append(string? current, string next) =>
        string.IsNullOrWhiteSpace(current) ? next : current + " " + next;
}
