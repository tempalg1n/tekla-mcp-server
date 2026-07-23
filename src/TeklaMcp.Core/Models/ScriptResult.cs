using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Outcome of an agent-authored C# script run (tekla_run_csharp). The pipeline is
/// policy check → compile → execute; <see cref="Stage"/> says how far it got. The mock
/// backend never executes and compiles only when Tekla reference DLLs are configured.
/// </summary>
public sealed class ScriptResult
{
    /// <summary>True when every stage the backend attempted succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>How far the pipeline got: "policy", "compile" or "execute".</summary>
    public string Stage { get; set; } = "";

    /// <summary>True when Roslyn compilation was actually attempted with Tekla references.</summary>
    public bool CompilationAttempted { get; set; }

    /// <summary>True only when compilation was attempted and completed without errors.</summary>
    public bool Compiled { get; set; }

    /// <summary>
    /// True when the script entered the live execution worker. It does not imply successful
    /// completion; inspect <see cref="Success"/> and <see cref="Error"/>.
    /// </summary>
    public bool Executed { get; set; }

    /// <summary>
    /// Explicit alias for execution-attempt semantics. False for policy/compile failures,
    /// compile-only checks and every Mock-backend result.
    /// </summary>
    public bool ExecutionAttempted { get; set; }

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>
    /// SHA-256 of the exact UTF-8 script text, for approval/audit without echoing the code.
    /// </summary>
    public string CodeSha256 { get; set; } = "";

    /// <summary>
    /// Mutating API member names detected syntactically, even when execution is allowed or
    /// the request is compile-only. This is conservative and may include false positives.
    /// </summary>
    public List<string> DetectedMutatingMembers { get; set; } = new List<string>();

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

    /// <summary>
    /// Non-fatal safety warnings, especially when a failed/timed-out mutation may have applied
    /// only part of its changes because script execution is not transactional.
    /// </summary>
    public List<string> Warnings { get; set; } = new List<string>();

    /// <summary>Wall-clock duration of the whole pipeline.</summary>
    public long DurationMs { get; set; }

    /// <summary>What the agent should do next (hint, not an error).</summary>
    public string? Guidance { get; set; }
}
