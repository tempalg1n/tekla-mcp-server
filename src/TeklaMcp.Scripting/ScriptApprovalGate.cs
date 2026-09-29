using System;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Scripting;

/// <summary>
/// Binds a script run to the exact source the user approved. <c>tekla_check_csharp</c> returns
/// <c>codeSha256</c>; the agent shows the script plus that hash to the user and passes it back as
/// <c>expectedSha256</c>. Without this, nothing stopped an agent from "fixing" an approved
/// mutation script after the approval and running the edited version.
///
/// Mutating runs REQUIRE the hash; read-only runs may pass it to guard against the same drift.
/// Runs before the backend is reached, so a rejected script is never compiled or executed.
/// </summary>
public static class ScriptApprovalGate
{
    /// <summary>Returns a rejection result, or null when the run may proceed.</summary>
    public static ScriptResult? Check(string code, bool allowMutations, string? expectedSha256)
    {
        var actual = ScriptEngine.ComputeCodeSha256(code);
        var expected = Normalize(expectedSha256);

        if (expected.Length == 0)
        {
            if (!allowMutations)
                return null;
            return Reject(code, actual,
                "allowMutations=true requires expectedSha256. Validate the exact script with tekla_check_csharp, " +
                "show the user the script and its codeSha256, and after their explicit approval pass that codeSha256 " +
                "as expectedSha256.");
        }

        if (string.Equals(expected, actual, StringComparison.Ordinal))
            return null;

        return Reject(code, actual,
            "expectedSha256 does not match this script (codeSha256 " + actual + "): the source differs from the " +
            "version that was checked/approved — even whitespace counts. Do not run a changed script on an old " +
            "approval: re-check it with tekla_check_csharp and get the user's approval for the new version.");
    }

    /// <summary>Tolerates case, surrounding whitespace and a "sha256:" prefix; nothing else.</summary>
    private static string Normalize(string? hash)
    {
        var h = (hash ?? "").Trim();
        if (h.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            h = h.Substring("sha256:".Length).Trim();
        return h.ToLowerInvariant();
    }

    private static ScriptResult Reject(string code, string actual, string violation)
    {
        var result = new ScriptResult
        {
            Success = false,
            Stage = "policy",
            CodeSha256 = actual,
            Guidance = "Nothing was compiled or executed.",
        };
        try
        {
            result.DetectedMutatingMembers.AddRange(ScriptPolicy.Analyze(code, allowMutations: true).MutatingMembers);
        }
        catch
        {
            // Informational only; the rejection stands without it.
        }
        result.PolicyViolations.Add(violation);
        return result;
    }
}
