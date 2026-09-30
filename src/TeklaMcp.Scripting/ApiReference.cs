using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Scripting;

/// <summary>
/// Search over the local Tekla Open API Markdown reference produced by
/// <see cref="ApiReferenceGenerator"/> — by the server itself from the installed Tekla on first
/// use, or by the <c>tools/TeklaApiDoc</c> CLI (one <c>Full.Type.Name.md</c> per type, member
/// lines starting with <c>- `</c>). Lets agents verify type/member signatures BEFORE writing a
/// script, instead of hallucinating them. The reference is never shipped (Trimble content), so
/// everything degrades to a "how to generate it" hint when no source is available.
/// </summary>
public static class ApiReference
{
    /// <summary>Env var pointing at the generated reference folder (overrides probing).</summary>
    public const string DirEnvVar = "TEKLA_MCP_API_REF_DIR";

    /// <summary>
    /// The Open API assemblies documented when the server generates the reference itself — the
    /// ones scripts actually use. Kept short on purpose: generation must fit well inside the MCP
    /// client's ~60 s request budget (~7 s for 1 740 types on a Tekla 2021 install).
    /// </summary>
    public static readonly IReadOnlyList<string> CoreAssemblyNames = new[]
    {
        "Tekla.Structures", "Tekla.Structures.Model", "Tekla.Structures.Drawing", "Tekla.Structures.Catalogs",
        "Tekla.Structures.Datatype", "Tekla.Structures.Dialog", "Tekla.Structures.Plugins",
    };

    /// <summary>How long a tool call waits for a first-time generation before answering "retry".</summary>
    public static TimeSpan GenerationWait { get; set; } = TimeSpan.FromSeconds(40);

    private const string HowToGenerate =
        "The local API reference is not available: no Tekla Open API assemblies were found to generate it " +
        "from. With the live backend it is generated automatically from the installed Tekla (on first use, " +
        "cached per version). On the mock backend set TEKLA_MCP_SCRIPT_REF_DIR to a folder with " +
        "Tekla.Structures*.dll, or point " + DirEnvVar + " at a folder generated with tools/TeklaApiDoc. " +
        "Without it, tekla_check_csharp still reports compiler errors.";

    private const int MaxLinesPerType = 8;

