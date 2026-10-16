using System.Collections.Generic;
using System.Linq;
using System.Text;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core;

/// <summary>
/// What a mark or a view label shows, from its content elements (issue #32). Tekla's
/// <c>ContainerElement.GetUnformattedString()</c> frames its result — an empty mark comes back as
/// <c>"{\n}\n"</c>, a filled one as <c>"{\nК30(?)}\n"</c> (live Tekla 2021) — so "blank text" cannot
/// be judged from it; the elements are joined instead.
/// </summary>
public static class MarkText
{
    /// <summary>The elements' values in order (property values, fixed text, "\n" for new lines), trimmed.</summary>
    public static string Compose(IEnumerable<MarkContentElement> elements)
    {
        var text = new StringBuilder();
        foreach (var element in elements ?? Enumerable.Empty<MarkContentElement>())
            text.Append(element.Kind == "newline" ? "\n" : element.Value ?? "");
        return text.ToString().Trim();
    }

    /// <summary>
    /// Removes the <c>{ … }</c> frames and line breaks <c>GetUnformattedString()</c> puts around a
    /// container's text — for element types whose value is only available that way.
    /// </summary>
    public static string StripFraming(string? unformatted)
    {
        var text = (unformatted ?? "").Trim();
        while (text.StartsWith("{") && text.EndsWith("}"))
            text = text.Substring(1, text.Length - 2).Trim();
        return text;
    }
}
