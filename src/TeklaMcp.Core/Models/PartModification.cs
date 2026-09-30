using System.ComponentModel;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Declarative edit for ONE existing part, addressed by GUID. Null fields are left unchanged.
/// Used for property edits (profile/material/class/name), geometry edits (new endpoints), and
/// the handle swap that flips a member's start/end (e.g. to turn a wrongly-oriented column).
/// </summary>
public sealed class PartModification
{
    /// <summary>GUID of the part to modify.</summary>
    [Description("GUID of the part to modify.")]
    public string Guid { get; set; } = "";

    /// <summary>New profile, or null to keep.</summary>
    [Description("New profile, or null to keep.")]
    public string? Profile { get; set; }

    /// <summary>New material, or null to keep.</summary>
    [Description("New material grade, or null to keep.")]
    public string? Material { get; set; }

    /// <summary>New class, or null to keep.</summary>
    [Description("New Tekla class, or null to keep.")]
    public string? Class { get; set; }

    /// <summary>New name, or null to keep.</summary>
    [Description("New object name, or null to keep.")]
    public string? Name { get; set; }

    /// <summary>New start point (linear members), or null to keep.</summary>
    [Description("New global start point for a linear member, or null to keep.")]
    public Point3D? NewStart { get; set; }

    /// <summary>New end point (linear members), or null to keep.</summary>
    [Description("New global end point for a linear member, or null to keep.")]
    public Point3D? NewEnd { get; set; }

    /// <summary>If true, swap the start and end handles of a linear member.</summary>
    [Description("Swap the start and end handles of a linear member.")]
    public bool SwapHandles { get; set; }

    /// <summary>Position fields to change; null fields inside it are left unchanged.</summary>
    [Description("Plane/Rotation/Depth fields to change; null fields are left unchanged.")]
    public PartPosition? Position { get; set; }

    /// <summary>
    /// Optional source part GUID whose complete Position is copied before explicit
    /// <see cref="Position"/> fields override it.
    /// </summary>
    [Description("Optional source part GUID whose complete Position is copied first; explicit " +
                 "Position fields then override it.")]
    public string? MatchPositionGuid { get; set; }
}
