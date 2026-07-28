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
    [Description("Sum weight (kg) for objects matching optional filters, streamed without materializing objects " +
                 "(safe on 400k+ models). Scans physical PARTS by default; partsOnly=false widens to every object " +
                 "(bolts, welds, assemblies). Set useSelection=true to sum only the current UI selection. " +
                 "If the result says truncated=true, repeat with cursor=nextCursor and ADD the page totals together.")]
    public static FilteredMetrics SumWeight(
        ITeklaModelService model,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'USER_FIELD_1'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Scan physical parts only (default). Set false to include bolts/welds/assemblies/etc.")] bool partsOnly = true,
        [Description("Cap the number of objects scanned in this call; combine with cursor to page huge models " +
                     "within the MCP client's request timeout (~40000 is a safe page on live models).")] int? maxObjects = null,
        [Description("Continuation cursor from the previous page's nextCursor. Keep every other argument identical.")] string? cursor = null)
    {
        var aggregation = model.AggregateBy(
            BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains, useSelection: useSelection),
            groupBy: null, limit: 1, cursor: cursor, maxObjects: maxObjects, partsOnly: partsOnly);
        return new FilteredMetrics
        {
            ObjectCount = aggregation.MatchedObjects,
            ObjectsWithWeight = aggregation.ObjectsWithWeight,
            TotalWeightKg = aggregation.TotalWeightKg,
            ScannedObjects = aggregation.ScannedObjects,
            Truncated = aggregation.Truncated,
            NextCursor = aggregation.NextCursor,
            Message = aggregation.Message,
            Backend = aggregation.Backend,
        };
    }

    [McpServerTool(Name = "tekla_group_weight_by")]
    [Description("Group objects by one field and return count + total weight (kg) per group, streamed without " +
                 "materializing objects (safe on 400k+ models). groupBy: 'type', 'class', 'profile', 'material', " +
                 "'name', 'assembly' (assembly mark / ASSEMBLY_POS), 'uda:NAME' to group by a user-defined " +
                 "attribute (e.g. 'uda:USER_FIELD_1' for approval-status workflows), or 'attr:NAME' for any " +
                 "report/UDA/built-in name. Objects with an empty/missing group value land in the '(none)' row — " +
                 "report it, it is usually the biggest group. Scans physical PARTS by default (partsOnly=false " +
                 "widens to all objects). If the result says truncated=true, repeat with cursor=nextCursor and the " +
                 "SAME arguments, then merge rows by key across pages (pages cover disjoint slices).")]
    public static AggregationResult GroupWeightBy(
        ITeklaModelService model,
        [Description("Group key: 'type', 'class', 'profile', 'material', 'name', 'assembly', 'uda:NAME' or 'attr:NAME'.")] string groupBy,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'USER_FIELD_1'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Maximum number of groups to return; the rest is rolled into an '(other)' row. Default 50.")] int limit = 50,
        [Description("Scan physical parts only (default). Set false to include bolts/welds/assemblies/etc.")] bool partsOnly = true,
        [Description("Cap the number of objects scanned in this call; combine with cursor to page huge models " +
                     "within the MCP client's request timeout (~40000 is a safe page on live models).")] int? maxObjects = null,
        [Description("Continuation cursor from the previous page's nextCursor. Keep every other argument identical.")] string? cursor = null)
        => model.AggregateBy(
            BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains, useSelection: useSelection),
            groupBy, limit, cursor, maxObjects, partsOnly);

    [McpServerTool(Name = "tekla_list_distinct_values")]
    [Description("List distinct values of a field with count + total weight per value. Same engine and paging as " +
                 "tekla_group_weight_by. field: 'type', 'class', 'profile', 'material', 'name', 'assembly', " +
                 "'uda:NAME' (e.g. 'uda:USER_FIELD_1') or 'attr:NAME'.")]
    public static AggregationResult ListDistinctValues(
        ITeklaModelService model,
        [Description("Field: 'type', 'class', 'profile', 'material', 'name', 'assembly', 'uda:NAME' or 'attr:NAME'.")] string field,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'USER_FIELD_1'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Maximum number of values to return; the rest is rolled into an '(other)' row. Default 100.")] int limit = 100,
        [Description("Scan physical parts only (default). Set false to include bolts/welds/assemblies/etc.")] bool partsOnly = true,
        [Description("Cap the number of objects scanned in this call; combine with cursor to page huge models.")] int? maxObjects = null,
        [Description("Continuation cursor from the previous page's nextCursor. Keep every other argument identical.")] string? cursor = null)
        => GroupWeightBy(model, field, type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains, useSelection, limit, partsOnly, maxObjects, cursor);

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
