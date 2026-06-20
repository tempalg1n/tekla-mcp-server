using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Assembly-oriented tools. Assemblies are grouped by their mark (the ASSEMBLY_POS report
/// property). Identical marks mean identical fabricated assemblies, so "distinct marks" is the
/// count of unique assembly TYPES — the number a detailer/fabricator usually cares about.
///
/// NOTE: ASSEMBLY_POS is only populated after the model has been numbered. Un-numbered objects
/// have an empty mark and are excluded from these tools (see tekla_find_modeling_issues to
/// detect them).
/// </summary>
[McpServerToolType]
public static class ModelAssemblyTools
{
    [McpServerTool(Name = "tekla_list_assemblies")]
    [Description("List assemblies grouped by assembly mark (ASSEMBLY_POS): part count and total " +
                 "weight per mark, sorted by weight. Optional filters narrow the scope. " +
                 "Requires the model to be numbered.")]
    public static IReadOnlyList<GroupedMetricRow> ListAssemblies(
        ITeklaModelService model,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Maximum number of assembly marks to return. Default 200.")] int limit = 200)
    {
        var objects = model.FindObjects(BuildQuery(type, @class, profile, material, nameContains, useSelection));
        var rows = objects
            .Where(o => !string.IsNullOrWhiteSpace(o.AssemblyPos))
            .GroupBy(o => o.AssemblyPos!)
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

    [McpServerTool(Name = "tekla_count_assemblies")]
    [Description("Count distinct assembly marks (ASSEMBLY_POS) — i.e. how many unique fabricated " +
                 "assembly types exist. Optional filters narrow the scope.")]
    public static int CountAssemblies(
        ITeklaModelService model,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false)
    {
        var objects = model.FindObjects(BuildQuery(type, @class, profile, material, nameContains, useSelection));
        return objects
            .Where(o => !string.IsNullOrWhiteSpace(o.AssemblyPos))
            .Select(o => o.AssemblyPos!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    [McpServerTool(Name = "tekla_get_assembly_parts")]
    [Description("List all parts that belong to a given assembly mark (ASSEMBLY_POS), e.g. 'B1'. " +
                 "Returns the parts that share that mark with their core properties.")]
    public static IReadOnlyList<ModelObjectInfo> GetAssemblyParts(
        ITeklaModelService model,
        [Description("Assembly mark / ASSEMBLY_POS value, e.g. 'B1'.")] string assemblyMark,
        [Description("Maximum number of parts to return. Default 500.")] int limit = 500)
        => model.FindObjects(
            new ObjectQuery { AttributeName = "ASSEMBLY_POS", AttributeEquals = assemblyMark },
            limit);

    private static ObjectQuery BuildQuery(
        string? type, string? @class, string? profile, string? material, string? nameContains, bool useSelection) =>
        new ObjectQuery
        {
            Type = type,
            Class = @class,
            Profile = profile,
            Material = material,
            NameContains = nameContains,
            UseSelection = useSelection,
        };
}
