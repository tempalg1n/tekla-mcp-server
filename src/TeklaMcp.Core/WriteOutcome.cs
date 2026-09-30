using System;

namespace TeklaMcp.Core;

/// <summary>
/// What a write call did to the model, as one word every write result carries (backlog §4).
///
/// Counters alone could not say it: <c>createdCount=3</c> next to a failed commit read as success,
/// and "no objects were written" after a connection broke mid-insert was a guess — the insert may
/// have reached Tekla. The Open API has no transactions: an <c>Insert</c>/<c>Modify</c>/
/// <c>Delete</c> is in the model as soon as it returns, and nothing is rolled back when a later
/// step fails. So a failure after the first write began is <see cref="Unknown"/>, never
/// "not written" and never "rolled back".
/// </summary>
public static class WriteOutcome
{
    /// <summary>apply=false: a preview, nothing was written.</summary>
    public const string Planned = "planned";

    /// <summary>
    /// apply=true, and nothing was written: the call failed before the first write, or Tekla
    /// refused every item. Safe to fix the cause and retry.
    /// </summary>
    public const string NotWritten = "not_written";

    /// <summary>Every item was written and the batch was committed.</summary>
    public const string Committed = "committed";

    /// <summary>Committed, but some items were refused (see errors); the others are in the model.</summary>
    public const string Partial = "partial";

    /// <summary>
    /// A failure after writing began (lost connection, failed commit): the model may hold some,
    /// all or none of the changes. Read the targets back before retrying — never retry blindly.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Outcome from counters alone, for backends that cannot fail half-way (the mock) and as the
    /// fallback for results a backend did not stamp. It can never say <see cref="Unknown"/> —
    /// a live backend that can lose the connection mid-write must stamp the outcome itself.
    /// </summary>
    public static string FromCounts(bool applied, long written, long failedItems)
    {
        if (!applied) return Planned;
        if (written == 0) return NotWritten;
        return failedItems == 0 ? Committed : Partial;
    }
}

/// <summary>
/// Tracks one write call so its <see cref="WriteOutcome"/> is derived from what actually
/// happened, not from counters. Backends call <see cref="BeginWrite"/> immediately before the
/// first mutating Open API call, <see cref="Complete"/> after the final commit returned, and
/// report exceptions through <see cref="Fail"/> (whole call) or <see cref="ItemFailed"/>.
/// </summary>
public sealed class WriteProgress
{
    /// <summary>A mutating call was about to be made — from here on a failure is not "not written".</summary>
    public bool Started { get; private set; }

    /// <summary>The final commit (or the last write, where there is no commit) returned.</summary>
    public bool Completed { get; private set; }

    /// <summary>A failure after <see cref="Started"/> left the model state unknown.</summary>
    public bool Interrupted { get; private set; }

    public void BeginWrite() => Started = true;

    public void Complete() => Completed = true;

    /// <summary>
    /// The call as a whole failed. Between the first write and the completed commit that is
    /// always unknown; after the commit (e.g. reading the result back) the write itself stands.
    /// </summary>
    public void Fail(Exception exception)
    {
        if (Started && !Completed) Interrupted = true;
    }

    /// <summary>
    /// One item failed and the batch goes on. Its own write may have reached Tekla only if the
    /// connection broke; any other item error is a validation or a refusal (not written).
    /// </summary>
    public void ItemFailed(Exception exception)
    {
        if (Started && ConnectionErrors.IsTeklaConnectionFailure(exception)) Interrupted = true;
    }

    /// <param name="apply">The call's apply flag.</param>
    /// <param name="written">Items known to be written (created + modified + deleted …).</param>
    /// <param name="failedItems">Items reported as failed (the result's error count).</param>
    public string Outcome(bool apply, long written, long failedItems)
    {
        if (!apply) return WriteOutcome.Planned;
        if (Interrupted) return WriteOutcome.Unknown;
        if (!Started) return WriteOutcome.NotWritten;
        if (!Completed) return WriteOutcome.Unknown;
        if (failedItems == 0) return written > 0 ? WriteOutcome.Committed : WriteOutcome.NotWritten;
        return written > 0 ? WriteOutcome.Partial : WriteOutcome.NotWritten;
    }
}
