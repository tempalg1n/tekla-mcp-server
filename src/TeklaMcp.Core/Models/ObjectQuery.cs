namespace TeklaMcp.Core.Models;

/// <summary>
/// Filter criteria for searching model objects. Every field is optional:
/// null/empty fields are ignored. <see cref="Type"/> and <see cref="Class"/> match
/// exactly (case-insensitive); the rest match as case-insensitive substrings.
/// </summary>
public sealed class ObjectQuery
{
    /// <summary>Exact object type, e.g. "Beam", "ContourPlate", "Bolt".</summary>
    public string? Type { get; set; }

    /// <summary>Exact Tekla class, e.g. "2".</summary>
    public string? Class { get; set; }

    /// <summary>Profile substring, e.g. "IPE" or "HEA300".</summary>
    public string? Profile { get; set; }

    /// <summary>Material substring, e.g. "S355".</summary>
    public string? Material { get; set; }

    /// <summary>Substring of the object name.</summary>
    public string? NameContains { get; set; }
}
