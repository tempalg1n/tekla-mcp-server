using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Aggregated matches for one attribute when searching by a known value.
/// </summary>
public sealed class AttributeValueMatch
{
    /// <summary>Matched attribute name.</summary>
    public string AttributeName { get; set; } = "";

    /// <summary>Number of objects where this attribute matched the requested value.</summary>
    public int MatchCount { get; set; }

    /// <summary>Distinct matched values (helpful for non-exact searches).</summary>
    public List<string> MatchedValues { get; set; } = new List<string>();

    /// <summary>Sample object GUIDs to inspect manually.</summary>
    public List<string> SampleGuids { get; set; } = new List<string>();
}
