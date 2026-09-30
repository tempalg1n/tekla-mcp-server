using System;

namespace TeklaMcp.Core;

/// <summary>
/// Compares a caller's <c>expectedModelPath</c> with the model folder Tekla reports, for the
/// write-target check (backlog §4). Windows semantics: case-insensitive, either separator, no
/// trailing separator; the model's own <c>.db1</c> file inside that folder names the same model.
/// Deliberately no prefix/fuzzy matching — a near miss must refuse the write.
/// </summary>
public static class ModelPaths
{
    public static bool Same(string? expected, string? actual)
    {
        var want = Normalize(expected);
        var have = Normalize(actual);
        if (want.Length == 0 || have.Length == 0) return false;
        if (string.Equals(want, have, StringComparison.OrdinalIgnoreCase)) return true;

        if (want.EndsWith(".db1", StringComparison.OrdinalIgnoreCase))
        {
            var slash = want.LastIndexOf('/');
            return slash > 0 && string.Equals(want.Substring(0, slash), have, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string Normalize(string? path) =>
        (path ?? "").Trim().Trim('"').Replace('\\', '/').TrimEnd('/');
}
