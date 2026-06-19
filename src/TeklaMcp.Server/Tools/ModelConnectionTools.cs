using System.ComponentModel;
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
}
