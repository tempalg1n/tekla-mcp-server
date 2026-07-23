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
