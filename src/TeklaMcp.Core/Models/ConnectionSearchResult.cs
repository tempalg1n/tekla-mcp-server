using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Connections discovered across a SET of parts, plus a per-name aggregation.
/// Answers "what is attached along this axis / to this selection?" without listing
/// every part one at a time.
/// </summary>
public sealed class ConnectionSearchResult
{
    /// <summary>How many parts were scanned for attached components.</summary>
    public int PartsScanned { get; set; }

    /// <summary>Distinct components found after filtering.</summary>
    public int ConnectionCount { get; set; }

    /// <summary>True when the part scan or the returned list hit its cap.</summary>
    public bool Truncated { get; set; }

    /// <summary>The matched components (capped by the caller's limit).</summary>
    public List<ComponentInfo> Connections { get; set; } = new List<ComponentInfo>();

    /// <summary>Counts grouped by component name+number — the "what else is on this beam?" view.</summary>
    public List<ConnectionGroupCount> ByName { get; set; } = new List<ConnectionGroupCount>();

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>Optional status or error message.</summary>
    public string? Message { get; set; }
}

/// <summary>One row of the connection-name aggregation.</summary>
public sealed class ConnectionGroupCount
{
    /// <summary>Exact component name as Tekla stores it (may include a trailing "(1)").</summary>
    public string Name { get; set; } = "";

    /// <summary>Component number; -1 for custom components.</summary>
    public int Number { get; set; }

    /// <summary>Component runtime type, e.g. "Connection" or "Detail".</summary>
    public string Type { get; set; } = "";

    /// <summary>How many distinct components of this name+number were found.</summary>
    public int Count { get; set; }
}
