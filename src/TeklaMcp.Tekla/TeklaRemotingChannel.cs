using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TS = Tekla.Structures;
using TSD = Tekla.Structures.Drawing;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla;

/// <summary>
/// Points the Open API clients at the remoting channels the running Tekla ACTUALLY publishes.
///
/// The Open API talks to Tekla over .NET remoting (IPC named pipes). Every Open API assembly
/// derives its channel name the same way (verified by decompiling the Tekla 2023 assemblies):
///
///     {AssemblyName}-{SESSIONNAME}:{AssemblyVersion}
///
/// where {SESSIONNAME} is the SESSIONNAME environment variable of the process that computes
/// the name (<c>TeklaStructuresInternal.Remoter.GetSessionName()</c>). Tekla's own process has
/// the Windows session variable (e.g. "Console", "RDP-Tcp#47"), but an MCP server launched by
/// an MCP client frequently has NO SESSIONNAME at all, so its client-side names come out as
/// "…-:version" and never match the published pipes (issue #7's mysterious "-Console" suffix).
///
/// THREE assemblies matter, each with its own Remoter and its own lazily-connected static
/// DelegateProxy:
///   - Tekla.Structures.dll — base. Hosts ModuleManager, whose static ctor connects over THIS
///     channel. Every Insert/Modify calls ModelModuleManager.CheckModules → ModuleManager, so
///     a misaligned base channel produces the deceptive "reads work, apply=true fails with
///     'The type initializer for Tekla.Structures.ModuleManager threw an exception'" pattern.
///   - Tekla.Structures.Model.dll — model reads and writes.
///   - Tekla.Structures.Drawing.dll — drawing tools.
///
/// A static proxy whose type initializer failed once is dead for the process lifetime (the
/// API's own doc: "Currently, there's no way to re-establish the connection"), so alignment
/// MUST happen before the first touch of each proxy.
///
/// <see cref="Align"/> runs before the first <c>Model()</c>: it derives the session suffix
/// from the published Tekla.Structures* pipes, sets SESSIONNAME for this process so every
/// Remoter computes the right name on its own, and belt-and-braces patches the internal
/// static <c>Remoter.ChannelName</c> fields of all three assemblies when they were already
/// initialized with a stale name. The result is cached — EXCEPT when no Tekla pipes were
/// found at all (server started before Tekla): then the next call probes again. Everything
/// here is best-effort: on any failure it logs to stderr and leaves the defaults.
///
/// Override: set <c>TEKLA_MCP_CHANNEL</c> to force an exact MODEL channel name (skips
/// probing); the session suffix embedded in it is applied to the other channels too.
/// </summary>
public static class TeklaRemotingChannel
{
    private const string PipePrefix = "Tekla.Structures";

    /// <summary>Longest name first, so "Tekla.Structures" only matches as a last resort.</summary>
    private static readonly string[] KnownAssemblyNames =
    {
        "Tekla.Structures.Drawing",
        "Tekla.Structures.Model",
        "Tekla.Structures",
    };

    private static readonly object Gate = new object();
    private static bool _done;
    private static bool _warmedUp;

