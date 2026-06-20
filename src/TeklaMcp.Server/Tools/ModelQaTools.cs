using System;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Model-quality (QA) tools. These run a battery of heuristic checks over the objects in scope
/// and report likely modeling problems for a human/agent to review. The checks are intentionally
/// conservative; tune the predicates to your team's conventions.
/// </summary>
[McpServerToolType]
public static class ModelQaTools
{
    [McpServerTool(Name = "tekla_find_modeling_issues")]
    [Description("Run model-quality checks over the objects in scope and report likely problems, " +
                 "grouped by issue with sample GUIDs. Checks: missing material, missing profile, " +
                 "missing class, zero/absent weight, and not-numbered (empty assembly position). " +
                 "Bolts and welds are excluded from part-property checks. Optional filters and " +
                 "useSelection narrow the scope.")]
    public static QaReport FindModelingIssues(
        ITeklaModelService model,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Safety cap on objects scanned. Default 20000.")] int limit = 20000)
    {
        var objects = model.FindObjects(
            new ObjectQuery
            {
                Type = type,
                Class = @class,
                Profile = profile,
                Material = material,
                NameContains = nameContains,
                UseSelection = useSelection,
            },
            limit);

        var groups = new Dictionary<string, QaIssueGroup>(StringComparer.Ordinal);
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Flag(string code, string description, ModelObjectInfo o)
        {
            if (!groups.TryGetValue(code, out var group))
            {
                group = new QaIssueGroup { Issue = code, Description = description };
                groups[code] = group;
            }
            group.Count++;
            if (group.SampleGuids.Count < 10) group.SampleGuids.Add(o.Guid);
            flagged.Add(o.Guid);
        }

        foreach (var o in objects)
        {
            if (IsBoltOrWeld(o)) continue; // bolts/welds don't carry these part properties
            if (!IsStructuralPart(o)) continue;

            if (string.IsNullOrWhiteSpace(o.Material))
                Flag("missing_material", "Part has no material grade assigned.", o);
            if (string.IsNullOrWhiteSpace(o.Profile))
                Flag("missing_profile", "Part has no profile assigned.", o);
            if (string.IsNullOrWhiteSpace(o.Class))
                Flag("missing_class", "Part has no class assigned.", o);
            if (!o.WeightKg.HasValue || o.WeightKg.Value <= 0)
                Flag("zero_weight", "Part has zero or unknown weight.", o);
            if (string.IsNullOrWhiteSpace(o.AssemblyPos))
                Flag("not_numbered", "Part has no assembly position (model not numbered?).", o);
        }

        var report = new QaReport
        {
            ScannedObjects = objects.Count,
            FlaggedObjects = flagged.Count,
            Backend = SafeBackend(model),
        };
        foreach (var g in groups.Values)
            report.Issues.Add(g);
        report.Issues.Sort((a, b) => b.Count.CompareTo(a.Count));
        return report;
    }

    private static bool IsBoltOrWeld(ModelObjectInfo o)
    {
        var t = o.Type ?? "";
        return t.IndexOf("Bolt", StringComparison.OrdinalIgnoreCase) >= 0 ||
               t.IndexOf("Weld", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Heuristic: an object worth checking for part properties.</summary>
    private static bool IsStructuralPart(ModelObjectInfo o)
    {
        if (!string.IsNullOrWhiteSpace(o.Profile)) return true;
        if (o.WeightKg.HasValue) return true;
        var t = o.Type ?? "";
        return t.IndexOf("Beam", StringComparison.OrdinalIgnoreCase) >= 0 ||
               t.IndexOf("Plate", StringComparison.OrdinalIgnoreCase) >= 0 ||
               t.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0 ||
               t.IndexOf("Part", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string SafeBackend(ITeklaModelService model)
    {
        try { return model.GetConnectionInfo().Backend; }
        catch { return ""; }
    }
}
