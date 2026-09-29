using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using TeklaMcp.Core;

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
    private static bool _envIgnoredLogged;
    private static bool _envSubfolderLogged;
    private static Version? _installedVersion;

    /// <summary>
    /// The Tekla Open API folder assemblies are resolved from (the folder holding
    /// Tekla.Structures.Model.dll — <c>bin</c> on 2023+, <c>nt\bin\plugins</c> on 2021), or null
    /// if not found.
    /// </summary>
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

    // Caller holds Gate (Register / OnAssemblyResolve), which also guards the log-once flags.
    private static string? LocateBinDir(out string source)
    {
        // 1) Explicit override — wins when it really holds the Open API. Its API subfolder is
        //    accepted too: Tekla 2021 keeps the API in nt\bin\plugins, and nt\bin itself holds
        //    seven unrelated Tekla.Structures.*.dll. That folder used to be taken as-is: the
        //    Model then bound from the GAC (so connecting worked), the version check had nothing
        //    to read, and every script compiled without Tekla.Structures.Model (DEV-005).
        var env = Environment.GetEnvironmentVariable("TEKLA_BIN_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var dir = TeklaBinLayout.FindApiDirectory(env, File.Exists);
            if (dir != null)
            {
                if (!SamePath(dir, env!))
                    LogOnce(ref _envSubfolderLogged,
                        $"[tekla] TEKLA_BIN_DIR='{env}' holds no Open API itself; using its subfolder '{dir}'.");
                source = "env";
                return dir;
            }
            LogOnce(ref _envIgnoredLogged,
                $"[tekla] WARNING: TEKLA_BIN_DIR='{env}' contains no {TeklaBinLayout.ModelAssemblyFile} " +
                "(its plugins/bin subfolders were checked too) — ignoring it and probing the running Tekla.");
        }

        // 2) The RUNNING Tekla, then 3) the registry. Prefer an Open API whose major version is the
        //    one this build was compiled for: with Tekla 2021 and 2023 open side by side the first
        //    process found used to win, and the 2021 build then failed as "wrong build".
        var candidates = new List<KeyValuePair<string, string>>(); // folder → how it was found
        foreach (var dir in FromProcesses())
            candidates.Add(new KeyValuePair<string, string>(dir, "process"));
        var reg = FromRegistry();
        if (reg != null)
            candidates.Add(new KeyValuePair<string, string>(reg, "registry"));

        foreach (var candidate in candidates)
        {
            if (!MatchesCompiledMajor(candidate.Key)) continue;
            source = candidate.Value;
            return candidate.Key;
        }
        if (candidates.Count > 0)
        {
            // Only other Tekla versions are around: return one anyway so EnsureVersionMatch can
            // say "wrong build for this Tekla version" instead of "Tekla not found".
            source = candidates[0].Value;
            return candidates[0].Key;
        }

        source = "(not found)";
        return null;
    }

    /// <summary>Open API folders of the running TeklaStructures processes (each at most once).</summary>
    private static List<string> FromProcesses()
    {
        var result = new List<string>();
        try
        {
            foreach (var p in Process.GetProcessesByName("TeklaStructures"))
            {
                try
                {
                    var file = p.MainModule?.FileName;
                    var dir = TeklaBinLayout.FindApiDirectory(
                        string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(file), File.Exists);
                    if (dir != null && !result.Contains(dir, StringComparer.OrdinalIgnoreCase))
                        result.Add(dir);
                }
                catch { /* access denied / bitness mismatch — try the next process */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* ignore */ }
        return result;
    }

    /// <summary>True when the folder's Tekla.Structures.Model.dll has this build's major version.</summary>
    private static bool MatchesCompiledMajor(string dir)
    {
        if (CompiledVersion is null) return true;
        try
        {
            var version = AssemblyName.GetAssemblyName(
                Path.Combine(dir, TeklaBinLayout.ModelAssemblyFile)).Version;
            return version != null && version.Major == CompiledVersion.Major;
        }
        catch
        {
            return false; // unreadable/locked — prefer another candidate
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim().Trim('"')).TrimEnd('\\', '/'),
                Path.GetFullPath(b.Trim().Trim('"')).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void LogOnce(ref bool logged, string message)
    {
        if (logged) return;
        logged = true;
        Console.Error.WriteLine(message); // stderr only — stdout is reserved for the MCP protocol
    }

    private static string? FromRegistry()
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            var dir = FromTrimbleKey(root) ?? FromLegacyTeklaKey(root);
            if (dir != null) return dir;
        }
        return null;
    }

    /// <summary>
    /// Current installs register under SOFTWARE\Trimble\Tekla Structures\&lt;version&gt;\setup with
    /// MainDir (e.g. C:\TeklaStructures\) + TSVersionDir (e.g. 2021.0) — verified on 2021 and 2023.
    /// The legacy SOFTWARE\Tekla\Structures key does not exist there, so the registry fallback
    /// found nothing and a 2021 build with only Tekla 2023 running reported "wrong build".
    /// </summary>
    private static string? FromTrimbleKey(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Trimble\Tekla Structures");
            if (key == null) return null;
            foreach (var version in PreferCompiledYear(key.GetSubKeyNames()))
            {
                using var setup = key.OpenSubKey(version + @"\setup");
                var mainDir = (setup?.GetValue("MainDir") as string)?.Trim().Trim('"');
                if (string.IsNullOrEmpty(mainDir)) continue; // e.g. HKCU "Ifc", "License"
                var versionDir = (setup!.GetValue("TSVersionDir") as string)?.Trim();
                var dir = NormalizeBin(Path.Combine(mainDir!, string.IsNullOrEmpty(versionDir) ? version : versionDir!));
                if (dir != null) return dir;
            }
        }
        catch { /* ignore — best effort */ }
        return null;
    }

    /// <summary>Older layout: SOFTWARE\Tekla\Structures\&lt;version&gt; with version-specific value names.</summary>
    private static string? FromLegacyTeklaKey(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Tekla\Structures");
            if (key == null) return null;
            foreach (var version in PreferCompiledYear(key.GetSubKeyNames()))
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
        catch { /* ignore — best effort */ }
        return null;
    }

    /// <summary>
    /// Several Teklas can be installed side by side — try the one this build was compiled for
    /// first, then the highest.
    /// </summary>
    private static string[] PreferCompiledYear(string[] versions)
    {
        var compiledYear = CompiledVersion?.Major.ToString() ?? "";
        return versions
            .OrderByDescending(v => compiledYear.Length > 0 && v.StartsWith(compiledYear, StringComparison.Ordinal))
            .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeBin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw!.Trim().Trim('"');
        // Registry values point at an install root, a bin folder or (2021) nt\bin — all resolve
        // to the folder that actually holds the Open API.
        return Directory.Exists(raw) ? TeklaBinLayout.FindApiDirectory(raw, File.Exists) : null;
    }
}
