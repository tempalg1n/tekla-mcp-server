namespace TeklaMcp.Core.Models;

/// <summary>
/// One grid line: its label and its coordinate along an axis. Used to translate human axis
/// references (e.g. "between axes 1 and 2 along axis Д") into model coordinates.
/// </summary>
public sealed class GridLineInfo
{
    /// <summary>Axis family: "X" or "Y" (or "Z" for elevations).</summary>
    public string Axis { get; set; } = "";

    /// <summary>Grid label as shown in Tekla, e.g. "1", "2", "А", "Д", "+3.300".</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// Coordinate of this grid line along its axis, millimetres (global). For a grid rotated
    /// against the global axes (<see cref="Rotated"/>) it is the grid-local position — use
    /// <see cref="Start"/>/<see cref="End"/> instead.
    /// </summary>
    public double Coordinate { get; set; }

    /// <summary>Tekla ID of the grid this line belongs to (models can hold several grids).</summary>
    public int? GridId { get; set; }

    /// <summary>True when the grid's axes are not the global X/Y.</summary>
    public bool Rotated { get; set; }

    /// <summary>X/Y lines: global end points spanning the grid (null for levels and on the mock).</summary>
    public Point3D? Start { get; set; }
    public Point3D? End { get; set; }
}
