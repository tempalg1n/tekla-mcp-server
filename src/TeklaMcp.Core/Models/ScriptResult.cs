using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Outcome of an agent-authored C# script run (tekla_run_csharp). The pipeline is
/// policy check → compile → execute; <see cref="Stage"/> says how far it got. The mock
/// backend validates and compiles but never executes (<see cref="Executed"/> stays false).
/// </summary>
public sealed class ScriptResult
{
    /// <summary>True when every stage the backend attempted succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>How far the pipeline got: "policy", "compile" or "execute".</summary>
    public string Stage { get; set; } = "";

    /// <summary>True only when the script actually ran against a live Tekla model.</summary>
    public bool Executed { get; set; }

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>Safety-policy violations (banned APIs, mutations without allowMutations, ...).</summary>
    public List<string> PolicyViolations { get; set; } = new List<string>();

    /// <summary>C# compiler errors — fix the script and retry.</summary>
    public List<string> CompileErrors { get; set; } = new List<string>();

    /// <summary>The script's return value (its last expression), rendered as JSON. Capped.</summary>
    public string? ReturnValueJson { get; set; }

    /// <summary>Lines the script emitted via Print(...). Capped.</summary>
    public List<string> PrintedOutput { get; set; } = new List<string>();

    /// <summary>Runtime error (exception or timeout), when execution failed.</summary>
    public string? Error { get; set; }

    /// <summary>Wall-clock duration of the whole pipeline.</summary>
    public long DurationMs { get; set; }

    /// <summary>What the agent should do next (hint, not an error).</summary>
    public string? Guidance { get; set; }
}
