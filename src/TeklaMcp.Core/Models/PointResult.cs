namespace TeklaMcp.Core.Models;

/// <summary>Result of resolving a model point from grid labels + elevation.</summary>
public sealed class PointResult
{
    /// <summary>True if both axis labels were found and the point was resolved.</summary>
    public bool Resolved { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    /// <summary>Echo of the grid labels used, e.g. "1" x "Д".</summary>
    public string? AxisX { get; set; }
    public string? AxisY { get; set; }

    /// <summary>Status / error message (e.g. which label was not found).</summary>
    public string? Message { get; set; }
}
