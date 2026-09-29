using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TeklaMcp.Core;
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
///
/// All of the above is 2021–2023 only. Tekla 2024+ runs on Trimble.Remoting (no named pipes)
/// and names its channels itself — <c>{Assembly}-{ProductName}-{SESSIONNAME|Console}:{FileVersion}</c>.
/// Its builds are never aligned: next to a running older Tekla the fix-up used to write that
/// Tekla's 2021–2023-style names into the 2024+ Remoters and poison the proxies. Only an explicit
/// <c>TEKLA_MCP_CHANNEL</c> is applied there (see <c>AlignTrimbleRemoting</c>).
/// </summary>
public static class TeklaRemotingChannel
{
    private const string PipePrefix = "Tekla.Structures";

    private static readonly object Gate = new object();
    private static bool _done;
    private static bool _warmedUp;
    private static bool _unavailableLogged;

    public static void Align()
    {
        lock (Gate)
        {
            if (_done) return;
            try
            {
                // Tekla 2024+ names its channels itself. Decided HERE, before AlignCore is even
                // JIT-compiled: compiling a method that merely mentions a Tekla type loads that
                // assembly (verified on .NET Framework 4.8). AlignCore returns false while this
                // build's Tekla publishes no pipes yet — retry on the next call.
                var compiledMajor = TeklaAssemblyResolver.CompiledVersion?.Major;
                _done = RemotingChannelNames.NamesItsOwnChannels(compiledMajor)
                    ? AlignTrimbleRemoting(compiledMajor.GetValueOrDefault())
                    : AlignCore();
            }
            catch (Exception ex) when (IsTeklaApiUnavailable(ex))
            {
                // The Open API assemblies cannot be loaded YET (no or a wrong-version Open API
                // folder when the server started). The resolver re-probes after a version
                // mismatch, so a later call can succeed — giving up here used to leave the
                // channels unaligned for the process lifetime.
                if (!_unavailableLogged)
                {
                    _unavailableLogged = true;
                    Console.Error.WriteLine(
                        "[tekla] remoting channel alignment postponed — the Tekla Open API cannot be " +
                        "loaded yet: " + ErrorText.Flatten(ex));
                }
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

    /// <summary>
    /// Turns a failed connection attempt into a cause and an action. Both failure modes below
    /// are cached by the Open API for the life of this process (see the class remarks), so the
    /// honest instruction is "restart the MCP server" — retrying the tool cannot help.
    /// </summary>
    public static string DiagnoseConnectionFailure(Exception ex)
    {
        string cause;
        if (HasInChain<TypeInitializationException>(ex))
        {
            cause = "The Open API connection failed to initialize — typically this server touched " +
                    "Tekla before Tekla published its channel (server started first, or Tekla was " +
                    "closed at that moment). The Open API caches that failure for the life of the " +
                    "server process. Open the model in Tekla, then restart this MCP server " +
                    "(reconnect it in the MCP client).";
        }
        else if (HasInChain<System.Runtime.Remoting.RemotingException>(ex) ||
                 HasInChain<System.Net.Sockets.SocketException>(ex) ||
                 HasInChain<System.IO.IOException>(ex))
        {
            cause = "The server was connected earlier, but the Tekla process behind that connection " +
                    "is gone (Tekla was restarted or closed). The Open API cannot re-establish the " +
                    "connection inside this process. Restart this MCP server (reconnect it in the " +
                    "MCP client); restarting Tekla again will not help.";
        }
        else
        {
            cause = "Connection failed.";
        }

        return cause + " Error: " + TeklaMcp.Core.ErrorText.Flatten(ex) +
               " | Tekla processes: " + DescribeTeklaProcesses() +
               " | " + Describe() + PipeListingCaveat();
    }

    private static bool HasInChain<T>(Exception? ex) where T : Exception
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is T) return true;
        return false;
    }

    private static string DescribeTeklaProcesses()
    {
        var processes = ListTeklaProcesses();
        return processes.Count == 0 ? "none running" : string.Join(", ", processes);
    }

    /// <summary>
    /// Running TeklaStructures processes as "PID n started …". PID + start time lets the user
    /// match a failure to a Tekla restart, or spot a second Tekla of the same year.
    /// </summary>
    public static List<string> ListTeklaProcesses()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("TeklaStructures")
                .Select(p =>
                {
                    try { return $"PID {p.Id} started {p.StartTime:yyyy-MM-dd HH:mm:ss}"; }
                    catch { return $"PID {p.Id}"; }
                    finally { p.Dispose(); }
                })
                .ToList();
        }
        catch (Exception e)
        {
            return new List<string> { "unavailable (" + e.Message + ")" };
        }
    }

    /// <summary>
    /// Tekla 2024+ moved from IPC named pipes to Trimble.Remoting (shared memory + named kernel
    /// objects; reported from decompiled 2024–2026 assemblies, not yet live-verified here), so an
    /// empty pipe list there is not evidence that Tekla is down.
    /// </summary>
    private static string PipeListingCaveat()
    {
        var major = TeklaAssemblyResolver.CompiledVersion?.Major;
        return major >= 2024
            ? " (this is a Tekla " + major + " build: its remoting does not use named pipes, so an " +
              "empty pipe list is expected and says nothing about Tekla's state)"
            : "";
    }

    /// <summary>Returns false when there was nothing to align to yet (no Tekla pipes) — retryable.</summary>
    private static bool AlignCore()
    {
        var compiledMajor = TeklaAssemblyResolver.CompiledVersion?.Major;
        var forced = Environment.GetEnvironmentVariable("TEKLA_MCP_CHANNEL");

        // 1) Determine the target session suffix WITHOUT touching any Tekla type: reading a
        //    Remoter field runs its type initializer, which snapshots SESSIONNAME — the env
        //    var must be corrected first.
        string? suffix;
        if (!string.IsNullOrWhiteSpace(forced))
        {
            suffix = RemotingChannelNames.SessionSuffixOf(forced!.Trim());
            Console.Error.WriteLine(
                $"[tekla] TEKLA_MCP_CHANNEL forces the model channel to '{forced.Trim()}'" +
                (suffix is null ? " (no session suffix recognized in it)." : $" (session suffix '{suffix}')."));
        }
        else
        {
            var pipes = ListPublishedTeklaPipes();
            if (pipes.Count == 0) return false; // Tekla not running (or pipes unreadable) — retry later.
            suffix = RemotingChannelNames.DeriveSessionSuffix(
                pipes, compiledMajor, Environment.GetEnvironmentVariable("SESSIONNAME"), out var note);
            if (note != null) Console.Error.WriteLine("[tekla] " + note);
            // Only other Tekla versions publish. Aligning to THEIR session used to end alignment for
            // the process, so a later start of this build's Tekla in another session never
            // connected — wait for this version's own pipes instead.
            if (suffix is null) return false;
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
    /// Tekla 2024+: leave the Open API's own channel names alone — it already falls back to
    /// "Console" when SESSIONNAME is unset. Only an explicit TEKLA_MCP_CHANNEL (the exact Model
    /// channel) is applied; the base and Drawing channels get the same name with their own
    /// assembly prefix. SESSIONNAME is not touched: 2024+ names carry a product segment that
    /// the 2021–2023 suffix logic would fold into the session. From decompiled 2024–2026
    /// assemblies — TODO(windows): verify on a live 2024+ install. No Tekla type is mentioned
    /// in this method, so the skip path loads no Tekla assembly.
    /// </summary>
    private static bool AlignTrimbleRemoting(int major)
    {
        var forced = Environment.GetEnvironmentVariable("TEKLA_MCP_CHANNEL");
        if (string.IsNullOrWhiteSpace(forced))
        {
            Console.Error.WriteLine(
                $"[tekla] Tekla {major} build: channel alignment skipped — Tekla " +
                $"{RemotingChannelNames.FirstTrimbleRemotingMajor}+ names its Trimble.Remoting channels itself.");
            return true;
        }

        var model = forced!.Trim();
        Console.Error.WriteLine(
            $"[tekla] TEKLA_MCP_CHANNEL forces the model channel to '{model}' (Tekla {major}: base and " +
            "drawing channels follow by assembly name; SESSIONNAME left alone).");
        ForceChannelsByAssemblyName(model);
        return true;
    }

    /// <summary>The TEKLA_MCP_CHANNEL part of <see cref="AlignTrimbleRemoting"/>.</summary>
    private static void ForceChannelsByAssemblyName(string model)
    {
        PatchChannel(typeof(TSM.Model).Assembly, "Tekla.Structures.ModelInternal.Remoter", model);

        var baseChannel = RemotingChannelNames.WithAssembly(model, "Tekla.Structures.Model", "Tekla.Structures");
        if (baseChannel != null)
            PatchChannel(typeof(TS.TeklaStructuresInfo).Assembly,
                "Tekla.Structures.TeklaStructuresInternal.Remoter", baseChannel);

        var drawingChannel = RemotingChannelNames.WithAssembly(model, "Tekla.Structures.Model", "Tekla.Structures.Drawing");
        if (drawingChannel != null)
            TryPatchDrawingChannel(drawingChannel);
    }

    /// <summary>
    /// True when the failure means the Tekla Open API assemblies cannot be loaded (yet): no or a
    /// wrong-version Open API folder. Retryable — see <see cref="Align"/>.
    /// </summary>
    private static bool IsTeklaApiUnavailable(Exception ex) =>
        HasInChain<FileNotFoundException>(ex) ||
        HasInChain<FileLoadException>(ex) ||
        HasInChain<BadImageFormatException>(ex) ||
        HasInChain<TeklaVersionMismatchException>(ex);

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

    /// <summary>Same as <see cref="TryPatchDrawing"/> with an exact channel name.</summary>
    private static void TryPatchDrawingChannel(string channel)
    {
        try
        {
            PatchChannel(typeof(TSD.DrawingHandler).Assembly,
                "Tekla.Structures.DrawingInternal.Remoter", channel);
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
