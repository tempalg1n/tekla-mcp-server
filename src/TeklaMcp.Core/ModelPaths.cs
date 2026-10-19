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

    /// <summary>
    /// Whether Tekla's <c>ModelInfo</c> describes an open model. With the model closed and
    /// Tekla still running, Tekla 2021 answers <c>ModelPath = ""</c> and <c>ModelName = ".db1"</c>
    /// (field report, issue #25 §8) — and every scan then sees zero objects.
    /// </summary>
    public static bool IsOpenModel(string? modelName, string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return false;
        var name = (modelName ?? "").Trim();
        if (name.EndsWith(".db1", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        return name.Length > 0;
    }

    /// <summary>The refusal every model read gives when no model is open.</summary>
    public static string NoOpenModelMessage(string? modelName, string? modelPath) =>
        $"Tekla is running, but no model is open (Tekla reports model path '{modelPath ?? ""}', name " +
        $"'{modelName ?? ""}'). Open a model in Tekla (or with tekla_open_model) and call again — an " +
        "empty answer now would NOT mean an empty model.";

    private static string Normalize(string? path) =>
        (path ?? "").Trim().Trim('"').Replace('\\', '/').TrimEnd('/');
}
