using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
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
                 "model name, path, the active backend (Mock or Tekla) and a status message, plus which " +
                 "server answered: server version/runtime/PID/start time, the Tekla version it was built " +
                 "for, where the Tekla assemblies load from, and the running Tekla processes. A server " +
                 "that started BEFORE the Tekla process it should talk to, or a build for another Tekla " +
                 "year, explains most connection failures. A non-empty assemblyLoadProblems is the cause " +
                 "whatever the message says: the Tekla installation's files could not be loaded, and " +
                 "restarting Tekla does not help. Call this first to verify connectivity.")]
    public static ConnectionInfo GetConnectionInfo(ITeklaModelService model)
    {
        var info = model.GetConnectionInfo();
        AddServerIdentity(info);
        return info;
    }

    private static readonly Lazy<(int Pid, string StartedAt)> ServerProcess = new(() =>
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return (p.Id, p.StartTime.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        }
        catch
        {
            return (0, "");
        }
    });

    private static void AddServerIdentity(ConnectionInfo info)
    {
        try
        {
            var asm = typeof(ModelInfoTools).Assembly;
            info.ServerVersion =
                asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString() ?? "";
            info.ServerRuntime = RuntimeInformation.FrameworkDescription;
            (info.ServerProcessId, info.ServerStartedAt) = ServerProcess.Value;
        }
        catch
        {
            // Identity is diagnostic garnish; never fail the connectivity probe over it.
        }
    }

    [McpServerTool(Name = "tekla_get_model_summary")]
    [Description("Return aggregated statistics for the whole model: total object count, " +
                 "total weight in kg, and counts grouped by type, class, profile and material. " +
                 "Use this for a high-level overview before drilling into individual objects. " +
                 "On very large models (100k+ objects) set includeWeights=false and/or maxObjects " +
                 "to keep the call fast; a capped result is marked truncated.")]
    public static ModelSummary GetModelSummary(
        ITeklaModelService model,
        [Description("Include per-object weight totals (one extra property read per part — the slow part on huge models). Default true.")]
        bool includeWeights = true,
        [Description("Stop after scanning this many objects; 0 = scan everything. A capped result has truncated=true.")]
        int maxObjects = 0)
        => model.GetModelSummary(includeWeights, maxObjects > 0 ? maxObjects : (int?)null);
}
