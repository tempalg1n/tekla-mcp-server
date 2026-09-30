using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Tools for connection heuristics and node-type analysis.
///
/// Orientation contract for every tool here: Tekla only PERSISTS an explicit connection
/// UpVector under auto-direction NA. Passing an upVector therefore selects NA unless the
/// caller names a different autoDirection — in which case the tools warn, because Tekla will
/// silently recompute the vector while still reporting success.
/// </summary>
[McpServerToolType]
public static class ModelConnectionTools
{
    private const string UpVectorQuirkWarning =
        "upVector was combined with a non-NA autoDirection: Tekla recomputes the vector in that " +
        "mode and the value you passed will NOT be stored (the write still reports success). " +
        "Omit autoDirection to let the tool use NA.";

    [McpServerTool(Name = "tekla_analyze_profile_connections")]
    [Description("Estimate unique connection types for members of a profile by checking " +
                 "which objects are near beam end points (geometric heuristic).")]
    public static ProfileConnectionSummary AnalyzeProfileConnections(
        ITeklaModelService model,
        [Description("Source profile to analyze, e.g. '20P' or 'IPE400'.")] string profile,
        [Description("Distance tolerance in mm for neighboring objects near a beam end. Default 50.")] double toleranceMm = 50,
        [Description("Maximum number of source objects to analyze. Default 1000.")] int limit = 1000)
        => model.AnalyzeConnectionsForProfile(profile, toleranceMm, limit);

    [McpServerTool(Name = "tekla_list_connections")]
    [Description("List real Tekla connections/components attached to a part GUID, including the " +
                 "component's exact Name/Number, primary/secondary GUIDs, UpVector and status. " +
                 "Use this to read Cyrillic custom-component names from an existing detail. " +
                 "The Name here is the ONLY reliable one — Tekla's UI shows a different form " +
                 "(UI '…ГК (1)' vs API '…ГК 1'), so never filter on a name copied from the UI. " +
                 "For a whole axis or selection use tekla_find_connections instead.")]
    public static IReadOnlyList<ComponentInfo> ListConnections(
        ITeklaModelService model,
        [Description("Part GUID whose attached components should be listed.")] string partGuid)
        => model.GetConnections(partGuid);

