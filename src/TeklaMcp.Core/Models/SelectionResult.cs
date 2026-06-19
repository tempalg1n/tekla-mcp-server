using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Result of selecting objects in the Tekla UI by filter.
/// </summary>
public sealed class SelectionResult
{
    /// <summary>Total number of objects selected in the UI.</summary>
    public int SelectedCount { get; set; }

    /// <summary>First selected objects (lightweight preview).</summary>
    public List<ModelObjectInfo> Preview { get; set; } = new List<ModelObjectInfo>();

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";
}
