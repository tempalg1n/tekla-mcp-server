using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>Result of probing the connection to a running Tekla Structures model.</summary>
public sealed class ConnectionInfo
{
    /// <summary>True if a model is open and the API answered.</summary>
    public bool Connected { get; set; }

    /// <summary>Model name, e.g. "MyTower". Empty when not connected.</summary>
    public string ModelName { get; set; } = "";

    /// <summary>Absolute path to the model folder. Empty when not connected.</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>Tekla Structures version reported by the running instance, if available.</summary>
    public string? TeklaVersion { get; set; }

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>Human-readable note, e.g. why the connection failed.</summary>
    public string? Message { get; set; }

    // --- Which server answered. Field reports could not tell a stale server process from a
    // fresh one, or the 2021 build from the 2023 build; compare these with the Tekla processes.

    /// <summary>MCP server version (assembly informational version).</summary>
    public string ServerVersion { get; set; } = "";

    /// <summary>Runtime of the server process, e.g. ".NET Framework 4.8.9" or ".NET 8.0.10".</summary>
    public string ServerRuntime { get; set; } = "";

    /// <summary>OS process id of the MCP server.</summary>
    public int ServerProcessId { get; set; }

    /// <summary>Local start time of the MCP server process (ISO 8601 with offset).</summary>
    public string ServerStartedAt { get; set; } = "";

    /// <summary>Tekla Open API version this build was compiled for; only one Tekla year works. Null on Mock.</summary>
    public string? CompiledTeklaVersion { get; set; }

    /// <summary>Folder the Tekla assemblies are resolved from. Null on Mock or when not found.</summary>
    public string? TeklaBinDir { get; set; }

    /// <summary>How <see cref="TeklaBinDir"/> was found: env | process | registry | (not found).</summary>
    public string? TeklaBinDirSource { get; set; }

    /// <summary>Running TeklaStructures processes as "PID n started …"; empty on Mock or when none run.</summary>
    public List<string> TeklaProcesses { get; set; } = new List<string>();
}
