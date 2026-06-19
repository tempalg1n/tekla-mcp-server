using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Heuristic summary of unique connection types for members of one profile.
/// </summary>
public sealed class ProfileConnectionSummary
{
    /// <summary>Source profile used for analysis.</summary>
    public string SourceProfile { get; set; } = "";

    /// <summary>Total number of source objects considered.</summary>
    public int SourceObjects { get; set; }

    /// <summary>Total number of unique connection signatures.</summary>
    public int UniqueConnectionTypes { get; set; }

    /// <summary>Distance tolerance used to detect neighboring elements near beam ends.</summary>
    public double ToleranceMm { get; set; }

    /// <summary>Sorted list of unique connection types.</summary>
    public List<ProfileConnectionType> ConnectionTypes { get; set; } = new List<ProfileConnectionType>();
}
