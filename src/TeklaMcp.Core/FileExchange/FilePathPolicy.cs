using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TeklaMcp.Core.FileExchange;

/// <summary>
/// Decides which paths the file-exchange tools (<c>tekla_export_parts_file</c>,
/// <c>tekla_set_udas_from_file</c>, <c>tekla_export_reference_objects_file</c>) may touch.
///
/// The server otherwise has NO file access — the script escape hatch bans it outright
/// (<c>ScriptPolicy.BannedIdentifiers</c>). These tools are the single sanctioned opening,
/// because a geometric reconciliation moves tens of MB of geometry and tens of thousands of
/// GUID→UDA pairs, which cannot pass through an LLM context. The opening is kept narrow:
///
///   * absolute paths only, under an explicit root allow-list;
///   * a data-file extension allow-list — never .exe/.dll/.bat/.ps1/...;
///   * no UNC/device paths, no wildcards, no reserved DOS device names;
///   * paths are canonicalized BEFORE the root check, so "..\..\Windows\System32\x.csv"
///     escapes nothing.
///
/// Roots come from <see cref="RootEnvironmentVariable"/> (';'-separated). When that variable
/// is set it REPLACES the defaults — that is the point of a whitelist, the operator must be
/// able to narrow it. When it is unset the defaults are
/// <c>%LOCALAPPDATA%\TeklaMcp\exchange</c> plus the open model's folder.
/// </summary>
public sealed class FilePathPolicy
{
    /// <summary>Environment variable holding ';'-separated allowed root directories.</summary>
    public const string RootEnvironmentVariable = "TEKLA_MCP_FILE_ROOT";

    /// <summary>Data-file extensions the exchange tools may read or write.</summary>
    private static readonly string[] AllowedExtensions = { ".jsonl", ".csv", ".json", ".txt" };

    /// <summary>
    /// Windows reserved device names. "CON.csv" opens the console, not a file, so the stem is
    /// checked as well as the whole name.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly List<string> _roots;

    /// <summary>Build a policy over an explicit root list (used by tests).</summary>
    public FilePathPolicy(IEnumerable<string>? roots)
    {
        _roots = new List<string>();
        foreach (var root in roots ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var normalized = TryNormalizeRoot(root!);
            if (normalized != null && !_roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                _roots.Add(normalized);
        }
    }

    /// <summary>
    /// Resolve the roots for this process: <see cref="RootEnvironmentVariable"/> when set,
    /// otherwise <c>%LOCALAPPDATA%\TeklaMcp\exchange</c> plus <paramref name="modelFolder"/>
    /// (the open model's directory, when the backend knows it).
    /// </summary>
    public static FilePathPolicy FromEnvironment(string? modelFolder = null)
    {
        string? configured = null;
        try
        {
            configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        }
        catch
        {
            // Environment access can be denied in hosted scenarios — fall back to defaults.
        }

        if (!string.IsNullOrWhiteSpace(configured))
            return new FilePathPolicy(configured!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));

        var roots = new List<string> { DefaultExchangeRoot() };
        if (!string.IsNullOrWhiteSpace(modelFolder)) roots.Add(modelFolder!);
        return new FilePathPolicy(roots);
    }

