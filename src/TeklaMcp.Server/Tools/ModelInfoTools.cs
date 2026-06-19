using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// High-level "what am I connected to / what's in the model" tools.
///
/// Each tool method receives <see cref="ITeklaModelService"/> as its first parameter;
/// the MCP SDK injects it from DI, so it is NOT exposed to the LLM as a tool argument.
/// Return values are serialized to JSON automatically.
/// </summary>
[McpServerToolType]
public static class ModelInfoTools
{
    [McpServerTool(Name = "tekla_get_connection_info")]
    [Description("Check whether a Tekla Structures model is open and reachable. Returns " +
                 "model name, path, the active backend (Mock or Tekla) and a status message. " +
                 "Call this first to verify connectivity before other tools.")]
    public static ConnectionInfo GetConnectionInfo(ITeklaModelService model)
        => model.GetConnectionInfo();

    [McpServerTool(Name = "tekla_get_model_summary")]
    [Description("Return aggregated statistics for the whole model: total object count, " +
                 "total weight in kg, and counts grouped by type, class, profile and material. " +
                 "Use this for a high-level overview before drilling into individual objects.")]
    public static ModelSummary GetModelSummary(ITeklaModelService model)
        => model.GetModelSummary();
}
