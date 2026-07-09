using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Filter criteria for searching model objects. Every field is optional:
/// null/empty fields are ignored. <see cref="Type"/> and <see cref="Class"/> match
/// exactly (case-insensitive); the rest match as case-insensitive substrings.
/// </summary>
public sealed class ObjectQuery
{
    /// <summary>
    /// Exact object type, e.g. "Beam", "ContourPlate", "Bolt". Friendly aliases are accepted:
    /// "Bolt" also matches Tekla's "BoltArray"/"BoltGroup" and "Plate" matches "ContourPlate"
    /// (see <see cref="TeklaTypeAliases"/>), so the filter works with the names an agent expects.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>Exact Tekla class, e.g. "2".</summary>
    public string? Class { get; set; }

    /// <summary>Profile substring, e.g. "IPE" or "HEA300".</summary>
    public string? Profile { get; set; }

    /// <summary>Material substring, e.g. "S355".</summary>
    public string? Material { get; set; }

    /// <summary>Substring of the object name.</summary>
    public string? NameContains { get; set; }

    /// <summary>UDA field name for exact match, e.g. 'RU_FN1_MRK'.</summary>
    public string? UdaName { get; set; }

    /// <summary>Exact UDA value to match (case-insensitive).</summary>
    public string? UdaEquals { get; set; }

    /// <summary>
    /// Generic attribute/report/UDA name to match, e.g. "ASSEMBLY_POS" or "RU_FN1_MRK".
    /// </summary>
    public string? AttributeName { get; set; }

    /// <summary>Exact attribute value to match (case-insensitive).</summary>
    public string? AttributeEquals { get; set; }

    /// <summary>Substring attribute value match (case-insensitive).</summary>
    public string? AttributeContains { get; set; }

    /// <summary>
    /// Attribute value the object must NOT have (case-insensitive). Requires the attribute
    /// named by <see cref="AttributeName"/> to be present — objects that lack it are excluded.
    /// Enables "everything whose BOLT_GRADE is not 88" filters.
    /// </summary>
    public string? AttributeNotEquals { get; set; }

    /// <summary>
    /// Optional explicit GUID allow-list. When provided, only objects from this list are matched.
    /// </summary>
    public List<string> GuidIn { get; set; } = new List<string>();

    /// <summary>
    /// When true, the query operates ONLY on the objects currently selected by the user in
    /// the Tekla UI, instead of scanning the whole model. Enables "analyze what I selected"
    /// workflows and is much faster on large models. Ignored by the mock unless a selection
    /// has been made.
    /// </summary>
    public bool UseSelection { get; set; }
}
