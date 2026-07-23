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
    private const int MaxTotalCharacters = 64_000;
    private const string CapMarker =
        "…(output capped at 500 lines / 64000 characters — aggregate in the script and return a smaller value)";

    private readonly object _gate = new object();
    private readonly List<string> _printedOutput = new List<string>();
    private int _printedCharacters;
    private bool _outputCapped;

    /// <summary>
    /// Return a copy of the collected lines. The backing list is intentionally private so
    /// script code cannot clear it, append unbounded data, or mutate host-owned output.
    /// </summary>
    public List<string> SnapshotOutput()
    {
        lock (_gate)
            return new List<string>(_printedOutput);
    }

    /// <summary>
    /// Script-facing logger: <c>Print(beam.Profile.ProfileString)</c>. Capped so a loop over a
    /// 400k-object model cannot flood the MCP response.
    /// </summary>
    public void Print(object? value)
    {
        string text;
        try
        {
            text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }
        catch (Exception ex)
        {
            text = "<Print ToString threw: " + (ex.InnerException ?? ex).Message + ">";
        }

        if (text.Length > MaxLineLength)
            text = text.Substring(0, MaxLineLength) + "…";

        lock (_gate)
        {
            if (_outputCapped)
                return;

            // Reserve the final line for a clear cap marker. A total-character cap protects
            // the MCP response even when every Print call emits a maximum-length line.
            if (_printedOutput.Count >= MaxLines - 1 ||
                _printedCharacters + text.Length + CapMarker.Length > MaxTotalCharacters)
            {
                AppendCapMarker();
                return;
            }

            _printedOutput.Add(text);
            _printedCharacters += text.Length;
        }
    }

    private void AppendCapMarker()
    {
        _outputCapped = true;
        if (_printedOutput.Count >= MaxLines || _printedCharacters >= MaxTotalCharacters)
            return;

        var marker = CapMarker;
        var remaining = MaxTotalCharacters - _printedCharacters;
        if (marker.Length > remaining)
            marker = marker.Substring(0, remaining);
        _printedOutput.Add(marker);
        _printedCharacters += marker.Length;
    }
}
