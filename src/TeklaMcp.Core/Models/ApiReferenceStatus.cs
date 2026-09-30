using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>Availability/setup diagnostics for the offline Tekla API reference.</summary>
public sealed class ApiReferenceStatus
{
    public bool Available { get; set; }
    public string Directory { get; set; } = "";
    public int TypeCount { get; set; }
    public List<string> Modules { get; set; } = new List<string>();
    public List<string> Warnings { get; set; } = new List<string>();
    public string Guidance { get; set; } = "";

    /// <summary>"generated" (built from the installed Tekla, cached per version), "configured"
    /// (TEKLA_MCP_API_REF_DIR) or "repository" (reference/tekla-api next to the server).</summary>
    public string Origin { get; set; } = "";

    /// <summary>True while the reference for the installed Tekla is still being generated.</summary>
    public bool Generating { get; set; }

    /// <summary>What it is (or will be) generated from.</summary>
    public string? Source { get; set; }
}
