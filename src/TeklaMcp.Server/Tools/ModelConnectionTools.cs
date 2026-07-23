using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Tools for connection heuristics and node-type analysis.
/// </summary>
[McpServerToolType]
public static class ModelConnectionTools
{
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
                 "Use this to read Cyrillic custom-component names from an existing detail.")]
    public static IReadOnlyList<ComponentInfo> ListConnections(
        ITeklaModelService model,
        [Description("Part GUID whose attached components should be listed.")] string partGuid)
        => model.GetConnections(partGuid);

    [McpServerTool(Name = "tekla_create_connection")]
    [Description("Create a Tekla Connection after resolving primary/secondary parts. Geometry is " +
                 "committed once before component insertion to avoid the fresh-part race. Negative " +
                 "number means a custom connection. Preview unless apply=true.")]
    public static WriteResult CreateConnection(
        ITeklaModelService model,
        [Description("Exact system/custom connection name (Unicode/Cyrillic supported).")] string name,
        [Description("Primary part GUID.")] string primaryGuid,
        [Description("Secondary part GUIDs, comma/semicolon/newline separated.")] string secondaryGuids,
        [Description("Up vector 'x,y,z'. Default global +Z.")] string upVector = "0,0,1",
        [Description("Saved attributes file name/path understood by Tekla. Empty = none.")] string attributesFile = "",
        [Description("System component number; negative = custom component. Default -1.")] int number = -1,
        [Description("NA, BASIC, DIAGONAL, SPLICE, GLOBAL_Z, etc. Default NA uses upVector.")] string autoDirection = "NA",
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
        => model.CreateConnections(new[]
        {
            new ConnectionSpec
            {
                Name = name ?? "",
                Number = number,
                PrimaryGuid = primaryGuid ?? "",
                SecondaryGuids = ToolHelpers.ParseList(secondaryGuids),
                UpVector = ToolHelpers.ParsePoint(upVector) ?? new Point3D(0, 0, 1),
                AttributesFile = attributesFile ?? "",
                AutoDirection = autoDirection ?? "NA",
            },
        }, apply);

    [McpServerTool(Name = "tekla_copy_connection")]
    [Description("Copy a connection's identity/orientation from an existing part to new primary/" +
                 "secondary parts. Reads the exact component Name (including Cyrillic), Number, " +
                 "UpVector and AutoDirection. Tekla does not enumerate arbitrary custom attributes; " +
                 "provide attributesFile when those values must be reproduced. Preview unless apply=true.")]
    public static WriteResult CopyConnection(
        ITeklaModelService model,
        [Description("Part GUID used to discover the source connection.")] string sourcePartGuid,
        [Description("Source connection integer ID. Use tekla_list_connections; 0 only when exactly one is attached.")]
        int sourceConnectionId,
        [Description("New primary part GUID.")] string targetPrimaryGuid,
        [Description("New secondary part GUIDs, comma/semicolon/newline separated.")] string targetSecondaryGuids,
        [Description("Optional attributes file to load for custom parameters.")] string attributesFile = "",
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

        return model.CreateConnections(new[]
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
            },
        }, apply);
    }
}
