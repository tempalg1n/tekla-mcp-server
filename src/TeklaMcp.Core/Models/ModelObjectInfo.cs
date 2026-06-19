namespace TeklaMcp.Core.Models;

/// <summary>
/// Lightweight, JSON-serializable snapshot of a single Tekla model object.
/// This is intentionally flat and string-heavy so it maps cleanly to MCP tool output
/// and is easy for an LLM to reason about. Geometry/relations are deliberately omitted
/// for the read-only prototype.
/// </summary>
public sealed class ModelObjectInfo
{
    /// <summary>Tekla GUID (stable across sessions), e.g. "0a1b2c3d-...".</summary>
    public string Guid { get; set; } = "";

    /// <summary>Tekla integer identifier (NOT stable across sessions).</summary>
    public int Id { get; set; }

    /// <summary>Object type, e.g. "Beam", "ContourPlate", "Bolt".</summary>
    public string Type { get; set; } = "";

    /// <summary>Object name, e.g. "BEAM", "COLUMN".</summary>
    public string Name { get; set; } = "";

    /// <summary>Tekla part "class" (a string in Tekla; used for coloring/grouping).</summary>
    public string Class { get; set; } = "";

    /// <summary>Profile string, e.g. "HEA300", "IPE200", "PL20*400".</summary>
    public string Profile { get; set; } = "";

    /// <summary>Material grade, e.g. "S355J2", "S235JR".</summary>
    public string Material { get; set; } = "";

    /// <summary>Length in millimetres, when applicable.</summary>
    public double? LengthMm { get; set; }

    /// <summary>Weight in kilograms, when applicable.</summary>
    public double? WeightKg { get; set; }

    /// <summary>Surface finish / coating, when set.</summary>
    public string? Finish { get; set; }

    /// <summary>Assembly position number, when set.</summary>
    public string? AssemblyPos { get; set; }
}
