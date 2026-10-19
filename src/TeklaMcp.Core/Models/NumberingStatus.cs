using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Whether numbering is current (issue #23): a read-only status query. Reading it never
/// numbers, saves or changes the model; numbering itself stays a separate, approved action.
/// </summary>
public sealed class NumberingStatus
{
    /// <summary>The model the answer is about.</summary>
    public string ModelName { get; set; } = "";

    public string ModelPath { get; set; } = "";

    /// <summary>
    /// <c>Operation.IsNumberingUpToDateAll()</c> — every assembly, part and rebar of the model.
    /// Null when the call failed (see <see cref="Message"/>).
    /// </summary>
    public bool? UpToDateAll { get; set; }

    /// <summary>Per-object status for the GUIDs asked about (at most <see cref="ObjectLimit"/>).</summary>
    public List<NumberingObjectStatus> Objects { get; set; } = new List<NumberingObjectStatus>();

    /// <summary>GUIDs that were asked about but are not in the model.</summary>
    public int NotFound { get; set; }

    /// <summary>GUIDs beyond <see cref="ObjectLimit"/> that were not checked.</summary>
    public int NotChecked { get; set; }

    /// <summary>What the status does and does not say.</summary>
    public List<string> Notes { get; set; } = new List<string>();

    public string Backend { get; set; } = "";

    public string? Message { get; set; }

    public const int ObjectLimit = 200;
}

/// <summary>Numbering status of one part or assembly.</summary>
public sealed class NumberingObjectStatus
{
    public string Guid { get; set; } = "";

    public bool Found { get; set; }

    public string Type { get; set; } = "";

    /// <summary><c>Operation.IsNumberingUpToDate(object)</c>; null when it could not be read.</summary>
    public bool? UpToDate { get; set; }

    /// <summary>The current marks (report properties), for context — not proof of freshness.</summary>
    public string? PartPos { get; set; }

    public string? AssemblyPos { get; set; }

    public string? Error { get; set; }
}
