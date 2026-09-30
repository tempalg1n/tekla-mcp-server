using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Scripting;

/// <summary>The outcome of <see cref="ScriptReferenceSelector.Select"/>.</summary>
public sealed class ScriptReferenceSelection
{
    /// <summary>Files to reference, loaded assemblies first — pass to <see cref="ScriptEngine.BuildReferences"/>.</summary>
    public List<string> Paths { get; } = new List<string>();

    /// <summary>Every candidate with its decision: included, or the reason it was left out.</summary>
    public List<ScriptReferenceInfo> Report { get; } = new List<ScriptReferenceInfo>();

    /// <summary>The Tekla major version (year) the set was built for; null when unknown.</summary>
    public int? TeklaMajor { get; set; }

    /// <summary>True when both Tekla.Structures and Tekla.Structures.Model are referenced.</summary>
    public bool HasCoreApi { get; set; }

    /// <summary>One line for <see cref="ScriptResult.ReferenceSummary"/>.</summary>
    public string Summary { get; set; } = "";
}

/// <summary>
/// Chooses the Tekla metadata references for a script compile, and records why.
///
/// Assemblies already loaded in this process are authoritative: they are what the live
/// connection binds (from the Tekla folder or from the GAC), so a compile against them cannot
/// disagree with execution. The Tekla Open API folder only ADDS what nobody loaded yet
/// (Drawing, Dialog, Plugins, Catalogs, …). A file is left out when it is not a managed
/// assembly, duplicates an assembly already in the set, or belongs to another Tekla year —
/// 2021 and 2023 Open API assemblies in one compile would bind types from both.
///
/// Background (DEV-005 field report, Tekla 2021): references used to be "every
/// Tekla.Structures*.dll in the resolver's folder", with loaded assemblies only as a fallback
/// for an EMPTY glob. With the folder at 2021's nt\bin — seven unrelated Tekla.Structures.*.dll;
/// the API lives in nt\bin\plugins — every script compiled without Tekla.Structures.Model,
/// while the connection itself worked from the GAC.
/// </summary>
public static class ScriptReferenceSelector
{
    public const string OriginLoaded = "loaded";
    public const string OriginDirectory = "directory";

    private const string BaseAssembly = "Tekla.Structures";
    private const string ModelAssembly = "Tekla.Structures.Model";

    /// <param name="loadedAssemblyPaths">Locations of Tekla assemblies loaded in this process.</param>
    /// <param name="directoryFiles">Candidate files from the Tekla Open API folder.</param>
    /// <param name="expectedTeklaMajor">The Tekla year this server was built for, when known;
    /// otherwise the first Tekla.Structures.Model found decides.</param>
    public static ScriptReferenceSelection Select(
        IEnumerable<string>? loadedAssemblyPaths,
        IEnumerable<string>? directoryFiles,
        int? expectedTeklaMajor)
    {
        var candidates = new List<Candidate>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidates(IEnumerable<string>? paths, string origin)
        {
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var full = SafeFullPath(path);
                if (seenPaths.Add(full)) // a loaded assembly usually also sits in the folder
                    candidates.Add(Candidate.Read(full, origin));
            }
        }

        AddCandidates(loadedAssemblyPaths, OriginLoaded);
        AddCandidates(directoryFiles, OriginDirectory);

        var selection = new ScriptReferenceSelection
        {
            TeklaMajor = expectedTeklaMajor ?? candidates
                .Where(c => c.Version != null && IsTeklaYear(c.Version) &&
                            string.Equals(c.Name, ModelAssembly, StringComparison.OrdinalIgnoreCase))
                .Select(c => (int?)c.Version!.Major)
                .FirstOrDefault(),
        };

