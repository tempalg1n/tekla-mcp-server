using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Geometry and semantic metadata for one selected/reference-model object. Reference objects
/// often have an empty Tekla GUID, so <see cref="Id"/> is the primary address inside a session.
/// </summary>
public sealed class ReferenceGeometryInfo
{
    public int Id { get; set; }
    public string Guid { get; set; } = "";
    public string ExternalGuid { get; set; } = "";
    public string Entity { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string ReferenceModelTitle { get; set; } = "";
    public string ReferenceModelFile { get; set; } = "";
    public double? OverallWidth { get; set; }
    public double? OverallHeight { get; set; }
    public double? MinX { get; set; }
    public double? MinY { get; set; }
    public double? MinZ { get; set; }
    public double? MaxX { get; set; }
    public double? MaxY { get; set; }
    public double? MaxZ { get; set; }

    /// <summary>
    /// Where the AABB came from: "tekla-faces" (exact, from Open API face polygons) or
    /// "ifc-placement-estimate" (rectangle spanned by placement + OverallWidth/Height —
    /// correct position/extent in the wall plane, zero thickness). Empty when no AABB.
    /// </summary>
    public string AabbSource { get; set; } = "";

    /// <summary>World placement origin (GLOBAL model mm), when resolvable.</summary>
    public Point3D? PlacementOrigin { get; set; }
    /// <summary>World placement axes (unit vectors in GLOBAL model coordinates).</summary>
    public Point3D? PlacementXAxis { get; set; }
    public Point3D? PlacementYAxis { get; set; }
    public Point3D? PlacementZAxis { get; set; }
    /// <summary>"ifc-file" when the placement was parsed from the reference IFC on disk.</summary>
    public string PlacementSource { get; set; } = "";

    public List<ReferenceFaceInfo> Faces { get; set; } = new List<ReferenceFaceInfo>();
    public Dictionary<string, string> Attributes { get; set; } =
        new Dictionary<string, string>();
    public bool Truncated { get; set; }
    public string? Message { get; set; }
}

/// <summary>A capped face polygon from a reference object, expressed in global model mm.</summary>
public sealed class ReferenceFaceInfo
{
    public List<Point3D> Points { get; set; } = new List<Point3D>();
}
