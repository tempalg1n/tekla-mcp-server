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

    /// <summary>
    /// False = preview only (nothing changed). True = apply was requested — whether it
    /// succeeded is <see cref="Outcome"/>.
    /// </summary>
    public bool Applied { get; set; }

    /// <summary>
    /// What happened to the model: planned / not_written / committed / partial / unknown (see
    /// <see cref="TeklaMcp.Core.WriteOutcome"/>). "unknown" means read back before retrying.
    /// </summary>
    public string Outcome { get; set; } = "";

    /// <summary>The model (and Tekla process) this write or preview was about.</summary>
    public WriteTarget? Target { get; set; }

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

    /// <summary>
    /// Integer Tekla identifiers of created objects. Useful for components whose GUID can be empty.
    /// </summary>
    public List<int> CreatedIds { get; set; } = new List<int>();

    /// <summary>A lightweight preview of the planned/affected objects (capped at 20).</summary>
    public List<ModelObjectInfo> Preview { get; set; } = new List<ModelObjectInfo>();

    /// <summary>
    /// How many objects <see cref="Preview"/> was drawn from, where the backend knows it; null
    /// when the operation does not report it.
    /// </summary>
    public int? PreviewTotal { get; set; }

    /// <summary>True when <see cref="Preview"/> lists fewer objects than <see cref="PreviewTotal"/>.</summary>
    public bool PreviewTruncated { get; set; }

    /// <summary>
    /// Field-by-field check of the written objects against the request, after the commit
    /// (tekla_modify_parts). Covers every written object, not just the preview.
    /// </summary>
    public WriteVerification? Verification { get; set; }

    /// <summary>Connection/component preview for component write operations (capped).</summary>
    public List<ComponentInfo> ComponentPreview { get; set; } = new List<ComponentInfo>();

    /// <summary>Per-item errors, if any.</summary>
    public List<string> Errors { get; set; } = new List<string>();

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>Optional status or error message.</summary>
    public string? Message { get; set; }
}
