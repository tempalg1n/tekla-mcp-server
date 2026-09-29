namespace TeklaMcp.Core.Models;

/// <summary>
/// One Tekla assembly the script compiler considered: what it is, where it came from, and
/// whether it went into the reference set. Returned by <c>tekla_check_csharp</c> (and on compile
/// failures) so a broken reference set is visible as a server problem, not a script error.
/// </summary>
public sealed class ScriptReferenceInfo
{
    /// <summary>Assembly simple name, e.g. "Tekla.Structures.Model"; the file name when unreadable.</summary>
    public string Name { get; set; } = "";

    /// <summary>Assembly version, e.g. "2021.0.0.0"; empty when the file is not a managed assembly.</summary>
    public string Version { get; set; } = "";

    /// <summary>Full path of the file.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// "loaded" — bound in this server process (what the live connection uses, possibly from the
    /// GAC); "directory" — found in the Tekla Open API folder.
    /// </summary>
    public string Origin { get; set; } = "";

    /// <summary>True when the assembly is part of the compile's reference set.</summary>
    public bool Included { get; set; }

    /// <summary>Why the file was left out; null for included references.</summary>
    public string? Reason { get; set; }
}
