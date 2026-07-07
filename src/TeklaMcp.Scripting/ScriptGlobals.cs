using System;
using System.Collections.Generic;
using System.Globalization;

namespace TeklaMcp.Scripting;

/// <summary>
/// Globals visible to every script: its members can be used as if they were top-level
/// functions/variables. Deliberately tiny — the ONLY host affordance is <see cref="Print"/>,
/// because stdout belongs to the MCP protocol and scripts must never touch <c>Console</c>.
/// The model connection is NOT injected: scripts open it themselves with
/// <c>var model = new Model();</c> (connects to the running Tekla instance).
/// </summary>
public class ScriptGlobals
{
    private const int MaxLines = 500;
    private const int MaxLineLength = 2000;

    /// <summary>Lines collected by <see cref="Print"/>, returned to the agent after the run.</summary>
    public List<string> PrintedOutput { get; } = new List<string>();

    /// <summary>
    /// Script-facing logger: <c>Print(beam.Profile.ProfileString)</c>. Capped so a loop over a
    /// 400k-object model cannot flood the MCP response.
    /// </summary>
    public void Print(object? value)
    {
        if (PrintedOutput.Count > MaxLines)
            return;
        if (PrintedOutput.Count == MaxLines)
        {
            PrintedOutput.Add($"…(output capped at {MaxLines} lines — aggregate in the script and return a value instead)");
            return;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        if (text.Length > MaxLineLength)
            text = text.Substring(0, MaxLineLength) + "…";
        PrintedOutput.Add(text);
    }
}