    private static readonly object GenerationGate = new object();
    private static readonly Dictionary<string, Task<string>> Generations = new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);

    public static ApiReferenceStatus GetStatus(ApiReferenceSource? source = null)
    {
        var dir = Resolve(source, out var origin, out var pending);
        var result = new ApiReferenceStatus
        {
            Available = dir != null,
            Directory = dir ?? "",
            Origin = origin,
            Generating = pending != null && !pending.StartsWith("Generating the API reference failed", StringComparison.Ordinal),
            Source = source?.Description,
        };
        if (dir == null)
        {
            result.Guidance = pending ?? HowToGenerate;
            return result;
        }
        if (pending != null) result.Warnings.Add(pending);

        try
        {
            var names = Directory.EnumerateFiles(dir, "*.md")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name) &&
                               !string.Equals(name, "INDEX", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .ToList();
            result.TypeCount = names.Count;
            AddModule(result, names, "Tekla.Structures.Model.", "Model");
            AddModule(result, names, "Tekla.Structures.Drawing.", "Drawing");
            AddModule(result, names, "Tekla.Structures.Geometry3d.", "Geometry3d");
            if (!result.Modules.Contains("Model"))
                result.Warnings.Add("Model API pages are missing.");
            if (!result.Modules.Contains("Drawing"))
                result.Warnings.Add(
                    "Drawing API pages are missing; regenerate with Tekla.Structures.Drawing.dll.");
            result.Guidance = result.Warnings.Count == 0
                ? "Offline Tekla API reference is ready (Model + Drawing coverage detected)."
                : "Reference is usable but incomplete: " + string.Join(" ", result.Warnings);
        }
        catch (Exception ex)
        {
            result.Available = false;
            result.Warnings.Add("Reference directory could not be inspected: " + ex.Message);
            result.Guidance = HowToGenerate;
        }
        return result;
    }

    /// <summary>
    /// Locate an existing reference folder without generating anything: the env var, the cached
    /// reference for <paramref name="source"/>'s exact Tekla build, then reference/tekla-api next
    /// to the server or the current directory (a source checkout).
    /// </summary>
    public static string? FindDirectory(ApiReferenceSource? source = null) =>
        FindDirectory(source, out _);

    private static string? FindDirectory(ApiReferenceSource? source, out string origin)
    {
        origin = "configured";
        var env = Environment.GetEnvironmentVariable(DirEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && HasMarkdownFiles(env))
            return env;

        origin = "generated";
        if (source != null && HasMarkdownFiles(CacheDirFor(source)))
            return CacheDirFor(source);

        origin = "repository";
        foreach (var start in new[] { AppContext.BaseDirectory, SafeCurrentDirectory() })
        {
            var dir = start;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "reference", "tekla-api");
                if (HasMarkdownFiles(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
        }
        origin = "";
        return null;
    }

    /// <summary>
    /// The folder to search, generating the reference for <paramref name="source"/> first when
    /// there is no configured folder and no cache for this exact Tekla build yet (the generated one
    /// beats a repository copy of possibly another version). Generation runs in the background: a
    /// call waits up to <see cref="GenerationWait"/>, then falls back to the repository copy, or
    /// answers with <paramref name="pending"/> ("retry shortly").
    /// </summary>
    private static string? Resolve(ApiReferenceSource? source, out string origin, out string? pending)
    {
        pending = null;
        var existing = FindDirectory(source, out origin);
        if (existing != null && origin != "repository") return existing;
        if (source == null || source.AssemblyPaths.Count == 0) return existing;

        var task = StartGeneration(source);
        try
        {
            // With a repository copy to fall back on, never make the caller wait.
            if (task.Wait(existing != null ? TimeSpan.Zero : GenerationWait))
            {
                origin = "generated";
                return task.Result;
            }
            pending = "The API reference for " + source.VersionKey + " is being generated from " + source.Description +
                      " (first use of this Tekla build; usually 10–30 s). Call again shortly.";
        }
        catch (AggregateException ex)
        {
            pending = "Generating the API reference failed: " + ErrorText.Flatten(ex.InnerException ?? ex);
        }
        return existing; // a repository copy, if any, until the generated one is ready
    }

    private static Task<string> StartGeneration(ApiReferenceSource source)
    {
        lock (GenerationGate)
        {
            // A failed generation is not retried for the life of the process: every call would pay
            // for it again. Its error is reported instead; a server restart retries.
            if (Generations.TryGetValue(source.VersionKey, out var running))
                return running;
            var task = Task.Run(() => GenerateInto(source));
            Generations[source.VersionKey] = task;
            return task;
        }
    }

    /// <summary>Generate into a temp folder, then rename: a half-written reference is never served.</summary>
    private static string GenerateInto(ApiReferenceSource source)
    {
        var final = CacheDirFor(source);
        if (HasMarkdownFiles(final)) return final;
        var temp = final + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var count = ApiReferenceGenerator.Generate(
                source.AssemblyPaths, source.XmlDirectories, temp,
                log: message => Console.Error.WriteLine("[api-reference] " + message),
                dependencyDirectories: source.DependencyDirectories);
            if (count == 0)
                throw new InvalidOperationException(
                    "No Tekla.Structures types could be read from " + source.Description + " — the files are " +
                    "not Tekla Open API assemblies, or their dependencies are missing.");
            File.WriteAllText(Path.Combine(temp, "SOURCE.txt"),
                source.Description + Environment.NewLine + string.Join(Environment.NewLine, source.AssemblyPaths) +
                Environment.NewLine + count + " types, generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            try
            {
                Directory.Move(temp, final);
            }
            catch (IOException) when (HasMarkdownFiles(final))
            {
                // Another server process finished first — use its copy.
            }
            Console.Error.WriteLine($"[api-reference] generated {count} types for {source.VersionKey} -> {final}");
            return final;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string CacheDirFor(ApiReferenceSource source)
    {
        var root = !string.IsNullOrWhiteSpace(source.CacheRoot)
            ? source.CacheRoot!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeklaMcp", "api-reference");
        var key = new string((source.VersionKey ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return Path.Combine(root, (key.Length == 0 ? "unknown" : key) + "-r" + ApiReferenceGenerator.FormatVersion);
    }

    public static ApiSearchResult Search(string query, int limit = 10, ApiReferenceSource? source = null)
    {
        var result = new ApiSearchResult { Query = query ?? "" };

        var dir = Resolve(source, out _, out var pending);
        if (dir == null)
        {
            result.Guidance = pending ?? HowToGenerate;
            return result;
        }

        result.ReferenceAvailable = true;
        result.ReferenceDir = dir;

        var tokens = Tokenize(query);
        if (tokens.Count == 0)
        {
            result.Guidance = "Query is empty — pass a type or member name fragment, e.g. 'Beam profile' or 'GetReportProperty'.";
            return result;
        }

        limit = Math.Min(Math.Max(limit, 1), 50);

        // Score every type file: strong signal when all tokens appear in the type name,
        // otherwise match member signature lines (all tokens on one line, case-insensitive).
        var hits = new List<(ApiSearchHit Hit, int Score)>();
        IEnumerable<string> searchFiles;
        try { searchFiles = Directory.EnumerateFiles(dir, "*.md").ToList(); }
        catch (Exception ex)
        {
            result.ReferenceAvailable = false;
            result.Guidance = "Reference directory could not be read: " + ex.Message + ". " + HowToGenerate;
            return result;
        }
        foreach (var file in searchFiles)
        {
            var typeName = Path.GetFileNameWithoutExtension(file);
            if (typeName.Equals("INDEX", StringComparison.OrdinalIgnoreCase))
                continue;

            var nameMatches = tokens.Count(t => typeName.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            List<string> lines;
            try
            {
                lines = MatchMemberLines(file, tokens);
            }
            catch
            {
                continue;
            }

            if (nameMatches < tokens.Count && lines.Count == 0)
                continue;

            var score = (nameMatches == tokens.Count ? 1000 - typeName.Length : 0) + lines.Count;
            hits.Add((new ApiSearchHit { Type = typeName, MatchingMembers = lines }, score));
        }

        foreach (var hit in hits.OrderByDescending(h => h.Score).Take(limit))
            result.Hits.Add(hit.Hit);
        result.TotalMatches = hits.Count;

        result.Guidance = result.Hits.Count == 0
            ? "No matches. Try fewer/shorter tokens (e.g. 'ReportProperty' instead of a full sentence), or a namespace fragment like 'Model.UI'."
            : "Use tekla_get_api_doc with a Type value to read the full signatures for that type.";
        return result;
    }

    public static ApiTypeDoc GetTypeDoc(string typeName, int maxChars = 24_000, ApiReferenceSource? source = null)
    {
        var result = new ApiTypeDoc { TypeName = typeName ?? "" };

        var dir = Resolve(source, out _, out var pending);
        if (dir == null)
        {
            result.Guidance = pending ?? HowToGenerate;
            return result;
        }

        if (string.IsNullOrWhiteSpace(typeName))
        {
            result.Guidance = "Pass a type name, e.g. 'Beam' or 'Tekla.Structures.Model.Beam'.";
            return result;
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.md")
                .Where(f => !Path.GetFileNameWithoutExtension(f).Equals(
                    "INDEX", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            result.Guidance = "Reference directory could not be read: " + ex.Message + ". " + HowToGenerate;
            return result;
        }

        // Exact full name, then exact short name (suffix), then substring — all case-insensitive.
        var trimmed = typeName!.Trim();
        var file = files.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (file == null)
        {
            var exactShortMatches = files.Where(f =>
                    Path.GetFileNameWithoutExtension(f).EndsWith(
                        "." + trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exactShortMatches.Count > 1)
            {
                result.Suggestions = exactShortMatches
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => name != null)
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToList();
                result.Guidance =
                    "Short type name is ambiguous. Pass one fully-qualified name from Suggestions.";
                return result;
            }
            file = exactShortMatches.SingleOrDefault();
        }

        if (file == null)
        {
            result.Suggestions = files
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n!.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => n!.Length)
                .Take(10)
                .Select(n => n!)
                .ToList();
            result.Guidance = result.Suggestions.Count > 0
                ? "No exact type match — did you mean one of Suggestions?"
                : "No type matched. Use tekla_search_api to find the right type name first.";
            return result;
        }

        result.Found = true;
        result.TypeName = Path.GetFileNameWithoutExtension(file);
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception ex)
        {
            result.Found = false;
            result.Guidance = "The matched reference page could not be read: " + ex.Message;
            return result;
        }
        maxChars = Math.Min(Math.Max(maxChars, 1_000), 100_000);
        if (text.Length > maxChars)
        {
            text = text.Substring(0, maxChars) + "\n…(truncated — ask for a larger maxChars if needed)";
            result.Truncated = true;
        }
        result.Markdown = text;
        return result;
    }

    private static List<string> MatchMemberLines(string file, List<string> tokens)
    {
        var matches = new List<string>();
        foreach (var raw in File.ReadLines(file))
        {
            if (!raw.StartsWith("- ", StringComparison.Ordinal))
                continue;
            var line = raw;
            if (tokens.All(t => line.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                matches.Add(line.Length > 300 ? line.Substring(0, 300) + "…" : line);
                if (matches.Count >= MaxLinesPerType)
                    break;
            }
        }
        return matches;
    }

    private static List<string> Tokenize(string? query)
        => (query ?? "")
            .Split(new[] { ' ', '\t', ',', ';', '(', ')', '.', '`', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

    private static string SafeCurrentDirectory()
    {
        try { return Directory.GetCurrentDirectory(); }
        catch { return AppContext.BaseDirectory; }
    }

    private static bool HasMarkdownFiles(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        try { return Directory.EnumerateFiles(directory, "*.md").Any(); }
        catch { return false; }
    }

    private static void AddModule(
        ApiReferenceStatus result,
        IEnumerable<string> names,
        string prefix,
        string module)
    {
        if (names.Any(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            result.Modules.Add(module);
    }
}
