using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>Availability/setup diagnostics for the offline Tekla API reference.</summary>
public sealed class ApiReferenceStatus
{
    public bool Available { get; set; }
    public string Directory { get; set; } = "";
    public int TypeCount { get; set; }
    public List<string> Modules { get; set; } = new List<string>();
    public List<string> Warnings { get; set; } = new List<string>();
    public string Guidance { get; set; } = "";
}
