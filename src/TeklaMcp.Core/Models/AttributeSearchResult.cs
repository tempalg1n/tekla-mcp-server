using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Result of searching attribute names by a known value
/// (<c>ITeklaModelService.FindAttributesByValue</c>).
///
/// This wrapper exists so "found nothing" is never ambiguous: an empty
/// <see cref="Matches"/> list together with <see cref="ScannedObjects"/> and
/// <see cref="Truncated"/> tells the caller whether the value is genuinely absent from the
/// scanned scope, or whether the scan simply did not look far enough (or failed — see
/// <see cref="Message"/>). A bare empty list used to send agents down false trails.
/// </summary>
public sealed class AttributeSearchResult
{
    /// <summary>Attribute names that matched, ordered by match count descending.</summary>
    public List<AttributeValueMatch> Matches { get; set; } = new List<AttributeValueMatch>();

    /// <summary>Objects actually inspected.</summary>
    public int ScannedObjects { get; set; }

    /// <summary>Number of candidate attribute names each object was checked against.</summary>
    public int CandidatesTried { get; set; }

    /// <summary>
    /// True when the scan stopped at <c>objectLimit</c> before exhausting the scope — an empty
    /// <see cref="Matches"/> then means "not found in the first N objects", NOT "absent".
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>Scan scope description, hints, or an error explanation.</summary>
    public string Message { get; set; } = "";

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";
}