    /// <summary>The built-in root used when the environment names none.</summary>
    public static string DefaultExchangeRoot()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                return Path.Combine(localAppData, "TeklaMcp", "exchange");
        }
        catch
        {
            // Fall through to the current directory below.
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "TeklaMcp-exchange");
    }

    /// <summary>Allowed root directories, canonicalized. Never null; may be empty.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Human-readable root list for error messages and tool responses.</summary>
    public string DescribeRoots() =>
        _roots.Count == 0 ? "(none configured)" : string.Join("; ", _roots);

    /// <summary>
    /// Validate a path the caller wants to WRITE and create its directory. Returns false with a
    /// caller-facing <paramref name="error"/> — the exchange tools report it in their result DTO
    /// instead of throwing.
    /// </summary>
    public bool TryResolveForWrite(string? path, out string fullPath, out string error)
    {
        if (!TryResolve(path, out fullPath, out error)) return false;

        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);
        }
        catch (Exception ex)
        {
            error = $"Cannot create the directory for '{fullPath}': {ex.Message}";
            fullPath = "";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validate a path the caller wants to READ. Adds an existence check on top of
    /// <see cref="TryResolveForWrite"/>'s rules.
    /// </summary>
    public bool TryResolveForRead(string? path, out string fullPath, out string error)
    {
        if (!TryResolve(path, out fullPath, out error)) return false;

        if (!File.Exists(fullPath))
        {
            error = $"File not found: '{fullPath}'.";
            fullPath = "";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Derive the sidecar status path for an export target: "parts.jsonl" → "parts.jsonl.status.json".
    /// The sidecar records the counters of the last call, so a client-side timeout (~60 s) that
    /// loses the tool response does not lose the evidence that the export completed.
    /// </summary>
    public static string StatusSidecarPath(string exportPath) => exportPath + ".status.json";

    private bool TryResolve(string? path, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path is required.";
            return false;
        }

        var raw = path!.Trim();

        if (raw.IndexOf('*') >= 0 || raw.IndexOf('?') >= 0)
        {
            error = $"Wildcards are not allowed in a path ('{raw}'). Name one file.";
            return false;
        }

        if (raw.StartsWith("\\\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
        {
            error = $"UNC and device paths are not allowed ('{raw}'). Use a local absolute path under: {DescribeRoots()}";
            return false;
        }

        if (!Path.IsPathRooted(raw))
        {
            error = $"'{raw}' is not an absolute path. Pass a full path under: {DescribeRoots()}";
            return false;
        }

        // Checked on the RAW name: Path.GetFullPath rewrites "…\CON.csv" to the device path
        // "\\.\CON", after which the file name no longer looks reserved at all.
        var rawFileName = Path.GetFileName(raw);
        if (ReservedNames.Contains(rawFileName) ||
            ReservedNames.Contains(Path.GetFileNameWithoutExtension(raw)))
        {
            error = $"'{rawFileName}' is a reserved Windows device name.";
            return false;
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(raw);
        }
        catch (Exception ex)
        {
            error = $"'{raw}' is not a usable path: {ex.Message}";
            return false;
        }

        // Re-check after canonicalization: GetFullPath can itself produce a device path.
        if (canonical.StartsWith("\\\\", StringComparison.Ordinal))
        {
            error = $"'{raw}' resolves to the device/UNC path '{canonical}', which is not allowed.";
            return false;
        }

        var fileName = Path.GetFileName(canonical);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            error = $"'{raw}' names a directory, not a file.";
            return false;
        }

        var extension = Path.GetExtension(canonical);
        if (string.IsNullOrEmpty(extension) ||
            !AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Extension '{extension}' is not allowed. Use one of: " +
                    string.Join(", ", AllowedExtensions) + ".";
            return false;
        }

        if (_roots.Count == 0)
        {
            error = "No allowed roots are configured. Set " + RootEnvironmentVariable +
                    " (';'-separated directories) and restart the server.";
            return false;
        }

        if (!_roots.Any(root => IsUnder(canonical, root)))
        {
            error = $"'{canonical}' is outside the allowed roots. Allowed: {DescribeRoots()}. " +
                    "Set " + RootEnvironmentVariable + " to change them (server restart required).";
            return false;
        }

        fullPath = canonical;
        return true;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the root itself or sits below it. Compares on a
    /// separator boundary so "C:\data-private" is NOT considered to be under "C:\data".
    /// </summary>
    private static bool IsUnder(string candidate, string root)
    {
        if (string.Equals(candidate, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryNormalizeRoot(string root)
    {
        try
        {
            var trimmed = root.Trim();
            if (trimmed.Length == 0 || !Path.IsPathRooted(trimmed)) return null;
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }
}
