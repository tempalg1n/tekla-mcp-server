using System;
using System.Collections.Generic;
using System.IO;

namespace TeklaMcp.Core;

/// <summary>
/// Where an installed Tekla keeps its Open API assemblies. Tekla 2023+ ships them directly in
/// <c>bin</c>; Tekla 2021 keeps them in <c>nt\bin\plugins</c>, while <c>nt\bin</c> itself holds
/// seven unrelated <c>Tekla.Structures.*.dll</c> files (Service, Ui.WpfKit, …). Trusting
/// <c>nt\bin</c> made the script escape hatch compile against those seven and fail on
/// <c>Tekla.Structures.Model</c> while the connection itself worked from the GAC (DEV-005 field
/// report). Pure path logic, so it is unit-testable without Tekla.
/// </summary>
public static class TeklaBinLayout
{
    /// <summary>The file that identifies an Open API folder.</summary>
    public const string ModelAssemblyFile = "Tekla.Structures.Model.dll";

    /// <summary>
    /// Folders to probe under <paramref name="root"/>, most specific first: the folder itself,
    /// its 2021-style <c>plugins</c> subfolder, then the layouts below an install root.
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories(string root) => new[]
    {
        root,
        Path.Combine(root, "plugins"),
        Path.Combine(root, "bin"),
        Path.Combine(root, "bin", "plugins"),
        Path.Combine(root, "nt", "bin", "plugins"),
        Path.Combine(root, "nt", "bin"),
    };

    /// <summary>
    /// The first candidate under <paramref name="root"/> that holds
    /// <see cref="ModelAssemblyFile"/>, or null when none does (or the root is blank).
    /// </summary>
    public static string? FindApiDirectory(string? root, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var trimmed = root!.Trim().Trim('"');
        if (trimmed.Length == 0) return null;

        foreach (var dir in CandidateDirectories(trimmed))
        {
            if (fileExists(Path.Combine(dir, ModelAssemblyFile)))
                return dir;
        }
        return null;
    }
}
