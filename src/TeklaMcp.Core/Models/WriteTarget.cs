namespace TeklaMcp.Core.Models;

/// <summary>
/// Which model a write (or its preview) went to — carried by every write result so a change can
/// be traced to a model and a Tekla process after the fact (DEV-005: with two Tekla instances
/// open the server wrote into the user's working model instead of the test model).
/// </summary>
public sealed class WriteTarget
{
    /// <summary>Model name as Tekla reports it.</summary>
    public string ModelName { get; set; } = "";

    /// <summary>Model folder as Tekla reports it. Compare against <c>expectedModelPath</c>.</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>
    /// Process id of the Tekla instance behind the connection, when it can be told apart: the
    /// only running instance of this build's Tekla version, or the PID in the channel name of a
    /// second instance. Null when ambiguous — see <see cref="Note"/>.
    /// </summary>
    public int? TeklaPid { get; set; }

    /// <summary>Start time of that process (local, yyyy-MM-dd HH:mm:ss) — a restart changes it.</summary>
    public string? TeklaStartedAt { get; set; }

    /// <summary>Why <see cref="TeklaPid"/> is missing, or other caveats.</summary>
    public string? Note { get; set; }
}
