namespace TeklaMcp.Core.Models;

/// <summary>
/// Declarative edit for ONE existing part, addressed by GUID. Null fields are left unchanged.
/// Used for property edits (profile/material/class/name), geometry edits (new endpoints), and
/// the handle swap that flips a member's start/end (e.g. to turn a wrongly-oriented column).
/// </summary>
public sealed class PartModification
{
    /// <summary>GUID of the part to modify.</summary>
    public string Guid { get; set; } = "";

    /// <summary>New profile, or null to keep.</summary>
    public string? Profile { get; set; }

    /// <summary>New material, or null to keep.</summary>
    public string? Material { get; set; }

    /// <summary>New class, or null to keep.</summary>
    public string? Class { get; set; }

    /// <summary>New name, or null to keep.</summary>
    public string? Name { get; set; }

    /// <summary>New start point (linear members), or null to keep.</summary>
    public Point3D? NewStart { get; set; }

    /// <summary>New end point (linear members), or null to keep.</summary>
    public Point3D? NewEnd { get; set; }

    /// <summary>If true, swap the start and end handles of a linear member.</summary>
    public bool SwapHandles { get; set; }

    /// <summary>Position fields to change; null fields inside it are left unchanged.</summary>
    public PartPosition? Position { get; set; }

    /// <summary>
    /// Optional source part GUID whose complete Position is copied before explicit
    /// <see cref="Position"/> fields override it.
    /// </summary>
    public string? MatchPositionGuid { get; set; }
}
