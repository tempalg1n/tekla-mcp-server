using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Meta tools about the server itself — currently the gap-reporting affordance that lets an agent
/// formally report a missing capability instead of scripting around it.
/// </summary>
[McpServerToolType]
public static class ModelMetaTools
{
    [McpServerTool(Name = "tekla_report_gap")]
    [Description("Report that this MCP tool set is missing a capability, or that an existing tool " +
                 "returns insufficient data. Call it whenever you hit a gap — INCLUDING when you " +
                 "bridged the gap with tekla_run_csharp (recurring scripts deserve a first-class tool). " +
                 "Returns a ready-to-file GitHub issue draft (title + body) and logs the request " +
                 "locally. After calling it, show the draft to the user and offer to file the issue.")]
    public static CapabilityRequest ReportGap(
        ITeklaModelService model,
        [Description("What you were trying to achieve (the user's goal).")] string goal,
        [Description("What is missing or insufficient — the tool or data field you needed.")] string missing,
        [Description("Existing tools you already tried, if any (comma-separated).")] string? attemptedTools = null,
        [Description("A suggested new tool name or parameter, if you have one.")] string? suggestedToolName = null)
    {
        var modelContext = SafeModelContext(model);

        var issuesUrl = Environment.GetEnvironmentVariable("TEKLA_MCP_ISSUES_URL");
        if (string.IsNullOrWhiteSpace(issuesUrl))
            issuesUrl = "https://github.com/tempalg1n/tekla-mcp-server/issues/new";

        var title = "Capability gap: " + Clip(Blank(missing), 100);
        var body = new StringBuilder()
            .AppendLine("**Goal:** " + Blank(goal))
            .AppendLine()
            .AppendLine("**Missing capability / insufficient data:** " + Blank(missing))
            .AppendLine()
            .AppendLine("**Existing tools tried:** " + Blank(attemptedTools))
            .AppendLine()
            .AppendLine("**Suggested tool/parameter:** " + Blank(suggestedToolName))
            .AppendLine()
            .AppendLine("**Model context:** " + Blank(modelContext))
            .AppendLine()
            .AppendLine("_Filed via `tekla_report_gap` — an agent hit a gap in the MCP tool set and " +
                        "reported it instead of scripting around it._")
            .ToString();

        return new CapabilityRequest
        {
            Goal = Blank(goal),
            Missing = Blank(missing),
            AttemptedTools = attemptedTools,
            SuggestedToolName = suggestedToolName,
            ModelContext = modelContext,
            IssueTitle = title,
            IssueBody = body,
            IssuesUrl = issuesUrl!,
            LoggedTo = TryLog(title, body),
            NextStep = "Show this draft to the user and file it as a GitHub issue at IssuesUrl " +
                       "(or ask the user to). Do NOT work around the gap with custom scripts.",
        };
    }

    private static string SafeModelContext(ITeklaModelService model)
    {
        try
        {
            var info = model.GetConnectionInfo();
            return $"{info.Backend}; model='{info.ModelName}'; connected={info.Connected}";
        }
        catch
        {
            return "";
        }
    }

    private static string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s!.Trim();

    private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    private static string? TryLog(string title, string body)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TeklaMcp");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "capability-requests.log");
            File.AppendAllText(path, $"=== {DateTime.UtcNow:o} ===\n{title}\n{body}\n");
            return path;
        }
        catch
        {
            return null;
        }
    }
}
