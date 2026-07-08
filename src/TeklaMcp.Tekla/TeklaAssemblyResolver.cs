using System;
using System.Collections.Generic;
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
    private static readonly Dictionary<string, Assembly> Cache =
        new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Loading =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            if (BinDir != null)
                PreloadCoreAssemblies();
        }
    }

    private static void PreloadCoreAssemblies()
    {
        // Warm the cache for the two assemblies every Open API call needs. Every Tekla.* bind
        // fails by design (App.config redirects them to the unreachable 2999.9.9.9 so the GAC
        // never wins — issue #7) and lands in AssemblyResolve; preloading keeps the first
        // Model() access to a single cache hit there (re-entrant resolve loops caused a
        // StackOverflow on live Tekla with the old LoadFile scheme — see cbe716b).
        foreach (var name in new[] { "Tekla.Structures", "Tekla.Structures.Model" })
            TryLoadFromBin(name);
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = SafeName(args.Name);
        if (name is null || name.Length == 0) return null;

        // AssemblyResolve can fire on any thread (tool calls, the script-execution thread);
        // Cache/Loading are plain collections, so serialize on the same gate Register uses.
        // Monitor is reentrant, so a same-thread recursive resolve does not deadlock.
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var cached))
                return cached;

            // A dependency bind can re-enter for the same simple name while the outer resolve
            // is still on the stack — return null to break infinite recursion.
            if (Loading.Contains(name))
                return null;

            // Tekla may have started AFTER this server (BinDir not found at Register time) —
            // re-probe on Tekla binds so "start server first, open Tekla later" recovers
            // without a restart. Gated on the name prefix to keep unrelated resolves (e.g.
            // *.resources) from re-scanning processes.
            if (BinDir is null && name.StartsWith("Tekla", StringComparison.OrdinalIgnoreCase))
            {
                BinDir = LocateBinDir(out var source);
                Source = source;
                if (BinDir != null)
                {
                    Console.Error.WriteLine($"[tekla] Tekla located after startup: {BinDir} (via {Source})");
                    PreloadCoreAssemblies();
                }
            }

            return BinDir is null ? null : TryLoadFromBin(name);
        }
    }

    /// <summary>Load one assembly from <see cref="BinDir"/>. Caller must hold <see cref="Gate"/>.</summary>
    private static Assembly? TryLoadFromBin(string name)
    {
        if (string.IsNullOrEmpty(BinDir)) return null;
        if (Cache.TryGetValue(name, out var cached))
            return cached;

        var path = Path.Combine(BinDir!, name + ".dll");
        if (!File.Exists(path))
            return null;

        Loading.Add(name);
        try
        {
            // Load(bytes), not LoadFile: LoadFile binds assemblies in a separate load context
            // where dependency binds re-entered AssemblyResolve until stack overflow on live
            // Tekla. Loading from bytes lands in the default context with the DLL's real
            // identity (e.g. 2023.0.0.0).
            //
            // Trade-off: byte-loaded assemblies have an EMPTY Assembly.Location. Anything that
            // needs the file on disk (e.g. Roslyn metadata references for tekla_run_csharp)
            // must go through BinDir paths instead — see TeklaModelService.ExecuteScript.
            var bytes = File.ReadAllBytes(path);
            var asm = Assembly.Load(bytes);
            Cache[name] = asm;
            return asm;
        }
        catch
        {
            return null;
        }
        finally
        {
            Loading.Remove(name);
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
