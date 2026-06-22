using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Declarative specification for ONE part to create. The same shape covers linear members
/// (beams/columns) and plates, selected by <see cref="Kind"/>. Coordinates are GLOBAL model
/// coordinates in millimetres. Generators build a list of these and submit them in one batch.
/// </summary>
public sealed class PartSpec
{
    /// <summary>"beam" | "column" | "plate". A column is a vertical beam (Start below End).</summary>
    public string Kind { get; set; } = "beam";

    /// <summary>Start point (beam/column). Bottom point for a column.</summary>
    public Point3D? Start { get; set; }

    /// <summary>End point (beam/column). Top point for a column.</summary>
    public Point3D? End { get; set; }

    /// <summary>Contour points for a plate (3+ points, in order).</summary>
    public List<Point3D> Contour { get; set; } = new List<Point3D>();

    /// <summary>Profile string, e.g. "HEA300", "IPE200", "PL20*400". For plates, "PL{thickness}".</summary>
    public string Profile { get; set; } = "";

    /// <summary>Material grade, e.g. "S355J2". Empty = model default.</summary>
    public string Material { get; set; } = "";

    /// <summary>Tekla class (string). Empty = model default.</summary>
    public string Class { get; set; } = "";

    /// <summary>Object name, e.g. "COLUMN". Empty = model default.</summary>
    public string Name { get; set; } = "";
}
