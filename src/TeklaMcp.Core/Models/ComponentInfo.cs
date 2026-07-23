using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>Serializable snapshot of a connection/component attached to a part.</summary>
public sealed class ComponentInfo
{
    public string Guid { get; set; } = "";
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public int Number { get; set; }
    public string PrimaryGuid { get; set; } = "";
    public List<string> SecondaryGuids { get; set; } = new List<string>();
    public Point3D? UpVector { get; set; }
    public string AutoDirection { get; set; } = "";
    public string Status { get; set; } = "";
}
