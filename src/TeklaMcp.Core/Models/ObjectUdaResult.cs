using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// UDA values read from a single model object.
/// </summary>
public sealed class ObjectUdaResult
{
    /// <summary>Requested object GUID.</summary>
    public string Guid { get; set; } = "";

    /// <summary>Object integer identifier, when found.</summary>
    public int? Id { get; set; }

    /// <summary>Object runtime type, e.g. Beam/ContourPlate/Bolt.</summary>
    public string? Type { get; set; }

    /// <summary>UDA key/value pairs that were found.</summary>
    public Dictionary<string, string> Udas { get; set; } = new Dictionary<string, string>();

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>Optional status or error message.</summary>
    public string? Message { get; set; }
}
