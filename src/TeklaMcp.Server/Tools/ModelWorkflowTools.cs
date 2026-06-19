using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Workflow-oriented tools for weighted queries, grouped analytics and UI selection.
/// </summary>
[McpServerToolType]
public static class ModelWorkflowTools
{
    [McpServerTool(Name = "tekla_count_objects")]
    [Description("Count objects matching optional filters.")]
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
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null)
        => model.FindObjects(BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains)).Count;

    [McpServerTool(Name = "tekla_sum_weight")]
    [Description("Sum weight (kg) for objects matching optional filters.")]
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
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null)
    {
        var objects = model.FindObjects(BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains));
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
    [Description("Group objects by one field and return count + total weight per group.")]
    public static IReadOnlyList<GroupedMetricRow> GroupWeightBy(
        ITeklaModelService model,
        [Description("Group key: 'type', 'class', 'profile', 'material' or 'name'.")] string groupBy,
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
        [Description("Maximum number of groups to return. Default 50.")] int limit = 50)
    {
        var objects = model.FindObjects(BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains));
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
    [Description("List distinct values of a field with count + total weight per value.")]
    public static IReadOnlyList<GroupedMetricRow> ListDistinctValues(
        ITeklaModelService model,
        [Description("Field: 'type', 'class', 'profile', 'material' or 'name'.")] string field,
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
        [Description("Maximum number of values to return. Default 100.")] int limit = 100)
        => GroupWeightBy(model, field, type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains, limit);

    [McpServerTool(Name = "tekla_select_objects")]
    [Description("Select objects in Tekla UI by filters and return selected count + preview.")]
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
        [Description("Safety limit for selected objects. Default 2000.")] int limit = 2000)
        => model.SelectObjects(BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, attributeName, attributeEquals, attributeContains), limit);

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
        string? attributeContains = null) =>
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
        };

    private static string GetGroupKey(ModelObjectInfo obj, string groupBy)
    {
        switch ((groupBy ?? "").Trim().ToLowerInvariant())
        {
            case "type": return obj.Type;
            case "class": return obj.Class;
            case "profile": return obj.Profile;
            case "material": return obj.Material;
            case "name": return obj.Name;
            default:
                throw new ArgumentException(
                    "Unsupported group field. Use one of: type, class, profile, material, name.");
        }
    }

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(none)" : value;
}
