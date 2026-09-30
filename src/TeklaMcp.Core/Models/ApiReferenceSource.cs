using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Where the offline API reference can be generated from on this machine: the installed Tekla's
/// Open API assemblies and the XML docs Tekla ships next to them. Supplied by the backend (it knows
/// the Tekla folder); the generator itself is Tekla-free.
/// </summary>
public sealed class ApiReferenceSource
{
    /// <summary>Cache key: one generated reference per Tekla build, e.g. "tekla-2023.0.29842.0".</summary>
    public string VersionKey { get; set; } = "";

    public List<string> AssemblyPaths { get; set; } = new List<string>();

    /// <summary>Folders searched for the XML doc files (e.g. the Tekla bin folder).</summary>
    public List<string> XmlDirectories { get; set; } = new List<string>();

    /// <summary>
    /// Extra folders to resolve dependencies from (Tekla 2021 keeps the Open API in nt/bin/plugins
    /// but some of its dependencies one level up).
    /// </summary>
    public List<string> DependencyDirectories { get; set; } = new List<string>();

    /// <summary>Human-readable origin, shown in the status.</summary>
    public string Description { get; set; } = "";

    /// <summary>Cache root override (tests); null = %LOCALAPPDATA%\TeklaMcp\api-reference.</summary>
    public string? CacheRoot { get; set; }
}
