namespace TeklaMcp.Core.Models;

/// <summary>
/// Tekla part positioning relative to its input line/plane. Enum values are represented as
/// strings so the Core project stays Tekla-free and the DTO remains stable across Tekla versions.
/// Blank enum values mean "leave/default"; offsets are millimetres/degrees as documented.
/// </summary>
public sealed class PartPosition
{
    /// <summary>MIDDLE | LEFT | RIGHT.</summary>
    public string? Plane { get; set; }

    /// <summary>Offset from <see cref="Plane"/> in millimetres.</summary>
    public double? PlaneOffset { get; set; }

    /// <summary>FRONT | TOP | BACK | BELOW.</summary>
    public string? Rotation { get; set; }

    /// <summary>Additional rotation in degrees.</summary>
    public double? RotationOffset { get; set; }

    /// <summary>MIDDLE | FRONT | BEHIND.</summary>
    public string? Depth { get; set; }

    /// <summary>Offset from <see cref="Depth"/> in millimetres.</summary>
    public double? DepthOffset { get; set; }
}