        var chosen = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates) // loaded first, then the folder
        {
            var info = new ScriptReferenceInfo
            {
                Name = candidate.Name,
                Version = candidate.Version?.ToString() ?? "",
                Path = candidate.Path,
                Origin = candidate.Origin,
            };
            selection.Report.Add(info);

            if (candidate.Version is null)
            {
                info.Reason = "not a managed assembly (native DLL or unreadable file)";
                continue;
            }

            if (chosen.TryGetValue(candidate.Name, out var existing))
            {
                info.Reason = existing.Version == candidate.Version
                    ? $"same assembly already referenced from {existing.Origin} ({existing.Path})"
                    : $"version {candidate.Version} conflicts with {existing.Version} already referenced " +
                      $"from {existing.Origin} ({existing.Path})";
                continue;
            }

            if (selection.TeklaMajor is int major && IsTeklaYear(candidate.Version) &&
                candidate.Version.Major != major)
            {
                info.Reason = $"Tekla {candidate.Version.Major} assembly in a Tekla {major} reference set " +
                              "(mixed Tekla versions are rejected)";
                continue;
            }

            chosen[candidate.Name] = candidate;
            info.Included = true;
            selection.Paths.Add(candidate.Path);
        }

        selection.HasCoreApi = chosen.ContainsKey(BaseAssembly) && chosen.ContainsKey(ModelAssembly);
        selection.Summary = Summarize(selection, chosen);
        return selection;
    }

    /// <summary>
    /// Guidance after a failed compile: a reference set without the core API is a server or
    /// installation problem, and rewriting the script cannot fix it.
    /// </summary>
    public static string CompileFailureGuidance(ScriptReferenceSelection selection) =>
        selection.HasCoreApi
            ? "Fix the compile errors and retry. Verify signatures with tekla_search_api."
            : "The compile had no core Tekla API (see referenceSummary / references): this is a server " +
              "installation problem, not a script error — do not rewrite the script to work around it. " +
              "Tell the user and report it with tekla_report_gap.";

    private static string Summarize(ScriptReferenceSelection selection, Dictionary<string, Candidate> chosen)
    {
        var included = selection.Report.Count(r => r.Included);
        var fromProcess = selection.Report.Count(r => r.Included && r.Origin == OriginLoaded);
        var leftOut = selection.Report.Count - included;

        var text = $"{included} Tekla reference(s) for Tekla " +
                   (selection.TeklaMajor?.ToString() ?? "(unknown version)") +
                   $": {fromProcess} loaded in the server process, {included - fromProcess} from the Tekla API folder" +
                   (leftOut > 0 ? $"; {leftOut} left out (see references)." : ".");

        if (!selection.HasCoreApi)
        {
            var missing = new[] { BaseAssembly, ModelAssembly }.Where(name => !chosen.ContainsKey(name));
            text += " MISSING " + string.Join(" and ", missing) +
                    " — scripts cannot use the Tekla model API with this server installation.";
        }
        return text;
    }

    private static bool IsTeklaYear(Version version) => version.Major >= 2000 && version.Major < 2100;

    private static string SafeFullPath(string path)
    {
        try { return System.IO.Path.GetFullPath(path.Trim()); }
        catch { return path.Trim(); }
    }

    private sealed class Candidate
    {
        public string Path { get; private set; } = "";
        public string Origin { get; private set; } = "";
        public string Name { get; private set; } = "";

        /// <summary>Null when the file is not a managed assembly.</summary>
        public Version? Version { get; private set; }

        /// <summary>Reads the assembly identity from metadata only; the file is never loaded.</summary>
        public static Candidate Read(string path, string origin)
        {
            var candidate = new Candidate
            {
                Path = path,
                Origin = origin,
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
            };
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) return candidate;
                var metadata = pe.GetMetadataReader();
                if (!metadata.IsAssembly) return candidate;
                var definition = metadata.GetAssemblyDefinition();
                candidate.Name = metadata.GetString(definition.Name);
                candidate.Version = definition.Version;
            }
            catch
            {
                // Missing, locked or not a PE image: reported as "not a managed assembly".
            }
            return candidate;
        }
    }
}