    public static void Align()
    {
        lock (Gate)
        {
            if (_done) return;
            try
            {
                // False only while Tekla publishes no pipes — retry on the next call.
                _done = AlignCore();
            }
            catch (Exception ex)
            {
                _done = true; // structural failure — retrying would only spam stderr
                Console.Error.WriteLine(
                    "[tekla] remoting channel alignment failed (keeping defaults): " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Force-initializes the write-path proxies (ModuleManager and its base-channel
    /// DelegateProxy) once, right after the first successful model connection — i.e. at the
    /// only moment we KNOW the channels are aligned and Tekla is up. Without this the base
    /// proxy connects lazily inside the first Insert/Modify; if that ever happens with a
    /// stale channel the CLR caches the failure until the server restarts. Never throws:
    /// a failure is logged and Insert will report the same (now unwrapped) error.
    /// </summary>
    public static void WarmUpWriteProxies()
    {
        lock (Gate)
        {
            if (_warmedUp) return;
            _warmedUp = true;
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                    typeof(TS.ModuleManager).TypeHandle);
                Console.Error.WriteLine(
                    "[tekla] write-path proxies initialized (ModuleManager configuration: " +
                    TS.ModuleManager.Configuration + ").");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[tekla] WARNING: write-path proxy init failed — apply=true operations " +
                    "will fail until this is resolved: " + TeklaMcp.Core.ErrorText.Flatten(ex));
            }
        }
    }

    /// <summary>One-line connection diagnostics for "not connected" error messages.</summary>
    public static string Describe()
    {
        try
        {
            var asm = typeof(TSM.Model).Assembly;
            var channel = ReadChannel(asm, "Tekla.Structures.ModelInternal.Remoter") ?? "(unknown)";
            var baseChannel = ReadChannel(
                typeof(TS.TeklaStructuresInfo).Assembly,
                "Tekla.Structures.TeklaStructuresInternal.Remoter") ?? "(unknown)";
            var pipes = ListPublishedTeklaPipes();
            var origin = string.IsNullOrEmpty(asm.Location)
                ? $"(no file location; resolver bin: '{TeklaAssemblyResolver.BinDir ?? "?"}')"
                : $"'{asm.Location}'";
            return $"model channel '{channel}', base channel '{baseChannel}', " +
                   $"API {asm.GetName().Version} from {origin}, SESSIONNAME " +
                   $"'{Environment.GetEnvironmentVariable("SESSIONNAME") ?? "(not set)"}', " +
                   $"published Tekla pipes: [{string.Join(", ", pipes)}]";
        }
        catch (Exception ex)
        {
            return "diagnostics unavailable: " + ex.Message;
        }
    }

    /// <summary>Returns false when there was nothing to align to yet (no Tekla pipes) — retryable.</summary>
    private static bool AlignCore()
    {
        // 1) Determine the target session suffix WITHOUT touching any Tekla type: reading a
        //    Remoter field runs its type initializer, which snapshots SESSIONNAME — the env
        //    var must be corrected first.
        string? suffix;
        var forced = Environment.GetEnvironmentVariable("TEKLA_MCP_CHANNEL");
        if (!string.IsNullOrWhiteSpace(forced))
        {
            suffix = SessionSuffixOf(forced!.Trim());
            Console.Error.WriteLine(
                $"[tekla] TEKLA_MCP_CHANNEL forces the model channel to '{forced.Trim()}'" +
                (suffix is null ? " (no session suffix recognized in it)." : $" (session suffix '{suffix}')."));
        }
        else
        {
            var pipes = ListPublishedTeklaPipes();
            if (pipes.Count == 0) return false; // Tekla not running (or pipes unreadable) — retry later.
            suffix = DeriveSessionSuffix(pipes);
        }

        // 2) Make this process compute the same channel names Tekla's process did. This fixes
        //    all three assemblies at once, including proxies that have not initialized yet.
        var current = Environment.GetEnvironmentVariable("SESSIONNAME");
        if (suffix != null && !string.Equals(current, suffix, StringComparison.Ordinal))
        {
            Environment.SetEnvironmentVariable("SESSIONNAME", suffix);
            Console.Error.WriteLine(
                $"[tekla] SESSIONNAME set to '{suffix}' (was '{current ?? "(not set)"}') so the " +
                "Open API computes the channel names the running Tekla actually publishes.");
        }

        // 3) Belt and braces: a Remoter whose type initializer ALREADY ran holds the stale
        //    name in a static field — patch it directly (safe until the corresponding
        //    DelegateProxy connects, which is exactly what Align's placement guarantees).
        if (!string.IsNullOrWhiteSpace(forced))
            PatchChannel(typeof(TSM.Model).Assembly,
                "Tekla.Structures.ModelInternal.Remoter", forced!.Trim());
        else if (suffix != null)
        {
            PatchToSuffix(typeof(TS.TeklaStructuresInfo).Assembly,
                "Tekla.Structures.TeklaStructuresInternal.Remoter", suffix);
            PatchToSuffix(typeof(TSM.Model).Assembly,
                "Tekla.Structures.ModelInternal.Remoter", suffix);
            TryPatchDrawing(suffix);
        }
        return true;
    }

    /// <summary>
    /// Picks the session suffix from published pipe names like
    /// "Tekla.Structures.Model-Console:2023.0.0.0". Prefers pipes of the Tekla major version
    /// this build was compiled for, and the current SESSIONNAME when several Tekla sessions
    /// publish different suffixes.
    /// </summary>
    private static string? DeriveSessionSuffix(List<string> pipes)
    {
        var compiledMajor = TeklaAssemblyResolver.CompiledVersion?.Major;
        var candidates = new List<(string Suffix, int? Major)>();
        foreach (var pipe in pipes)
        {
            var colon = pipe.LastIndexOf(':');
            if (colon <= 0) continue;
            var name = pipe.Substring(0, colon);
            int? major = Version.TryParse(pipe.Substring(colon + 1), out var v) ? v.Major : (int?)null;
            foreach (var asm in KnownAssemblyNames)
            {
                if (!name.StartsWith(asm + "-", StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add((name.Substring(asm.Length + 1), major));
                break;
            }
        }
        if (candidates.Count == 0) return null;

        var preferred = candidates
            .Where(c => compiledMajor is null || c.Major == compiledMajor)
            .Select(c => c.Suffix)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (preferred.Count == 0)
            preferred = candidates.Select(c => c.Suffix).Distinct(StringComparer.Ordinal).ToList();

        if (preferred.Count == 1) return preferred[0];

        // Several Tekla sessions (e.g. multiple RDP users) — keep the current session's if it
        // is one of them, otherwise pick deterministically and say so.
        var current = Environment.GetEnvironmentVariable("SESSIONNAME");
        if (current != null && preferred.Contains(current)) return current;
        preferred.Sort(StringComparer.OrdinalIgnoreCase);
        Console.Error.WriteLine(
            $"[tekla] several Tekla sessions publish pipes ({string.Join(", ", preferred)}); " +
            $"using '{preferred[0]}'. Set TEKLA_MCP_CHANNEL to override.");
        return preferred[0];
    }

    /// <summary>Extracts "Console" from "Tekla.Structures.Model-Console:2023.0.0.0".</summary>
    private static string? SessionSuffixOf(string channel)
    {
        var colon = channel.LastIndexOf(':');
        var name = colon > 0 ? channel.Substring(0, colon) : channel;
        foreach (var asm in KnownAssemblyNames)
            if (name.StartsWith(asm + "-", StringComparison.OrdinalIgnoreCase))
                return name.Substring(asm.Length + 1);
        return null;
    }

    private static void PatchToSuffix(Assembly assembly, string remoterTypeName, string suffix)
    {
        var name = assembly.GetName();
        PatchChannel(assembly, remoterTypeName, $"{name.Name}-{suffix}:{name.Version}");
    }

    /// <summary>
    /// The Drawing assembly is loaded on demand; align it only when it can be resolved, and
    /// never let a missing/old Drawing DLL break model alignment.
    /// </summary>
    private static void TryPatchDrawing(string suffix)
    {
        try
        {
            PatchToSuffix(typeof(TSD.DrawingHandler).Assembly,
                "Tekla.Structures.DrawingInternal.Remoter", suffix);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[tekla] drawing channel alignment skipped: " + ex.Message);
        }
    }

    private static void PatchChannel(Assembly assembly, string remoterTypeName, string expected)
    {
        // internal static readonly string on all inspected versions (2021 baseline). Setting an
        // InitOnly static via reflection is supported on .NET Framework, which is the only TFM
        // this project builds for.
        var field = FindChannelNameField(assembly, remoterTypeName);
        if (field is null)
        {
            Console.Error.WriteLine(
                $"[tekla] {remoterTypeName}.ChannelName not found in this Tekla version; " +
                "using the default channel.");
            return;
        }

        var current = field.GetValue(null) as string ?? "";
        if (string.Equals(current, expected, StringComparison.Ordinal)) return;
        field.SetValue(null, expected);
        Console.Error.WriteLine(
            $"[tekla] {remoterTypeName}: channel '{current}' → '{expected}'.");
    }

    private static string? ReadChannel(Assembly assembly, string remoterTypeName)
        => FindChannelNameField(assembly, remoterTypeName)?.GetValue(null) as string;

    private static FieldInfo? FindChannelNameField(Assembly assembly, string remoterTypeName)
    {
        var remoter = assembly.GetType(remoterTypeName, throwOnError: false);
        return remoter?.GetField("ChannelName",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static List<string> ListPublishedTeklaPipes()
    {
        var result = new List<string>();
        try
        {
            // Named pipes are enumerable as files under \\.\pipe\. Some pipe names contain
            // characters that are invalid in paths and can make enumeration throw mid-way on
            // .NET Framework — treat the listing as best-effort and keep what we got.
            foreach (var path in Directory.EnumerateFiles(@"\\.\pipe\"))
            {
                var name = path.Substring(path.LastIndexOf('\\') + 1);
                if (name.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(name);
            }
        }
        catch
        {
            // best-effort
        }
        return result;
    }
}
