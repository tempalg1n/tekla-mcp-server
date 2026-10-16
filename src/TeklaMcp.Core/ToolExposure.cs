using System;
using System.Collections.Generic;
using System.Linq;

namespace TeklaMcp.Core;

/// <summary>
/// The operator's switches for which tools the server offers, read once at startup from the
/// environment (Program.cs). Read-only mode is a SAFETY switch: it fails closed, and the write paths
/// enforce it at runtime as well. Toolsets only trim tools/list for clients that load every tool
/// description into the context — they are not a safety feature.
/// </summary>
public static class ToolExposure
{
    public const string ReadOnlyVariable = "TEKLA_MCP_READ_ONLY";
    public const string ToolsetsVariable = "TEKLA_MCP_TOOLSETS";

    // One toolset per tool file ([Toolset] on the class in TeklaMcp.Server.Tools).
    public const string Core = "core";
    public const string Model = "model";
    public const string Analytics = "analytics";
    public const string Files = "files";
    public const string Drawings = "drawings";
    public const string Scripting = "scripting";
    public const string Visual = "visual";

    /// <summary>Every toolset, in documentation order. <see cref="Core"/> is always served.</summary>
    public static readonly IReadOnlyList<string> AllToolsets =
        new[] { Core, Model, Analytics, Files, Drawings, Scripting, Visual };

    private static readonly string[] OnValues = { "1", "true", "yes", "on" };
    private static readonly string[] OffValues = { "0", "false", "no", "off" };

    /// <summary>
    /// TEKLA_MCP_READ_ONLY: unset, blank, 0/false/no/off = off; 1/true/yes/on = on (case-insensitive).
    /// Anything else turns read-only ON — a misspelled safety switch must not leave writes enabled —
    /// and <paramref name="warning"/> says so.
    /// </summary>
    public static bool ParseReadOnly(string? value, out string? warning)
    {
        warning = null;
        var v = (value ?? "").Trim();
        if (v.Length == 0 || OffValues.Contains(v, StringComparer.OrdinalIgnoreCase)) return false;
        if (OnValues.Contains(v, StringComparer.OrdinalIgnoreCase)) return true;
        warning = ReadOnlyVariable + "='" + v + "' is not a recognized value, so read-only mode is ON " +
                  "(fail closed). Use 1/true/yes/on or 0/false/no/off.";
        return true;
    }

    /// <summary>
    /// TEKLA_MCP_TOOLSETS: names separated by comma, semicolon or space, case-insensitive. Returns
    /// the toolsets to serve in <see cref="AllToolsets"/> order with <see cref="Core"/> always added,
    /// or null for all of them (unset, blank, or "all" among the names). An unknown name returns
    /// null and sets <paramref name="error"/>: a typo must stop the server, not hide tools silently.
    /// </summary>
    public static IReadOnlyList<string>? ParseToolsets(string? value, out string? error)
    {
        error = null;
        var names = (value ?? "")
            .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim().ToLowerInvariant())
            .Where(name => name.Length > 0)
            .ToList();
        if (names.Count == 0 || names.Contains("all")) return null;

        var unknown = names.Where(name => !AllToolsets.Contains(name)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            error = ToolsetsVariable + " names unknown toolset(s) " + string.Join(", ", unknown.Select(n => "'" + n + "'")) +
                    ". Known toolsets: " + string.Join(", ", AllToolsets) + " (or 'all'). The server did not start.";
            return null;
        }

        var selected = new HashSet<string>(names) { Core };
        return AllToolsets.Where(selected.Contains).ToList();
    }

    /// <summary>The refusal every write path gives in read-only mode.</summary>
    public static string ReadOnlyRefusal(string operation) =>
        "Refused: this Tekla MCP server runs in read-only mode (" + ReadOnlyVariable + "), so " + operation +
        " is not allowed. Nothing was written or changed. Read-only mode is the operator's choice: tell the " +
        "user what you would change; doing it needs the server started without " + ReadOnlyVariable + ".";

    /// <summary>
    /// What the connecting agent is told about a reduced tool surface, put before the regular server
    /// instructions (which name tools that may be missing). Empty when every tool is served.
    /// </summary>
    public static string DescribeMode(bool readOnly, IReadOnlyList<string>? toolsets)
    {
        var text = "";
        if (readOnly)
            text +=
                "READ-ONLY MODE (" + ReadOnlyVariable + ", set by whoever configured this server): only tools " +
                "that change nothing are served. Every tool that writes to the model, drawings or files, or " +
                "changes the Tekla UI is switched off — including tekla_run_csharp, selection, view capture, " +
                "file exports and tekla_report_gap — and the write paths refuse even if one is reached. Where " +
                "the text below mentions such tools, they are not available: describe the change you would " +
                "make (or the missing capability) to the user instead, and do not look for a workaround.\n\n";
        if (toolsets != null)
            text +=
                "REDUCED TOOL SET (" + ToolsetsVariable + "=" + string.Join(",", toolsets) + "): tools from " +
                "the other toolsets (" + string.Join(", ", AllToolsets.Where(t => !toolsets.Contains(t))) +
                ") are not served here, even where the text below names them. If the job needs one, tell the " +
                "user which toolset to enable instead of scripting around it.\n\n";
        return text;
    }
}
