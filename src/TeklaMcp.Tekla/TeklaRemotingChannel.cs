using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla;

/// <summary>
/// Points the Open API client at the remoting channel the running Tekla ACTUALLY publishes.
///
/// The Open API talks to Tekla over .NET remoting (IPC named pipes). The client-side channel
/// name is baked into Tekla.Structures.Model.dll as
/// <c>Tekla.Structures.Model-{app}:{apiVersion}</c> with an empty <c>{app}</c> suffix — but
/// some Tekla setups publish the pipe under a different suffix (observed in the wild:
/// <c>Console</c>, so the pipe is <c>Tekla.Structures.Model-Console:2023.0.0.0</c> — see
/// issue #7). When the names disagree, <c>Model.GetConnectionStatus()</c> returns false and
/// remoting fails with "Failed to connect to an IPC Port" even though Tekla is running with
/// a model open.
///
/// <see cref="Align"/> runs before <c>Model()</c> is created: it compares the baked-in channel
/// name with the <c>Tekla.Structures.Model-*</c> pipes present on the machine and, when the
/// default is not among them, rewrites the internal static
/// <c>Tekla.Structures.ModelInternal.Remoter.ChannelName</c> field to the published name.
/// The result is cached — EXCEPT when no Tekla pipes were found at all (server started before
/// Tekla): then the next call probes again, so "start server, open Tekla later" still aligns.
/// Everything here is best-effort: on any failure it logs to stderr and leaves the default.
///
/// Override: set <c>TEKLA_MCP_CHANNEL</c> to force an exact channel name (skips probing).
/// </summary>
public static class TeklaRemotingChannel
{
    private const string PipePrefix = "Tekla.Structures.Model-";

    private static readonly object Gate = new object();
    private static bool _done;

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
                    "[tekla] remoting channel alignment failed (keeping default): " + ex.Message);
            }
        }
    }

    /// <summary>One-line connection diagnostics for "not connected" error messages.</summary>
    public static string Describe()
    {
        try
        {
            var asm = typeof(TSM.Model).Assembly;
            var field = FindChannelNameField();
            var channel = field?.GetValue(null) as string ?? "(unknown)";
            var pipes = ListPublishedModelPipes();
            var origin = string.IsNullOrEmpty(asm.Location)
                ? $"(no file location; resolver bin: '{TeklaAssemblyResolver.BinDir ?? "?"}')"
                : $"'{asm.Location}'";
            return $"client channel '{channel}', API {asm.GetName().Version} from {origin}, " +
                   $"published model pipes: [{string.Join(", ", pipes)}]";
        }
        catch (Exception ex)
        {
            return "diagnostics unavailable: " + ex.Message;
        }
    }

    /// <summary>Returns false when there was nothing to align to yet (no Tekla pipes) — retryable.</summary>
    private static bool AlignCore()
    {
        var field = FindChannelNameField();
        if (field is null)
        {
            Console.Error.WriteLine(
                "[tekla] Remoter.ChannelName not found in this Tekla API version; using the default channel.");
            return true;
        }

        var current = field.GetValue(null) as string ?? "";

        var forced = Environment.GetEnvironmentVariable("TEKLA_MCP_CHANNEL");
        if (!string.IsNullOrWhiteSpace(forced))
        {
            field.SetValue(null, forced.Trim());
            Console.Error.WriteLine(
                $"[tekla] remoting channel forced via TEKLA_MCP_CHANNEL: '{forced.Trim()}' (default was '{current}').");
            return true;
        }

        var pipes = ListPublishedModelPipes();
        if (pipes.Count == 0) return false;       // Tekla not running (or pipes unreadable) — retry later.
        if (pipes.Contains(current)) return true; // the default channel IS published — no patch needed.

        // Only consider pipes for the SAME Open API version the loaded assemblies speak;
        // the remoting protocol is not compatible across versions anyway.
        var version = VersionSuffix(current);
        var candidates = version is null
            ? new List<string>()
            : pipes.FindAll(p => p.EndsWith(":" + version, StringComparison.OrdinalIgnoreCase));

        if (candidates.Count == 0)
        {
            Console.Error.WriteLine(
                $"[tekla] no published Tekla model pipe matches the loaded API version ({version ?? "?"}). " +
                $"Published: [{string.Join(", ", pipes)}]. Keeping default channel '{current}'. " +
                "This usually means the loaded Tekla assemblies do not match the running Tekla.");
            return true;
        }

        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        var chosen = candidates[0];
        field.SetValue(null, chosen);
        Console.Error.WriteLine(
            $"[tekla] remoting channel '{current}' is not published; using '{chosen}'." +
            (candidates.Count > 1
                ? $" Other candidates: [{string.Join(", ", candidates.GetRange(1, candidates.Count - 1))}]."
                : ""));
        return true;
    }

    private static FieldInfo? FindChannelNameField()
    {
        // internal static readonly string on all inspected versions (2021 baseline). Setting an
        // InitOnly static via reflection is supported on .NET Framework, which is the only TFM
        // this project builds for.
        var remoter = typeof(TSM.Model).Assembly
            .GetType("Tekla.Structures.ModelInternal.Remoter", throwOnError: false);
        return remoter?.GetField("ChannelName",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static List<string> ListPublishedModelPipes()
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

    private static string? VersionSuffix(string channel)
    {
        var i = channel.LastIndexOf(':');
        return i >= 0 && i < channel.Length - 1 ? channel.Substring(i + 1) : null;
    }
}
