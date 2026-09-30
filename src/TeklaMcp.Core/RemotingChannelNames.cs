using System;
using System.Collections.Generic;
using System.Linq;

namespace TeklaMcp.Core;

/// <summary>
/// Pure logic behind the Open API channel alignment in <c>TeklaRemotingChannel</c>: parsing the
/// channel names Tekla publishes, choosing the session suffix, and knowing which Tekla versions
/// must not be aligned at all. Tekla-free, so it is unit-testable.
///
/// Tekla 2021–2023 name every channel <c>{Assembly}-{SESSIONNAME}:{AssemblyVersion}</c> and
/// publish it as an IPC named pipe; a second instance of the same version appends its PID
/// (<c>Console-12345</c>). Tekla 2024+ moved to Trimble.Remoting — no named pipes — and names
/// its channels itself as <c>{Assembly}-{ProductName}-{SESSIONNAME|Console}:{FileVersion}</c>
/// (from decompiled 2024–2026 assemblies; not verified live).
/// </summary>
public static class RemotingChannelNames
{
    /// <summary>The first Tekla major version on Trimble.Remoting.</summary>
    public const int FirstTrimbleRemotingMajor = 2024;

    /// <summary>Assemblies whose channels are aligned — longest name first, so the bare base
    /// name only matches as a last resort.</summary>
    public static readonly IReadOnlyList<string> KnownAssemblyNames = new[]
    {
        "Tekla.Structures.Drawing",
        "Tekla.Structures.Model",
        "Tekla.Structures",
    };

    /// <summary>
    /// True for builds whose Open API names its channels itself (2024+). They must be left alone:
    /// writing a 2021–2023-style name into one of their Remoters poisons the proxy on first use.
    /// </summary>
    public static bool NamesItsOwnChannels(int? compiledMajor) =>
        compiledMajor is int major && major >= FirstTrimbleRemotingMajor;

    /// <summary>
    /// "Console" from "Tekla.Structures.Model-Console:2023.0.0.0" ("" for an unset SESSIONNAME);
    /// null when the name does not belong to one of <see cref="KnownAssemblyNames"/>.
    /// </summary>
    public static string? SessionSuffixOf(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return null;
        var colon = channel.LastIndexOf(':');
        var name = colon > 0 ? channel.Substring(0, colon) : channel;
        foreach (var assembly in KnownAssemblyNames)
        {
            if (name.StartsWith(assembly + "-", StringComparison.OrdinalIgnoreCase))
                return name.Substring(assembly.Length + 1);
        }
        return null;
    }

    /// <summary>
    /// The session suffix to align to, from published 2021–2023 pipe names. When
    /// <paramref name="compiledMajor"/> is known only pipes of that version count: another Tekla
    /// version's session says nothing about this build's Tekla, and aligning to it used to end
    /// alignment for the whole server process. Null means nothing of this version is published
    /// yet — retry later. With several sessions the current SESSIONNAME wins, otherwise the
    /// ordinal first; <paramref name="note"/> then explains the choice.
    /// </summary>
    public static string? DeriveSessionSuffix(
        IEnumerable<string>? pipes, int? compiledMajor, string? currentSessionName, out string? note)
    {
        note = null;
        var suffixes = new List<string>();
        foreach (var pipe in pipes ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrEmpty(pipe)) continue;
            var colon = pipe.LastIndexOf(':');
            if (colon <= 0) continue;
            if (compiledMajor is int major &&
                !(Version.TryParse(pipe.Substring(colon + 1), out var version) && version.Major == major))
                continue;

            var suffix = SessionSuffixOf(pipe);
            if (suffix != null && !suffixes.Contains(suffix, StringComparer.Ordinal))
                suffixes.Add(suffix);
        }

        if (suffixes.Count == 0) return null;
        if (suffixes.Count == 1) return suffixes[0];

