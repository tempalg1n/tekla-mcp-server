using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Result of searching the local Tekla Open API reference (tekla_search_api).
/// When the generated reference folder is missing, <see cref="ReferenceAvailable"/> is false
/// and <see cref="Guidance"/> explains how to generate it.
/// </summary>
public sealed class ApiSearchResult
{
    /// <summary>The query as received.</summary>
    public string Query { get; set; } = "";

    /// <summary>False when the generated reference folder was not found.</summary>
    public bool ReferenceAvailable { get; set; }

    /// <summary>Where the reference was found (for diagnostics).</summary>
    public string? ReferenceDir { get; set; }

    /// <summary>Matching types, best first.</summary>
    public List<ApiSearchHit> Hits { get; set; } = new List<ApiSearchHit>();

    /// <summary>Total matching types before the limit was applied.</summary>
    public int TotalMatches { get; set; }

    /// <summary>What the agent should do next (hint, not an error).</summary>
    public string? Guidance { get; set; }
}

/// <summary>One matching type in the API reference.</summary>
public sealed class ApiSearchHit
{
    /// <summary>Full type name, e.g. "Tekla.Structures.Model.Beam".</summary>
    public string Type { get; set; } = "";

    /// <summary>Member signature lines that matched the query (capped).</summary>
    public List<string> MatchingMembers { get; set; } = new List<string>();
}
