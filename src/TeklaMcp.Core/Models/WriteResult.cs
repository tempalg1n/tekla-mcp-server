using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Result of a mutating operation (create / modify / delete). Mutations run in PREVIEW mode by
/// default (<see cref="Applied"/> = false): nothing is committed, but the plan and counts are
/// returned so the caller can confirm before committing with apply=true.
/// </summary>
public sealed class WriteResult
{
    /// <summary>Operation label, e.g. "create", "modify", "delete", "generate_frame".</summary>
    public string Operation { get; set; } = "";

    /// <summary>False = preview only (nothing changed). True = committed to the model.</summary>
    public bool Applied { get; set; }

    /// <summary>How many objects the operation would affect / matched.</summary>
    public int PlannedCount { get; set; }

    /// <summary>How many objects were created (only when applied).</summary>
    public int CreatedCount { get; set; }

    /// <summary>How many objects were modified (only when applied).</summary>
    public int ModifiedCount { get; set; }

    /// <summary>How many objects were deleted (only when applied).</summary>
    public int DeletedCount { get; set; }

    /// <summary>GUIDs of created objects (when applied).</summary>
    public List<string> CreatedGuids { get; set; } = new List<string>();

    /// <summary>A lightweight preview of the planned/affected objects (capped).</summary>
    public List<ModelObjectInfo> Preview { get; set; } = new List<ModelObjectInfo>();

    /// <summary>Per-item errors, if any.</summary>
    public List<string> Errors { get; set; } = new List<string>();

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>Optional status or error message.</summary>
    public string? Message { get; set; }
}
