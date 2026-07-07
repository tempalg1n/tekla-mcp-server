using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Full reference page for one Tekla Open API type (tekla_get_api_doc): the generated
/// Markdown with all constructor/property/method signatures and summaries.
/// </summary>
public sealed class ApiTypeDoc
{
    /// <summary>True when the type page was found.</summary>
    public bool Found { get; set; }

    /// <summary>Resolved full type name (or the query as received when not found).</summary>
    public string TypeName { get; set; } = "";

    /// <summary>The type's reference page (Markdown). Null when not found.</summary>
    public string? Markdown { get; set; }

    /// <summary>True when <see cref="Markdown"/> was cut at maxChars.</summary>
    public bool Truncated { get; set; }

    /// <summary>Close type-name matches when the exact type was not found.</summary>
    public List<string> Suggestions { get; set; } = new List<string>();

    /// <summary>What the agent should do next (hint, not an error).</summary>
    public string? Guidance { get; set; }
}
