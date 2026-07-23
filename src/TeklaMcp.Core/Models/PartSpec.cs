using System.Collections.Generic;
using System.ComponentModel;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Declarative specification for ONE part to create. The same shape covers linear members
/// (beams/columns) and plates, selected by <see cref="Kind"/>. Coordinates are GLOBAL model
/// coordinates in millimetres. Generators build a list of these and submit them in one batch.
/// </summary>
public sealed class PartSpec
{
    /// <summary>"beam" | "column" | "plate". A column is a vertical beam (Start below End).</summary>
    [Description("beam, column, or plate.")]
    public string Kind { get; set; } = "beam";

    /// <summary>Start point (beam/column). Bottom point for a column.</summary>
    [Description("Global start/bottom point for a beam or column.")]
    public Point3D? Start { get; set; }

    /// <summary>End point (beam/column). Top point for a column.</summary>
    [Description("Global end/top point for a beam or column.")]
    public Point3D? End { get; set; }

    /// <summary>Contour points for a plate (3+ points, in order).</summary>
    [Description("Three or more global contour points for a plate.")]
    public List<Point3D> Contour { get; set; } = new List<Point3D>();

    /// <summary>Profile string, e.g. "HEA300", "IPE200", "PL20*400". For plates, "PL{thickness}".</summary>
    [Description("Tekla profile, e.g. HEA300, IPE200, or PL20*400.")]
    public string Profile { get; set; } = "";

    /// <summary>Material grade, e.g. "S355J2". Empty = model default.</summary>
    [Description("Material grade; empty uses the model default.")]
    public string Material { get; set; } = "";

    /// <summary>Tekla class (string). Empty = model default.</summary>
    [Description("Tekla class; empty uses the model default.")]
    public string Class { get; set; } = "";

    /// <summary>Object name, e.g. "COLUMN". Empty = model default.</summary>
    [Description("Object name; empty uses the model default.")]
    public string Name { get; set; } = "";

    /// <summary>Explicit Tekla Plane/Rotation/Depth positioning; null = model defaults.</summary>
    [Description("Explicit Plane/Rotation/Depth positioning; null uses defaults.")]
    public PartPosition? Position { get; set; }

    /// <summary>
    /// Optional source part GUID whose complete Position is copied before explicit
    /// <see cref="Position"/> fields override it.
    /// </summary>
    [Description("Optional source part GUID whose Position is copied first.")]
    public string? MatchPositionGuid { get; set; }
}
