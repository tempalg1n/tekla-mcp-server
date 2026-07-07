using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Workflow-oriented tools for weighted queries, grouped analytics, UI selection and export.
/// Every tool accepts the shared filter parameters plus <c>useSelection</c> to scope the work
/// to the current Tekla UI selection instead of the whole model.
/// </summary>
[McpServerToolType]
public static class ModelWorkflowTools
{
    [McpServerTool(Name = "tekla_count_objects")]
    [Description("Count objects matching optional filters. Set useSelection=true to count only the current UI selection.")]
    public static int CountObjects(
        ITeklaModelService model,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'RU_FN1_MRK'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false)
        => model.CountObjects(BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains, useSelection: useSelection));

    [McpServerTool(Name = "tekla_sum_weight")]
    [Description("Sum weight (kg) for objects matching optional filters. Set useSelection=true to sum only the current UI selection.")]
    public static FilteredMetrics SumWeight(
        ITeklaModelService model,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'RU_FN1_MRK'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false)
    {
        var objects = model.FindObjects(BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains, useSelection: useSelection));
        var totalWeight = 0.0;
        var withWeight = 0;
        foreach (var obj in objects)
        {
            if (!obj.WeightKg.HasValue) continue;
            withWeight++;
            totalWeight += obj.WeightKg.Value;
        }

        return new FilteredMetrics
        {
            ObjectCount = objects.Count,
            ObjectsWithWeight = withWeight,
            TotalWeightKg = Math.Round(totalWeight, 2),
        };
    }

    [McpServerTool(Name = "tekla_group_weight_by")]
    [Description("Group objects by one field and return count + total weight per group. " +
                 "groupBy: 'type', 'class', 'profile', 'material', 'name' or 'assembly' (assembly mark / ASSEMBLY_POS).")]
    public static IReadOnlyList<GroupedMetricRow> GroupWeightBy(
        ITeklaModelService model,
        [Description("Group key: 'type', 'class', 'profile', 'material', 'name' or 'assembly'.")] string groupBy,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'RU_FN1_MRK'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Maximum number of groups to return. Default 50.")] int limit = 50)
    {
        var objects = model.FindObjects(BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains, useSelection: useSelection));
        var rows = objects
            .GroupBy(o => Normalize(GetGroupKey(o, groupBy)))
            .Select(g => new GroupedMetricRow
            {
                Key = g.Key,
                Count = g.Count(),
                TotalWeightKg = Math.Round(g.Sum(x => x.WeightKg ?? 0), 2),
            })
            .OrderByDescending(r => r.TotalWeightKg)
            .ThenByDescending(r => r.Count)
            .ToList();

        if (limit > 0 && rows.Count > limit) rows = rows.Take(limit).ToList();
        return rows;
    }

    [McpServerTool(Name = "tekla_list_distinct_values")]
    [Description("List distinct values of a field with count + total weight per value. " +
                 "field: 'type', 'class', 'profile', 'material', 'name' or 'assembly'.")]
    public static IReadOnlyList<GroupedMetricRow> ListDistinctValues(
        ITeklaModelService model,
        [Description("Field: 'type', 'class', 'profile', 'material', 'name' or 'assembly'.")] string field,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'RU_FN1_MRK'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Maximum number of values to return. Default 100.")] int limit = 100)
        => GroupWeightBy(model, field, type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains, useSelection, limit);

    [McpServerTool(Name = "tekla_select_objects")]
    [Description("Select objects in Tekla UI by filters (including UDA/attribute filters or explicit GUID list) and return selected count + preview. " +
                 "With useSelection=true the filter is applied within the current selection (narrowing it).")]
    public static SelectionResult SelectObjects(
        ITeklaModelService model,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'RU_FN1_MRK'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Optional GUID list (comma/semicolon/newline separated). If set, only these GUIDs are considered.")] string? guidIn = null,
        [Description("Narrow within the current selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Safety limit for selected objects. Default 2000.")] int limit = 2000)
        => model.SelectObjects(BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains, ParseList(guidIn), useSelection), limit);

    [McpServerTool(Name = "tekla_export_objects")]
    [Description("Export objects matching filters as a text table for the user. " +
                 "Columns: guid, id, type, name, class, profile, material, lengthMm, weightKg, assemblyPos. " +
                 "format: 'csv' (default) or 'markdown'. Handy for producing a bill-of-materials. Capped by 'limit'.")]
    public static string ExportObjects(
        ITeklaModelService model,
        [Description("Output format: 'csv' (default) or 'markdown'.")] string format = "csv",
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'RU_FN1_MRK'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Maximum number of rows. Default 1000.")] int limit = 1000)
    {
        var objects = model.FindObjects(BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains, useSelection: useSelection), limit);
        return FormatTable(objects, format);
    }

    // ---------------------------------------------------------------- helpers ----

    private static ObjectQuery BuildQuery(
        string? type,
        string? @class,
        string? profile,
        string? material,
        string? nameContains,
        string? udaName = null,
        string? udaEquals = null,
        string? attributeName = null,
        string? attributeEquals = null,
        string? attributeContains = null,
        IReadOnlyList<string>? guidIn = null,
        bool useSelection = false) =>
        new ObjectQuery
        {
            Type = type,
            Class = @class,
            Profile = profile,
            Material = material,
            NameContains = nameContains,
            UdaName = udaName,
            UdaEquals = udaEquals,
            AttributeName = attributeName,
            AttributeEquals = attributeEquals,
            AttributeContains = attributeContains,
            GuidIn = guidIn is null ? new List<string>() : guidIn.ToList(),
            UseSelection = useSelection,
        };

    private static IReadOnlyList<string>? ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw!.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed)) result.Add(trimmed);
        }
        return result;
    }

    private static string GetGroupKey(ModelObjectInfo obj, string groupBy)
    {
        switch ((groupBy ?? "").Trim().ToLowerInvariant())
        {
            case "type": return obj.Type;
            case "class": return obj.Class;
            case "profile": return obj.Profile;
            case "material": return obj.Material;
            case "name": return obj.Name;
            case "assembly":
            case "assembly_pos":
            case "mark": return obj.AssemblyPos ?? "";
            default:
                throw new ArgumentException(
                    "Unsupported group field. Use one of: type, class, profile, material, name, assembly.");
        }
    }

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string FormatTable(IReadOnlyList<ModelObjectInfo> objects, string? format)
    {
        var markdown = string.Equals((format ?? "").Trim(), "markdown", StringComparison.OrdinalIgnoreCase);
        var headers = new[] { "guid", "id", "type", "name", "class", "profile", "material", "lengthMm", "weightKg", "assemblyPos" };
        var sb = new StringBuilder();

        string Cell(ModelObjectInfo o, string h) => h switch
        {
            "guid" => o.Guid,
            "id" => o.Id.ToString(CultureInfo.InvariantCulture),
            "type" => o.Type,
            "name" => o.Name,
            "class" => o.Class,
            "profile" => o.Profile,
            "material" => o.Material,
            "lengthMm" => o.LengthMm?.ToString(CultureInfo.InvariantCulture) ?? "",
            "weightKg" => o.WeightKg?.ToString(CultureInfo.InvariantCulture) ?? "",
            "assemblyPos" => o.AssemblyPos ?? "",
            _ => "",
        };

        if (markdown)
        {
            sb.Append("| ").Append(string.Join(" | ", headers)).AppendLine(" |");
            sb.Append("| ").Append(string.Join(" | ", headers.Select(_ => "---"))).AppendLine(" |");
            foreach (var o in objects)
                sb.Append("| ").Append(string.Join(" | ", headers.Select(h => Cell(o, h).Replace("|", "\\|")))).AppendLine(" |");
        }
        else
        {
            sb.AppendLine(string.Join(",", headers));
            foreach (var o in objects)
                sb.AppendLine(string.Join(",", headers.Select(h => CsvEscape(Cell(o, h)))));
        }

        return sb.ToString();
    }

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
