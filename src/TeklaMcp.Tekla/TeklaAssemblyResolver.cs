using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace TeklaMcp.Tekla;

/// <summary>
/// Makes a single net48 build work with ANY installed Tekla version (2021+).
///
/// The project compiles against a BASELINE Tekla API (the lowest supported version) and does NOT
/// ship the Tekla DLLs. At runtime this resolver supplies the Tekla.* assemblies (and their
/// dependency closure) from the actually installed/running Tekla, so the version-locked Open API
/// protocol always matches the running Tekla.
///
/// <see cref="Register"/> must be called once at startup, BEFORE any Tekla type is touched.
/// </summary>
public static class TeklaAssemblyResolver
{
    private static readonly object Gate = new object();
    private static bool _registered;

    /// <summary>The Tekla 'bin' directory assemblies are resolved from, or null if not found.</summary>
    public static string? BinDir { get; private set; }

    /// <summary>How <see cref="BinDir"/> was located: "env" | "process" | "registry" | "(not found)".</summary>
    public static string Source { get; private set; } = "(not found)";

    public static void Register()
    {
        lock (Gate)
        {
            if (_registered) return;
            _registered = true;

            BinDir = LocateBinDir(out var source);
            Source = source;
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            // stderr only — stdout is reserved for the MCP protocol.
            Console.Error.WriteLine(BinDir != null
                ? $"[tekla] resolving Tekla assemblies from: {BinDir} (via {Source})"
                : "[tekla] Tekla 'bin' not found. Start Tekla, or set TEKLA_BIN_DIR. " +
                  "Connection will fail until then.");
        }
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        if (string.IsNullOrEmpty(BinDir)) return null;

        var name = SafeName(args.Name);
        if (string.IsNullOrEmpty(name)) return null;

        try
        {
            // Supply any assembly (Tekla.* and its dependency closure) that exists in the Tekla
            // bin. The handler only fires when the normal load failed, so loading a same-named
            // DLL from the install is the intended behaviour.
            //
            // LoadFile, not LoadFrom: LoadFrom re-applies binding policy to the file's identity,
            // and App.config deliberately redirects Tekla.* to an unreachable version (to keep
            // the GAC out of the picture) — LoadFrom would chase that redirect and re-enter this
            // handler. LoadFile loads exactly the file, no policy, no probing.
            var path = Path.Combine(BinDir!, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFile(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeName(string fullName)
    {
        try { return new AssemblyName(fullName).Name; }
        catch { return null; }
    }

    private static string? LocateBinDir(out string source)
    {
        // 1) Explicit override — always wins.
        var env = Environment.GetEnvironmentVariable("TEKLA_BIN_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) { source = "env"; return env; }

        // 2) The RUNNING Tekla — guarantees a protocol match with the open instance.
        try
        {
            foreach (var p in Process.GetProcessesByName("TeklaStructures"))
            {
                try
                {
                    var file = p.MainModule?.FileName;
                    var dir = string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(dir) && HasTeklaModel(dir!)) { source = "process"; return dir; }
                }
                catch { /* access denied / bitness mismatch — try the next process */ }
            }
        }
        catch { /* ignore */ }

        // 3) Registry fallback: highest installed version.
        var reg = FromRegistry();
        if (reg != null) { source = "registry"; return reg; }

        source = "(not found)";
        return null;
    }

    private static string? FromRegistry()
    {
        // Best-effort: Tekla records install info under SOFTWARE\Tekla\Structures\<version>.
        // Value names vary across versions, so probe common ones and the \bin subfolder.
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Tekla\Structures");
                if (key == null) continue;

                var versions = key.GetSubKeyNames();
                Array.Sort(versions, StringComparer.OrdinalIgnoreCase);
                Array.Reverse(versions); // highest first

                foreach (var version in versions)
                {
                    using var vk = key.OpenSubKey(version);
                    if (vk == null) continue;
                    foreach (var valueName in new[] { "Bin directory", "BinDirectory", "InstallationDirectory", "" })
                    {
                        var dir = NormalizeBin(vk.GetValue(valueName) as string);
                        if (dir != null) return dir;
                    }
                }
            }
            catch { /* ignore and try the next root */ }
        }
        return null;
    }

    private static string? NormalizeBin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw!.Trim().Trim('"');
        if (!Directory.Exists(raw)) return null;
        if (HasTeklaModel(raw)) return raw;
        var bin = Path.Combine(raw, "bin");
        return HasTeklaModel(bin) ? bin : null;
    }

    private static bool HasTeklaModel(string dir) =>
        File.Exists(Path.Combine(dir, "Tekla.Structures.Model.dll"));
}
