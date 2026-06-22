namespace TeklaMcp.Core.Models;

/// <summary>
/// A structured "the tool set is missing something" report, produced by <c>tekla_report_gap</c>.
/// It carries a ready-to-file GitHub issue draft so a gap reaches the developer instead of being
/// worked around with ad-hoc scripts. The server does NOT file the issue itself (it has no
/// GitHub credentials) — the calling agent/user files it.
/// </summary>
public sealed class CapabilityRequest
{
    /// <summary>What the user/agent was trying to achieve.</summary>
    public string Goal { get; set; } = "";

    /// <summary>The missing capability or the data that an existing tool failed to provide.</summary>
    public string Missing { get; set; } = "";

    /// <summary>Existing tools that were tried, if any.</summary>
    public string? AttemptedTools { get; set; }

    /// <summary>A suggested new tool name or parameter, if the agent has one.</summary>
    public string? SuggestedToolName { get; set; }

    /// <summary>Connection/model context at the time of the report (backend, model name).</summary>
    public string ModelContext { get; set; } = "";

    /// <summary>Ready-to-file GitHub issue title.</summary>
    public string IssueTitle { get; set; } = "";

    /// <summary>Ready-to-file GitHub issue body (Markdown).</summary>
    public string IssueBody { get; set; } = "";

    /// <summary>Where to file the issue.</summary>
    public string IssuesUrl { get; set; } = "";

    /// <summary>Local file the request was appended to, or null if logging failed.</summary>
    public string? LoggedTo { get; set; }

    /// <summary>What the agent should do next with this report.</summary>
    public string NextStep { get; set; } = "";
}
