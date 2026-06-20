using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// One category of model-quality finding (e.g. "missing material"), with how many objects
/// were affected and a few sample GUIDs to inspect.
/// </summary>
public sealed class QaIssueGroup
{
    /// <summary>Machine-readable issue code, e.g. "missing_material".</summary>
    public string Issue { get; set; } = "";

    /// <summary>Human-readable description of what was checked.</summary>
    public string Description { get; set; } = "";

    /// <summary>Number of objects flagged for this issue.</summary>
    public int Count { get; set; }

    /// <summary>Up to a handful of example GUIDs to help locate the problem.</summary>
    public List<string> SampleGuids { get; set; } = new List<string>();
}

/// <summary>
/// Result of a model-quality (QA) scan: a battery of heuristic checks over the objects in
/// scope, grouped by issue. Checks are intentionally conservative and may need tuning per
/// team conventions.
/// </summary>
public sealed class QaReport
{
    /// <summary>How many objects were inspected.</summary>
    public int ScannedObjects { get; set; }

    /// <summary>How many distinct objects were flagged by at least one check.</summary>
    public int FlaggedObjects { get; set; }

    /// <summary>Findings grouped by issue, ordered by count descending.</summary>
    public List<QaIssueGroup> Issues { get; set; } = new List<QaIssueGroup>();

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";
}
