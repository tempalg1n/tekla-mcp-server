namespace TeklaMcp.Core.Models;

/// <summary>
/// Lightweight, JSON-serializable snapshot of a single Tekla model object.
/// This is intentionally flat and string-heavy so it maps cleanly to MCP tool output
/// and is easy for an LLM to reason about.
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

    /// <summary>Center X coordinate (mm) in model coordinate system, when available.</summary>
    public double? CenterX { get; set; }

    /// <summary>Center Y coordinate (mm) in model coordinate system, when available.</summary>
    public double? CenterY { get; set; }

    /// <summary>Center Z coordinate (mm) in model coordinate system, when available.</summary>
    public double? CenterZ { get; set; }

    /// <summary>Start X coordinate for linear objects (typically beams), when available.</summary>
    public double? StartX { get; set; }

    /// <summary>Start Y coordinate for linear objects (typically beams), when available.</summary>
    public double? StartY { get; set; }

    /// <summary>Start Z coordinate for linear objects (typically beams), when available.</summary>
    public double? StartZ { get; set; }

    /// <summary>End X coordinate for linear objects (typically beams), when available.</summary>
    public double? EndX { get; set; }

    /// <summary>End Y coordinate for linear objects (typically beams), when available.</summary>
    public double? EndY { get; set; }

    /// <summary>End Z coordinate for linear objects (typically beams), when available.</summary>
    public double? EndZ { get; set; }

    /// <summary>Solid bounding box minimum X coordinate (mm), when available.</summary>
    public double? MinX { get; set; }

    /// <summary>Solid bounding box minimum Y coordinate (mm), when available.</summary>
    public double? MinY { get; set; }

    /// <summary>Solid bounding box minimum Z coordinate (mm), when available.</summary>
    public double? MinZ { get; set; }

    /// <summary>Solid bounding box maximum X coordinate (mm), when available.</summary>
    public double? MaxX { get; set; }

    /// <summary>Solid bounding box maximum Y coordinate (mm), when available.</summary>
    public double? MaxY { get; set; }

    /// <summary>Solid bounding box maximum Z coordinate (mm), when available.</summary>
    public double? MaxZ { get; set; }
}