    [McpServerTool(Name = "tekla_find_connections")]
    [Description("Find connections across MANY parts at once and aggregate them by component " +
                 "name — 'what is attached along this axis / to my selection, besides the usual " +
                 "node?'. Scope the parts with the usual filters (or useSelection), optionally " +
                 "restrict to a bounding box, then filter the components by name/number. Returns " +
                 "the distinct components plus a byName count breakdown. Cost note: this reads " +
                 "components part by part, so keep partLimit tight (an axis, not the model).")]
    public static ConnectionSearchResult FindConnections(
        ITeklaModelService model,
        [Description("Part type filter, e.g. 'Beam'. Empty = any.")] string? type = null,
        [Description("Part Tekla class filter.")] string? @class = null,
        [Description("Part profile substring filter.")] string? profile = null,
        [Description("Part material substring filter.")] string? material = null,
        [Description("Part name substring filter, e.g. 'Фахверк'.")] string? partNameContains = null,
        [Description("Explicit part GUID list (comma/semicolon/newline).")] string? guidIn = null,
        [Description("Scope to the current Tekla UI selection. Default false.")] bool useSelection = false,
        [Description("Only keep components whose exact API name contains this substring.")] string? connectionNameContains = null,
        [Description("Only keep components with this Tekla number (-1 = custom components).")] int? number = null,
        [Description("Bounding-box minimum 'x,y,z' (mm). Parts whose CENTER falls outside are skipped.")] string? bboxMin = null,
        [Description("Bounding-box maximum 'x,y,z' (mm).")] string? bboxMax = null,
        [Description("How many parts to scan at most. Default 500.")] int partLimit = 500,
        [Description("How many components to return. Default 200; the byName counts cover them all.")] int limit = 200)
    {
        var parts = model.FindObjects(
            ToolHelpers.BuildQuery(
                type, @class, profile, material, partNameContains,
                guidIn: guidIn, useSelection: useSelection),
            partLimit);

        var min = ToolHelpers.ParsePoint(bboxMin);
        var max = ToolHelpers.ParsePoint(bboxMax);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<ComponentInfo>();
        var scanned = 0;

        foreach (var part in parts)
        {
            if (!CenterInBox(part, min, max)) continue;
            scanned++;

            foreach (var component in model.GetConnections(part.Guid))
            {
                var key = string.IsNullOrWhiteSpace(component.Guid)
                    ? "id:" + component.Id
                    : "guid:" + component.Guid;
                if (!seen.Add(key)) continue;

                if (!string.IsNullOrWhiteSpace(connectionNameContains) &&
                    (component.Name ?? "").IndexOf(
                        connectionNameContains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (number.HasValue && component.Number != number.Value) continue;

                found.Add(component);
            }
        }

        var returned = found.Take(Math.Max(0, limit)).ToList();
        return new ConnectionSearchResult
        {
            Backend = SafeBackend(model),
            PartsScanned = scanned,
            ConnectionCount = found.Count,
            Connections = returned,
            Truncated = found.Count > returned.Count || parts.Count >= partLimit,
            ByName = found
                .GroupBy(c => new { c.Name, c.Number, c.Type })
                .Select(g => new ConnectionGroupCount
                {
                    Name = g.Key.Name ?? "",
                    Number = g.Key.Number,
                    Type = g.Key.Type ?? "",
                    Count = g.Count(),
                })
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    [McpServerTool(Name = "tekla_modify_connections")]
    [Description("Re-orient EXISTING connections (one GUID or a whole list) without deleting them: " +
                 "set the up vector, the auto-direction mode, and/or load a saved attributes file. " +
                 "This is the reliable way to mass-change UpVector — a raw Open API write under " +
                 "auto-direction BASIC reports success but is silently discarded, so passing " +
                 "upVector here switches the component to NA unless you name a mode yourself. " +
                 "Feed it GUIDs from tekla_find_connections. Preview unless apply=true.")]
    public static WriteResult ModifyConnections(
        ITeklaModelService model,
        [Description("Connection GUIDs (comma/semicolon/newline). One GUID is fine.")] string guids,
        [Description("New up vector 'x,y,z' in global model coordinates. Empty = keep.")] string? upVector = null,
        [Description("Tekla AutoDirectionType: NA, BASIC, DIAGONAL, SPLICE, GLOBAL_Z. Empty = keep, " +
                     "or NA when upVector is given.")] string? autoDirection = null,
        [Description("Saved attributes file to load into each component. Empty = none.")] string attributesFile = "",
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
    {
        var parsedUpVector = ToolHelpers.ParsePoint(upVector);
        var mods = ToolHelpers.ParseList(guids)
            .Select(guid => new ConnectionModification
            {
                Guid = guid,
                UpVector = parsedUpVector,
                AutoDirection = string.IsNullOrWhiteSpace(autoDirection) ? null : autoDirection,
                AttributesFile = attributesFile ?? "",
            })
            .ToList();

        if (mods.Count == 0)
            return new WriteResult
            {
                Operation = "modify_connections",
                Applied = apply,
                Message = "No connection GUIDs provided.",
            };

        var result = ToolHelpers.FailIfNothingApplied(model.ModifyConnections(mods, apply));
        return WarnOnDiscardedUpVector(result, parsedUpVector, autoDirection);
    }

    [McpServerTool(Name = "tekla_create_connection")]
    [Description("Create a Tekla Connection after resolving primary/secondary parts. Geometry is " +
                 "committed once before component insertion to avoid the fresh-part race. Negative " +
                 "number means a custom connection. Tekla rejects a second connection on a pair " +
                 "that already has one — set replaceExisting=true to swap the node type. " +
                 "Preview unless apply=true.")]
    public static WriteResult CreateConnection(
        ITeklaModelService model,
        [Description("Exact system/custom connection name (Unicode/Cyrillic supported).")] string name,
        [Description("Primary part GUID.")] string primaryGuid,
        [Description("Secondary part GUIDs, comma/semicolon/newline separated.")] string secondaryGuids,
        [Description("Up vector 'x,y,z'. Default global +Z; only stored under autoDirection NA.")] string upVector = "0,0,1",
        [Description("Saved attributes file name/path understood by Tekla. Empty = none.")] string attributesFile = "",
        [Description("System component number; negative = custom component. Default -1.")] int number = -1,
        [Description("NA, BASIC, DIAGONAL, SPLICE, GLOBAL_Z, etc. Default NA uses upVector.")] string autoDirection = "NA",
        [Description("Delete components already on this primary/secondary pair first. Default false.")] bool replaceExisting = false,
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
    {
        var parsedUpVector = ToolHelpers.ParsePoint(upVector) ?? new Point3D(0, 0, 1);
        var result = ToolHelpers.FailIfNothingApplied(model.CreateConnections(new[]
        {
            new ConnectionSpec
            {
                Name = name ?? "",
                Number = number,
                PrimaryGuid = primaryGuid ?? "",
                SecondaryGuids = ToolHelpers.ParseList(secondaryGuids),
                UpVector = parsedUpVector,
                AttributesFile = attributesFile ?? "",
                AutoDirection = autoDirection ?? "NA",
                ReplaceExisting = replaceExisting,
            },
        }, apply));
        return WarnOnDiscardedUpVector(result, parsedUpVector, autoDirection);
    }

    [McpServerTool(Name = "tekla_copy_connection")]
    [Description("Copy a connection's identity/orientation from an existing part to new primary/" +
                 "secondary parts. Reads the exact component Name (including Cyrillic), Number, " +
                 "UpVector and AutoDirection. Tekla does not enumerate arbitrary custom attributes; " +
                 "provide attributesFile when those values must be reproduced. To SWAP the node " +
                 "type on a pair that already carries a connection, set replaceExisting=true — " +
                 "Tekla refuses a plain second insert there. Preview unless apply=true.")]
    public static WriteResult CopyConnection(
        ITeklaModelService model,
        [Description("Part GUID used to discover the source connection.")] string sourcePartGuid,
        [Description("Source connection integer ID. Use tekla_list_connections; 0 only when exactly one is attached.")]
        int sourceConnectionId,
        [Description("New primary part GUID.")] string targetPrimaryGuid,
        [Description("New secondary part GUIDs, comma/semicolon/newline separated.")] string targetSecondaryGuids,
        [Description("Optional attributes file to load for custom parameters.")] string attributesFile = "",
        [Description("Delete components already on the target pair first. Default false.")] bool replaceExisting = false,
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
    {
        var candidates = model.GetConnections(sourcePartGuid)
            .Where(c => c.Type == "Connection")
            .ToList();
        var source = sourceConnectionId == 0
            ? (candidates.Count == 1 ? candidates[0] : null)
            : candidates.FirstOrDefault(c => c.Id == sourceConnectionId);
        if (source == null)
            return new WriteResult
            {
                Operation = "copy_connection",
                Applied = apply,
                Message = candidates.Count == 0
                    ? "No source connections found on part " + sourcePartGuid + "."
                    : "Source connection is ambiguous/not found; call tekla_list_connections and pass its id.",
            };

        return ToolHelpers.FailIfNothingApplied(model.CreateConnections(new[]
        {
            new ConnectionSpec
            {
                Name = source.Name,
                Number = source.Number,
                PrimaryGuid = targetPrimaryGuid,
                SecondaryGuids = ToolHelpers.ParseList(targetSecondaryGuids),
                UpVector = source.UpVector,
                AutoDirection = source.AutoDirection,
                AttributesFile = attributesFile ?? "",
                ReplaceExisting = replaceExisting,
            },
        }, apply));
    }

    /// <summary>
    /// True when no box was given, or the part's center lies inside it. Parts without a center
    /// are excluded once a box is requested — a filter must not silently pass unknowns.
    /// </summary>
    private static bool CenterInBox(ModelObjectInfo part, Point3D? min, Point3D? max)
    {
        if (min == null && max == null) return true;
        if (!part.CenterX.HasValue || !part.CenterY.HasValue || !part.CenterZ.HasValue) return false;

        if (min != null &&
            (part.CenterX.Value < min.X || part.CenterY.Value < min.Y || part.CenterZ.Value < min.Z))
            return false;
        if (max != null &&
            (part.CenterX.Value > max.X || part.CenterY.Value > max.Y || part.CenterZ.Value > max.Z))
            return false;
        return true;
    }

    /// <summary>
    /// Append the auto-direction warning when the caller pinned a non-NA mode AND passed a
    /// vector — the combination Tekla accepts and then ignores.
    /// </summary>
    private static WriteResult WarnOnDiscardedUpVector(
        WriteResult result, Point3D? upVector, string? autoDirection)
    {
        if (upVector == null || string.IsNullOrWhiteSpace(autoDirection)) return result;
        var normalized = autoDirection!.Trim().ToUpperInvariant();
        if (normalized == "NA" || normalized == "AUTODIR_NA") return result;

        result.Message = string.IsNullOrWhiteSpace(result.Message)
            ? UpVectorQuirkWarning
            : result.Message + " " + UpVectorQuirkWarning;
        return result;
    }

    private static string SafeBackend(ITeklaModelService model)
    {
        try { return model.GetConnectionInfo().Backend ?? ""; }
        catch { return ""; }
    }
}
