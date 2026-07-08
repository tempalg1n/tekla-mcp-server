using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace TeklaMcp.Tekla;

/// <summary>
/// Supplies the Tekla Open API assemblies for a PER-VERSION build (issue #11).
///
/// Each release artifact is compiled against ONE Tekla version (<c>-p:TeklaVersion</c>, see
/// TeklaMcp.Tekla.csproj) and does not ship the Tekla DLLs. At runtime this resolver locates
/// the installed/running Tekla's <c>bin</c>, verifies its MAJOR version matches the version
/// this build was compiled for, and <see cref="Assembly.LoadFrom(string)"/>s the DLLs from
/// there. On a mismatch every Tekla operation fails fast with a "wrong build for this Tekla
/// version" message instead of speaking the wrong remoting protocol.
///
/// No byte-loading and no bindingRedirect tricks — the GAC cannot hijack a per-version build
/// because a strong-named bind is only satisfied by the exact compiled version (a same-version
/// GAC copy is protocol-compatible by definition). See App.config for why the old anti-GAC
/// redirects must never come back.
///
/// <see cref="Register"/> must be called once at startup, BEFORE any Tekla type is touched.
/// </summary>
public static class TeklaAssemblyResolver
{
    private static readonly object Gate = new object();
    private static bool _registered;
    private static bool _mismatchLogged;
    private static Version? _installedVersion;

    /// <summary>The Tekla 'bin' directory assemblies are resolved from, or null if not found.</summary>
    public static string? BinDir { get; private set; }

    /// <summary>How <see cref="BinDir"/> was located: "env" | "process" | "registry" | "(not found)".</summary>
    public static string Source { get; private set; } = "(not found)";

    /// <summary>
    /// The Tekla.Structures.Model version this build was COMPILED against (the build-time
    /// <c>TeklaVersion</c>), read from this assembly's references — its Major is the Tekla year.
    /// </summary>
    public static readonly Version? CompiledVersion = FindCompiledVersion();

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
            Console.Error.WriteLine(
                $"[tekla] this build is for Tekla {CompiledVersion?.Major.ToString() ?? "?"}; " +
                (BinDir != null
                    ? $"resolving Tekla assemblies from: {BinDir} (via {Source})"
                    : "Tekla 'bin' not found. Start Tekla, or set TEKLA_BIN_DIR. " +
                      "Connection will fail until then."));

            // Surface a version mismatch at startup already (logged once); tool calls will
            // keep failing fast with the same message via EnsureVersionMatch.
            try { EnsureVersionMatch(); }
            catch (InvalidOperationException) { /* logged inside */ }
        }
    }

    /// <summary>
    /// Throws when the installed Tekla's major version differs from the one this build was
    /// compiled for — the Open API remoting protocol is version-locked, so proceeding would
    /// only produce cryptic remoting failures. No-op while Tekla has not been located yet.
    /// Called both from the resolver and from <c>EnsureTeklaReady</c>, so the clear message
    /// also covers binds satisfied without the resolver (e.g. a same-version GAC copy).
    /// </summary>
    public static void EnsureVersionMatch()
    {
        lock (Gate)
        {
            var installed = InstalledVersion();
            if (installed is null || CompiledVersion is null) return;
            if (installed.Major == CompiledVersion.Major) return;

            var msg =
                $"Wrong build for this Tekla version: this TeklaMcp.Server build is for " +
                $"Tekla {CompiledVersion.Major}, but the installed/running Tekla is " +
                $"{installed.Major} ({BinDir}). Download the TeklaMcp.Server " +
                $"…-tekla{installed.Major}.zip from the project's GitHub Releases page " +
                $"(or rebuild with -p:TeklaVersion={installed.Major}.x).";
            if (!_mismatchLogged)
            {
                _mismatchLogged = true;
                Console.Error.WriteLine("[tekla] " + msg);
            }
            throw new InvalidOperationException(msg);
        }
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = SafeName(args.Name);
        if (name is null || !name.StartsWith("Tekla", StringComparison.OrdinalIgnoreCase))
            return null;

        // Fires on any thread (tool calls, the script-execution thread) — serialize on the
        // same gate Register uses. Monitor is reentrant, so a recursive resolve on the same
        // thread cannot deadlock.
        lock (Gate)
        {
            // Tekla may have started AFTER this server (BinDir not found at Register time) —
            // re-probe on Tekla binds so "start server first, open Tekla later" recovers
            // without a restart.
            if (BinDir is null)
            {
                BinDir = LocateBinDir(out var source);
                Source = source;
                if (BinDir != null)
                    Console.Error.WriteLine($"[tekla] Tekla located after startup: {BinDir} (via {Source})");
            }
            if (BinDir is null) return null;

            var path = Path.Combine(BinDir, name + ".dll");
            if (!File.Exists(path)) return null;

            // Never hand the runtime a wrong-version protocol assembly — fail fast instead.
            EnsureVersionMatch();

            // LoadFrom (not LoadFile, not Load(bytes)): Assembly.Location stays real, LoadFrom
            // caches by path, and the LoadFrom context resolves the DLL's own dependencies
            // from the same directory without re-entering this handler.
            return Assembly.LoadFrom(path);
        }
    }

    /// <summary>Version of Tekla.Structures.Model.dll in <see cref="BinDir"/>, probed once. Caller must hold <see cref="Gate"/>.</summary>
    private static Version? InstalledVersion()
    {
        if (_installedVersion != null) return _installedVersion;
        if (BinDir is null) return null;
        try
        {
            _installedVersion = AssemblyName
                .GetAssemblyName(Path.Combine(BinDir, "Tekla.Structures.Model.dll")).Version;
        }
        catch
        {
            // unreadable/locked DLL — leave null and retry on the next call
        }
        return _installedVersion;
    }

    private static Version? FindCompiledVersion()
    {
        // TeklaMcp.Tekla always references Tekla.Structures.Model; the reference carries the
        // exact version the build compiled against. No Tekla assembly is loaded by this.
        foreach (var reference in typeof(TeklaAssemblyResolver).Assembly.GetReferencedAssemblies())
        {
            if (string.Equals(reference.Name, "Tekla.Structures.Model", StringComparison.OrdinalIgnoreCase))
                return reference.Version;
        }
        return null;
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

        // 2) The RUNNING Tekla — guarantees we check against the open instance.
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

        // 3) Registry fallback.
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

                // A machine can have several Teklas installed side by side — prefer the one
                // this build was compiled for, then fall back to the highest.
                var compiledYear = CompiledVersion?.Major.ToString() ?? "";
                var versions = key.GetSubKeyNames()
                    .OrderByDescending(v => v.StartsWith(compiledYear, StringComparison.Ordinal))
                    .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

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
