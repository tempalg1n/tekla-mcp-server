using System;
using System.Reflection;
using System.Text;

namespace TeklaMcp.Core;

/// <summary>
/// The rules the live backend's assembly resolver applies to a bind the runtime could not satisfy on
/// its own (issue #17). Up to v0.8.0 it answered only <c>Tekla*</c> names from one folder; on Tekla
/// 2026 a <c>Tekla.Structures.dll</c> that lives in <c>bin\Net48Runtime</c> needs
/// <c>Trimble.Remoting</c> and <c>DotNetKit</c> from <c>bin</c>, and the 2026 Open API references
/// more such names (Newtonsoft.Json, Polly, SemanticDb.NET, …). Pure, so the decisions are
/// unit-testable without Tekla; the resolver supplies the files.
/// </summary>
public static class TeklaAssemblyProbe
{
    /// <summary>
    /// Satellite resource assemblies are never answered: Tekla keeps them in culture subfolders and
    /// the runtime falls back to the neutral resources, so such a miss is neither an error nor news.
    /// </summary>
    public static bool IsSatellite(string simpleName) =>
        simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// This server's own assemblies (<c>TeklaMcp.*</c>): they share the prefix but always come from the
    /// server's folder — never resolved from Tekla, and their failed binds are never Tekla's problem.
    /// </summary>
    public static bool IsServerAssembly(string simpleName) =>
        simpleName.StartsWith("TeklaMcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>Tekla*</c> names, answered from the Tekla install without an identity check, exactly as
    /// before issue #17 (the major-version check covers the install as a whole).
    /// </summary>
    public static bool IsTeklaName(string simpleName) =>
        simpleName.StartsWith("Tekla", StringComparison.OrdinalIgnoreCase) && !IsServerAssembly(simpleName);

    /// <summary>
    /// True when a failed bind belongs in the connection diagnostics: the Open API or one of its
    /// dependencies asked for it. Every failed bind of the process reaches the resolver — including
    /// the XmlSerializer probing for pre-generated <c>*.XmlSerializers</c> assemblies, which fails by
    /// design and is never reported.
    /// </summary>
    public static bool ConcernsTekla(string requestedName, string? requesterName, bool requesterInInstall)
    {
        if (requestedName.EndsWith(".XmlSerializers", StringComparison.OrdinalIgnoreCase)) return false;
        return IsTeklaSideName(requestedName) || requesterInInstall ||
               (!string.IsNullOrEmpty(requesterName) && IsTeklaSideName(requesterName!));
    }

    /// <summary>
    /// Whether <paramref name="candidate"/>, a file of the Tekla install, may answer the bind for
    /// <paramref name="requested"/>, an assembly outside the <c>Tekla*</c> names: the same simple
    /// name, the requested public key token when the request carries one, and at least the requested
    /// version. A newer file is what Tekla's own binding redirects hand out; an older one would be
    /// loaded into a caller compiled against something newer. <paramref name="reason"/> says why a
    /// file was refused.
    /// </summary>
    public static bool Accepts(AssemblyName requested, AssemblyName candidate, out string reason)
    {
        if (!string.Equals(requested.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
        {
            reason = "the file holds " + candidate.Name + ", not " + requested.Name;
            return false;
        }

        var wantedToken = requested.GetPublicKeyToken();
        if (wantedToken != null && wantedToken.Length > 0)
        {
            var token = candidate.GetPublicKeyToken();
            if (token is null || !SameBytes(wantedToken, token))
            {
                reason = "public key token " + Hex(token) + " instead of " + Hex(wantedToken);
                return false;
            }
        }

        if (requested.Version != null && (candidate.Version is null || candidate.Version < requested.Version))
        {
            reason = "version " + (candidate.Version?.ToString() ?? "(none)") + " is older than the requested " +
                     requested.Version;
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// One diagnostics line, e.g. "Trimble.Remoting 4.0.0.0 (needed by Tekla.Structures): not found
    /// in C:\…\bin, C:\…\bin\Net48Runtime".
    /// </summary>
    public static string DescribeMiss(AssemblyName requested, string? requesterName, string reason)
    {
        var version = requested.Version is null ? "" : " " + requested.Version;
        var who = string.IsNullOrEmpty(requesterName) ? "" : " (needed by " + requesterName + ")";
        return requested.Name + version + who + ": " + reason;
    }

    private static bool IsTeklaSideName(string name) =>
        IsTeklaName(name) || name.StartsWith("Trimble", StringComparison.OrdinalIgnoreCase);

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static string Hex(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return "null";
        var text = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) text.Append(b.ToString("x2"));
        return text.ToString();
    }
}
