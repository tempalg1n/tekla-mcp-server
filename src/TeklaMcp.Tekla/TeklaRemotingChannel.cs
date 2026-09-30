using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
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
    private const string ModelRemoter = "Tekla.Structures.ModelInternal.Remoter";
    private const string DrawingRemoter = "Tekla.Structures.DrawingInternal.Remoter";

    private static readonly object Gate = new object();
    private static bool _done;
    private static bool _warmedUp;
    private static bool _unavailableLogged;
    private static bool _incompleteListingLogged;
    private static readonly HashSet<TeklaChannel> Touched = new HashSet<TeklaChannel>();

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
    /// The poisoning guard (backlog §1.1): call right before the first Open API object of
    /// <paramref name="which"/> is created. A proxy created while its channel is not published is
    /// dead for the process — on Tekla 2021 (verified 2026-09-30) the Open API prints
    /// "Connection failed : … RemotingException" and leaves the delegate null, so
    /// <c>GetConnectionStatus()</c> answers false forever, even after Tekla starts. Throws
    /// <see cref="TeklaChannelUnavailableException"/> instead of letting the caller dial a pipe
    /// that does not exist; nothing Tekla-side is touched, so the next call can still succeed.
    ///
    /// Only blocks on evidence: 2024+ builds (no pipes), an incomplete pipe listing or an unknown
    /// channel name pass as before. While no proxy has dialed yet, a channel published under
    /// another session/instance suffix triggers a fresh alignment first.
    /// </summary>
    public static void EnsurePublished(TeklaChannel which)
    {
        lock (Gate)
        {
            var state = CheckChannel(which, out var channel, out var pipes);
            var forced = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEKLA_MCP_CHANNEL"));
            if (state == ChannelPublication.PublishedUnderOtherName && Touched.Count == 0 && !forced)
            {
                // Nothing has dialed, so the names can still change: the Tekla seen at startup is
                // gone and this version publishes under another suffix (another Windows session,
                // or a surviving second instance's Console-<PID>).
                Console.Error.WriteLine(
                    $"[tekla] channel '{channel}' is not published but this Tekla version publishes " +
                    "under another name — aligning again before the first connection.");
                _done = false;
                Align();
                state = CheckChannel(which, out channel, out pipes);
            }

            if (state == ChannelPublication.Published || state == ChannelPublication.Unknown)
            {
                Touched.Add(which); // the caller creates the proxy right after this returns
                return;
            }

            throw new TeklaChannelUnavailableException(
                UnavailableMessage(state, channel ?? "(unknown)", pipes, Touched.Contains(which), forced));
        }
    }

    /// <summary>The Open API clients that can be recreated, Model first: it is the one every tool needs.</summary>
    private static readonly (string Assembly, string Namespace)[] ReconnectableClients =
    {
        ("Tekla.Structures.Model", "Tekla.Structures.ModelInternal"),
        ("Tekla.Structures", "Tekla.Structures.TeklaStructuresInternal"),
        ("Tekla.Structures.Drawing", "Tekla.Structures.DrawingInternal"),
        ("Tekla.Structures.Catalogs", "Tekla.Structures.CatalogInternal"),
    };

    /// <summary>When and why the clients were last recreated in this process; null if never.</summary>
    public static string? LastReconnect { get; private set; }

    /// <summary>In-process reconnect is implemented for the 2021–2023 named-pipe transport only.</summary>
    public static bool SupportsReconnect =>
        !RemotingChannelNames.NamesItsOwnChannels(TeklaAssemblyResolver.CompiledVersion?.Major);

    /// <summary>
    /// Appended to the 2024+ connection messages. The maintainers have no 2024+ install: everything
    /// connection-related there (Trimble.Remoting, backlog §3) comes from decompiled assemblies, so
    /// field reports from users of those versions are the only way it gets verified and extended.
    /// </summary>
    private const string UntestedVersionNote =
        " Tekla 2024+ is not tested by the maintainers (no install available): please tell the user that a report " +
        "is very welcome — tekla_report_gap drafts a GitHub issue with what happened; include the " +
        "tekla_get_connection_info output.";

    /// <summary>
    /// Backlog §1: replaces the Open API clients of this process with fresh ones on the SAME channel
    /// names, after Tekla was restarted (a stale client: <c>GetConnectionStatus()</c> still true, every
    /// call a <c>RemotingException</c>) or when a client was created while Tekla was down (2021: a null
    /// delegate). Every client is a static <c>{Asm}Internal.DelegateProxy</c> around a
    /// <c>GenericDelegateProxy</c>; a new one comes from the public
    /// <c>RemotingProxyHelper.CreateInstance&lt;CDelegate&gt;("ipc://" + channel)</c> and goes in through the
    /// <c>DelegateProxy.Delegate</c> setter — identical in 2021 and 2023 (checked by reflection on both
    /// installs; the swap itself verified on a live Tekla 2023 without a restart, model/drawing/catalog
    /// clients keep working). Only assemblies already loaded are touched; a failed attempt
    /// leaves the old client in place, so the next call can try again (Tekla may still be starting).
    /// Never switches to a channel with another name — that may be a different Tekla (§2).
    /// A client whose type initializer failed cannot be recreated; that still needs a server restart.
    /// TODO(windows): acceptance = three real restarts each of Tekla 2021 and 2023.
    /// </summary>
    public static bool TryReconnect(string reason, out string message)
    {
        lock (Gate)
        {
            if (!SupportsReconnect)
            {
                message = "Reconnecting inside the server is implemented for Tekla 2021–2023 only; restart this " +
                          "MCP server (reconnect it in the MCP client)." + UntestedVersionNote;
                return false;
            }

            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            var renewed = new List<string>();
            foreach (var (assemblyName, ns) in ReconnectableClients)
            {
                var assembly = loaded.FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal));
                if (assembly is null) continue; // never used — nothing stale to replace
                try
                {
                    RecreateClient(assembly, ns);
                    renewed.Add(assemblyName);
                }
                catch (Exception ex)
                {
                    var flat = ErrorText.Flatten(ex);
                    if (assemblyName != "Tekla.Structures.Model")
                    {
                        Console.Error.WriteLine($"[tekla] reconnect: {assemblyName} client not renewed: {flat}");
                        continue;
                    }
                    message = HasInChain<TypeInitializationException>(ex)
                        ? "The Open API client failed to initialize earlier in this process and cannot be recreated — " +
                          "restart this MCP server (reconnect it in the MCP client). Error: " + flat
                        : "Could not reconnect to Tekla (" + reason + "): " + flat + ". If Tekla is still starting or " +
                          "no model is open yet, call again in a moment.";
                    Console.Error.WriteLine("[tekla] reconnect failed: " + message);
                    return false;
                }
            }

            LastReconnect = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} reconnected ({reason}): {string.Join(", ", renewed)}";
            Console.Error.WriteLine("[tekla] " + LastReconnect);
            message = LastReconnect;
            return true;
        }
    }

    private static void RecreateClient(Assembly assembly, string ns)
    {
        var channel = ReadChannel(assembly, ns + ".Remoter");
        if (string.IsNullOrEmpty(channel))
            throw new InvalidOperationException(ns + ".Remoter.ChannelName not found in this Tekla version.");
        var cdelegate = assembly.GetType(ns + ".CDelegate", throwOnError: true)!;

        // Looked up by name: the helper is public in 2021–2023, and this path never runs on 2024+.
        var helper = typeof(TS.TeklaStructuresInfo).Assembly.GetType("Tekla.Structures.Internal.RemotingProxyHelper", throwOnError: true)!;
        var create = helper.GetMethod("CreateInstance", BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(cdelegate);

        object? fresh = null;
        Exception? last = null;
        // The IPC client may still hold a connection to the dead pipe; the first activation can fail on
        // it and drop it, the second then dials the new pipe.
        for (var attempt = 0; attempt < 2 && fresh is null; attempt++)
        {
            try { fresh = create.Invoke(null, new object[] { "ipc://" + channel }); }
            catch (TargetInvocationException ex) { last = ex.InnerException ?? ex; }
        }
        if (fresh is null)
            throw new InvalidOperationException("Could not create a client on channel '" + channel + "'.", last);

        // Through DelegateProxy.Delegate itself, NOT the public CDelegateSetter.SetInstanceForUnitTesting:
        // verified on live Tekla 2023 (2026-09-30) that the latter leaves the process in a state where
        // the first CatalogHandler.GetMaterialItems() throws NullReferenceException inside Tekla; the
        // property setter does not.
        var property = assembly.GetType(ns + ".DelegateProxy", throwOnError: true)!
            .GetProperty("Delegate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(ns + ".DelegateProxy.Delegate not found.");
        property.SetValue(null, fresh);
    }

    /// <summary>
    /// "Not connected" text for a proxy that answers <c>GetConnectionStatus() == false</c>. When
    /// the channel IS published, the proxy was created while it was not (before this guard, or on
    /// a fail-open) and only a server restart helps — say so instead of "is Tekla running?".
    /// </summary>
    public static string NotConnectedMessage(TeklaChannel which)
    {
        ChannelPublication state;
        lock (Gate) state = CheckChannel(which, out _, out _);
        var api = which == TeklaChannel.Drawing ? "Drawing API" : "Open API";
        var text = state == ChannelPublication.Published
            ? $"Not connected although Tekla publishes the {which.ToString().ToLowerInvariant()} channel: this " +
              $"server's {api} client was most likely created while Tekla was not running or still starting, " +
              "and the Open API never retries that inside a process. If a model is open in Tekla, restart " +
              "this MCP server (reconnect it in the MCP client)."
            : $"The {api} is not connected. Is Tekla Structures running with a model open?";
        return text + " (" + Describe() + ")";
    }

    /// <summary>
    /// Fills <see cref="WriteTarget.TeklaPid"/> / <see cref="WriteTarget.TeklaStartedAt"/> with the
    /// Tekla process behind this server's model channel, when the evidence names exactly one
    /// (see <see cref="RemotingChannelNames.ResolveInstancePid"/>); otherwise leaves the PID null
    /// and says why in <see cref="WriteTarget.Note"/>. Candidates are running TeklaStructures
    /// processes of this build's version (the exe's file major version is the Tekla year —
    /// 2021.0.12696.0, 2023.0.29842.0); one whose version cannot be read stays a candidate, so an
    /// unreadable process makes the answer ambiguous rather than wrong. Never throws.
    /// </summary>
    public static void IdentifyInstance(WriteTarget target)
    {
        try
        {
            var major = TeklaAssemblyResolver.CompiledVersion?.Major;
            var starts = new Dictionary<int, DateTime?>();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("TeklaStructures"))
            {
                using (process)
                {
                    int? fileMajor = null;
                    try { fileMajor = process.MainModule?.FileVersionInfo.FileMajorPart; } catch { }
                    if (major is int m && fileMajor is int f && f != m) continue;
                    DateTime? started = null;
                    try { started = process.StartTime; } catch { }
                    starts[process.Id] = started;
                }
            }

            string? channel;
            List<string> pipes;
            lock (Gate)
            {
                try { channel = ReadModelChannel(); } catch { channel = null; }
                pipes = ListPublishedTeklaPipes();
            }

            var pid = RemotingChannelNames.ResolveInstancePid(channel, pipes, starts.Keys.ToList(), out var note);
            target.TeklaPid = pid;
            if (pid is int found && starts.TryGetValue(found, out var start) && start is DateTime s)
                target.TeklaStartedAt = s.ToString("yyyy-MM-dd HH:mm:ss");
            if (note != null) target.Note = "Tekla process not identified: " + note + ".";
        }
        catch (Exception ex)
        {
            target.Note = "Tekla process not identified: " + ex.Message;
        }
    }

    /// <summary>Guard evidence; never throws (a failure is <see cref="ChannelPublication.Unknown"/>).</summary>
    private static ChannelPublication CheckChannel(
        TeklaChannel which, out string? channel, out List<string> pipes)
    {
        channel = null;
        pipes = new List<string>();
        var major = TeklaAssemblyResolver.CompiledVersion?.Major;
        // 2024+: Trimble.Remoting publishes no pipes. Decided before any Tekla type is mentioned.
        // TODO(windows): a 2024+ guard would test EventWaitHandle.TryOpenExisting(channel + "$S")
        // (decompilation, backlog §3) — unverified, so those builds are not guarded.
        if (RemotingChannelNames.NamesItsOwnChannels(major)) return ChannelPublication.Unknown;

        var complete = TryListPublishedTeklaPipes(pipes);
        if (!complete && !_incompleteListingLogged)
        {
            _incompleteListingLogged = true;
            Console.Error.WriteLine(
                "[tekla] the named-pipe listing failed part-way; the connection guard lets calls through " +
                "(it only blocks on a complete listing).");
        }
        try { channel = which == TeklaChannel.Drawing ? ReadDrawingChannel() : ReadModelChannel(); }
        catch { channel = null; }
        return RemotingChannelNames.CheckPublished(channel, pipes, complete, major);
    }

    private static string? ReadModelChannel() => ReadChannel(typeof(TSM.Model).Assembly, ModelRemoter);

    // Separate method: mentioning a Drawing type loads that assembly when the method is compiled.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string? ReadDrawingChannel() => ReadChannel(typeof(TSD.DrawingHandler).Assembly, DrawingRemoter);

    private static string UnavailableMessage(
        ChannelPublication state, string channel, List<string> pipes, bool connectedBefore, bool forced)
    {
        var major = TeklaAssemblyResolver.CompiledVersion?.Major;
        var tekla = major is int m ? "Tekla Structures " + m : "Tekla Structures";
        string text;
        if (!connectedBefore && state == ChannelPublication.NotPublished)
            text = $"{tekla} is not reachable: its Open API channel '{channel}' is not published — {tekla} " +
                   "is not running or is still starting. Nothing was sent to Tekla, so this server has not " +
                   $"cached a failed connection: open the model in {tekla} and call the tool again. No MCP " +
                   "server restart is needed.";
        else if (!connectedBefore)
            text = $"{tekla} publishes its channels under another name than '{channel}'" +
                   (forced
                       ? ", which TEKLA_MCP_CHANNEL forces — correct or unset it."
                       : ", and aligning to it did not help.") +
                   " Nothing was sent to Tekla.";
        else if (state == ChannelPublication.NotPublished)
            text = $"{tekla} is not running: the channel '{channel}' this server was connected to is no longer " +
                   $"published (Tekla was closed or is restarting). Nothing was sent. Once the model is open in " +
                   $"{tekla} again, just call again — the server reconnects by itself; no MCP server restart.";
        else
            text = $"{tekla} is running under another session or instance name than '{channel}', the channel this " +
                   "server is bound to — possibly a DIFFERENT Tekla. The server only reconnects to its own channel " +
                   "(choosing between instances is not implemented): restart this MCP server to bind to the " +
                   "running one.";

        return text + " | published Tekla pipes: [" + string.Join(", ", pipes) + "]" +
               " | Tekla processes: " + DescribeTeklaProcesses();
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
        // The guard refused before anything was dialed; its message is already cause + action.
        for (var e = ex; e != null; e = e.InnerException)
            if (e is TeklaChannelUnavailableException unavailable)
                return unavailable.Message;

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
            cause = SupportsReconnect
                ? "The Tekla process behind this server's connection is gone (Tekla was restarted or closed). " +
                  "Once Tekla is running with the model open, call again: the server reconnects at the start of " +
                  "the next call. Restart the MCP server only if that keeps failing. If this call was a write, " +
                  "its outcome is unknown — read the targets back before repeating it."
                : "The server was connected earlier, but the Tekla process behind that connection is gone " +
                  "(Tekla was restarted or closed). Reconnecting inside the server is implemented for Tekla " +
                  "2021–2023 only: restart this MCP server (reconnect it in the MCP client)." + UntestedVersionNote;
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
        TryListPublishedTeklaPipes(result);
        return result;
    }

    /// <summary>
    /// Adds the published Tekla pipe names to <paramref name="result"/>; false when the listing
    /// stopped early. Named pipes are enumerable as files under \\.\pipe\, but some pipe names
    /// contain characters that are invalid in paths and can make enumeration throw mid-way on
    /// .NET Framework — keep what we got, and never treat a partial listing as proof of absence.
    /// </summary>
    private static bool TryListPublishedTeklaPipes(List<string> result)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(@"\\.\pipe\"))
            {
                var name = path.Substring(path.LastIndexOf('\\') + 1);
                if (name.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(name);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>The Open API clients the connection guard knows about.</summary>
public enum TeklaChannel
{
    Model,
    Drawing,
}

/// <summary>
/// Thrown by <see cref="TeklaRemotingChannel.EnsurePublished"/> INSTEAD of creating an Open API
/// proxy against a channel that is not published. The message carries cause and action.
/// </summary>
public sealed class TeklaChannelUnavailableException : InvalidOperationException
{
    public TeklaChannelUnavailableException(string message) : base(message) { }
}
