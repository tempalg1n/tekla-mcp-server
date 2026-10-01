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
/// report). Tekla 2026 splits the API over two folders (see <see cref="Net48RuntimeFolder"/>).
/// Pure path logic, so it is unit-testable without Tekla.
/// </summary>
public static class TeklaBinLayout
{
    /// <summary>The file that identifies an Open API folder.</summary>
    public const string ModelAssemblyFile = "Tekla.Structures.Model.dll";

    /// <summary>
    /// Subfolder of the Open API folder with the .NET Framework builds of assemblies Tekla also ships
    /// for newer runtimes. Tekla 2026 keeps <c>Tekla.Structures.dll</c> and
    /// <c>Tekla.Structures.Internal.dll</c> ONLY there, while <c>Tekla.Structures.Model.dll</c>,
    /// <c>Trimble.Remoting.dll</c> and <c>DotNetKit.dll</c> stay in <c>bin</c>. Tekla's own process
    /// finds them through a privatePath and a codeBase in TeklaStructures.exe.config, which this
    /// server does not inherit (issue #17).
    /// </summary>
    public const string Net48RuntimeFolder = "Net48Runtime";

    /// <summary>
    /// The folders the assemblies of the Tekla install behind <paramref name="apiDirectory"/> are
    /// looked up in, in order: the Open API folder, then its <see cref="Net48RuntimeFolder"/> when it
    /// exists — the order TeklaStructures.exe.config gives Tekla's own process (application base,
    /// then privatePath). Empty for a blank folder.
    /// </summary>
    public static IReadOnlyList<string> ProbeDirectories(string? apiDirectory, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(apiDirectory)) return Array.Empty<string>();
        var dir = apiDirectory!.Trim().Trim('"');
        if (dir.Length == 0) return Array.Empty<string>();

        var runtime = Path.Combine(dir, Net48RuntimeFolder);
        return directoryExists(runtime) ? new[] { dir, runtime } : new[] { dir };
    }

    /// <summary>
    /// The first <c>&lt;folder&gt;\&lt;simpleName&gt;.dll</c> that exists in
    /// <paramref name="directories"/>, or null. A name that is not a plain file name (a path
    /// separator, a drive colon, an invalid character) finds nothing: it must never reach outside
    /// those folders.
    /// </summary>
    public static string? FindAssemblyFile(string? simpleName, IEnumerable<string> directories, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(simpleName)) return null;
        if (simpleName!.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 ||
            simpleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        foreach (var dir in directories)
        {
            var path = Path.Combine(dir, simpleName + ".dll");
            if (fileExists(path)) return path;
        }
        return null;
    }

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
