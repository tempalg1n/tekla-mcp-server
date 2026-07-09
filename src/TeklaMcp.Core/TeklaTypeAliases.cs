using System;
using System.Collections.Generic;

namespace TeklaMcp.Core;

/// <summary>
/// Bridges the friendly object-type names an agent naturally types ("Bolt", "Plate") to the
/// concrete runtime type names Tekla actually reports (<c>BoltArray</c>, <c>ContourPlate</c>).
/// Without this, a filter like <c>type="Bolt"</c> silently returns nothing on the live backend
/// because <c>ModelObject.GetType().Name</c> is "BoltArray". Both the mock and the live backend
/// route their type comparison through <see cref="TypeMatches"/> so the two behave identically.
/// </summary>
public static class TeklaTypeAliases
{
    // Friendly alias (case-insensitive) => the concrete type names it should also match.
    // Keep entries conservative: only add a mapping when the concrete name is well known,
    // so a type filter never silently widens to something the agent did not intend.
    private static readonly Dictionary<string, string[]> AliasToConcrete =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bolt"] = new[] { "BoltArray", "BoltGroup" },
            ["Bolts"] = new[] { "BoltArray", "BoltGroup" },
            ["Plate"] = new[] { "ContourPlate" },
        };

    /// <summary>
    /// The set of concrete type names a queried type should match: the queried name itself
    /// plus any aliases. Case-insensitive. Returns an empty set for null/blank input.
    /// </summary>
    public static IReadOnlyCollection<string> ResolveTypeNames(string? queriedType)
    {
        if (string.IsNullOrWhiteSpace(queriedType)) return Array.Empty<string>();
        var q = queriedType!.Trim();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { q };
        if (AliasToConcrete.TryGetValue(q, out var concrete))
            foreach (var name in concrete) set.Add(name);
        return set;
    }

    /// <summary>
    /// True if <paramref name="concreteType"/> (as reported by Tekla, e.g. "BoltArray")
    /// satisfies a <paramref name="queriedType"/> filter (e.g. "Bolt"). A null/blank query
    /// matches anything, mirroring the "empty filter is ignored" contract of ObjectQuery.
    /// </summary>
    public static bool TypeMatches(string? concreteType, string? queriedType)
    {
        if (string.IsNullOrWhiteSpace(queriedType)) return true;
        foreach (var name in ResolveTypeNames(queriedType))
            if (string.Equals(concreteType, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