        // Several Tekla sessions or instances (RDP users, or a second instance's Console-<PID>)
        // — keep the current session's if it is one of them, otherwise pick deterministically.
        if (currentSessionName != null && suffixes.Contains(currentSessionName, StringComparer.Ordinal))
            return currentSessionName;
        suffixes.Sort(StringComparer.OrdinalIgnoreCase);
        note = $"several Tekla sessions publish pipes ({string.Join(", ", suffixes)}); " +
               $"using '{suffixes[0]}'. Set TEKLA_MCP_CHANNEL to override.";
        return suffixes[0];
    }

    /// <summary>
    /// Whether <paramref name="channel"/> — the name an Open API client is about to dial — is
    /// published right now, judged from the named-pipe listing. Only 2021–2023 publish pipes, so
    /// anything else, an incomplete listing or an unknown channel name is
    /// <see cref="ChannelPublication.Unknown"/>: callers must then behave as before (fail open),
    /// never block a connection on a guess.
    /// </summary>
    public static ChannelPublication CheckPublished(
        string? channel, IEnumerable<string>? pipes, bool listingComplete, int? compiledMajor)
    {
        if (NamesItsOwnChannels(compiledMajor) || !listingComplete || pipes is null ||
            string.IsNullOrEmpty(channel))
            return ChannelPublication.Unknown;

        var assembly = AssemblyOf(channel!);
        if (assembly is null) return ChannelPublication.Unknown;

        var sameAssemblyAndVersion = false;
        foreach (var pipe in pipes)
        {
            if (string.IsNullOrEmpty(pipe)) continue;
            if (string.Equals(pipe, channel, StringComparison.OrdinalIgnoreCase))
                return ChannelPublication.Published;
            if (string.Equals(AssemblyOf(pipe), assembly, StringComparison.OrdinalIgnoreCase) &&
                SameMajor(pipe, channel!))
                sameAssemblyAndVersion = true;
        }
        return sameAssemblyAndVersion
            ? ChannelPublication.PublishedUnderOtherName
            : ChannelPublication.NotPublished;
    }

    /// <summary>"Tekla.Structures.Model" from "Tekla.Structures.Model-Console:2023.0.0.0".</summary>
    private static string? AssemblyOf(string channel)
    {
        foreach (var assembly in KnownAssemblyNames)
            if (channel.StartsWith(assembly + "-", StringComparison.OrdinalIgnoreCase))
                return assembly;
        return null;
    }

    private static bool SameMajor(string a, string b) =>
        MajorOf(a) is int major && major == MajorOf(b);

    private static int? MajorOf(string channel)
    {
        var colon = channel.LastIndexOf(':');
        return colon > 0 && Version.TryParse(channel.Substring(colon + 1), out var version)
            ? version.Major
            : (int?)null;
    }

    /// <summary>
    /// The same channel for another assembly: swaps the leading assembly name and keeps the rest
    /// (session/product segments and version), which is how Tekla names its channels in both the
    /// 2021–2023 and the 2024+ formats. Null when <paramref name="channel"/> does not start with
    /// "{fromAssembly}-".
    /// </summary>
    public static string? WithAssembly(string? channel, string fromAssembly, string toAssembly)
    {
        if (string.IsNullOrEmpty(channel) ||
            !channel!.StartsWith(fromAssembly + "-", StringComparison.OrdinalIgnoreCase))
            return null;
        return toAssembly + channel.Substring(fromAssembly.Length);
    }
}

/// <summary>Result of <see cref="RemotingChannelNames.CheckPublished"/>.</summary>
public enum ChannelPublication
{
    /// <summary>Cannot tell (2024+ build, incomplete pipe listing, unrecognized name) — do not block.</summary>
    Unknown,
    /// <summary>The exact channel is published.</summary>
    Published,
    /// <summary>The same assembly and Tekla version publishes, but under another session/instance
    /// suffix (another Windows session, or a second instance's <c>Console-&lt;PID&gt;</c>).</summary>
    PublishedUnderOtherName,
    /// <summary>Nothing of this assembly and version is published: that Tekla is not running.</summary>
    NotPublished,
}
