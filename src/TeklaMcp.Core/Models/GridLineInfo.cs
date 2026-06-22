namespace TeklaMcp.Core.Models;

/// <summary>
/// One grid line: its label and its coordinate along an axis. Used to translate human axis
/// references (e.g. "between axes 1 and 2 along axis Д") into model coordinates.
/// </summary>
public sealed class GridLineInfo
{
    /// <summary>Axis family: "X" or "Y" (or "Z" for elevations).</summary>
    public string Axis { get; set; } = "";

    /// <summary>Grid label as shown in Tekla, e.g. "1", "2", "А", "Д".</summary>
    public string Label { get; set; } = "";

    /// <summary>Coordinate of this grid line along its axis, millimetres (global).</summary>
    public double Coordinate { get; set; }
}
