using System;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Higher-level analysis tools composed from the basic service calls. These show the
/// intended pattern for future analytics: read via <see cref="ITeklaModelService"/>,
/// then shape the result into something directly useful to the user/LLM.
/// </summary>
[McpServerToolType]
public static class ModelAnalysisTools
{
    [McpServerTool(Name = "tekla_analyze_by_material")]
    [Description("Bill-of-material style breakdown by material grade: object count and total " +
                 "weight (kg) per material, sorted by weight descending.")]
    public static IReadOnlyList<MaterialBreakdownRow> AnalyzeByMaterial(ITeklaModelService model)
    {
        var summary = model.GetModelSummary();
        var rows = new List<MaterialBreakdownRow>();

        foreach (var kv in summary.CountByMaterial)
        {
            summary.WeightByMaterialKg.TryGetValue(kv.Key, out var weight);
            rows.Add(new MaterialBreakdownRow
            {
                Material = kv.Key,
                Count = kv.Value,
                TotalWeightKg = Math.Round(weight, 1),
            });
        }

        rows.Sort((a, b) => b.TotalWeightKg.CompareTo(a.TotalWeightKg));
        return rows;
    }
}
