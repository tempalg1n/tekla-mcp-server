using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Result of a UDA write/update operation.
/// </summary>
public sealed class UdaOperationResult
{
    /// <summary>False = preview mode, true = apply was requested (success: <see cref="Outcome"/>).</summary>
    public bool Applied { get; set; }

    /// <summary>planned / not_written / committed / partial / unknown (see <see cref="TeklaMcp.Core.WriteOutcome"/>).</summary>
    public string Outcome { get; set; } = "";

    /// <summary>The model (and Tekla process) this write or preview was about.</summary>
    public WriteTarget? Target { get; set; }

    /// <summary>How many objects matched the target query.</summary>
    public int MatchedObjects { get; set; }

    /// <summary>How many objects were actually updated.</summary>
    public int UpdatedObjects { get; set; }

    /// <summary>Total number of individual UDA fields updated.</summary>
    public int UpdatedFields { get; set; }

    /// <summary>Preview of affected objects (first items only).</summary>
    public List<ModelObjectInfo> Preview { get; set; } = new List<ModelObjectInfo>();

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>Optional status or error message.</summary>
    public string? Message { get; set; }
}
