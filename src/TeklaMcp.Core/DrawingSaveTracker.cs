using System.Threading;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core;

/// <summary>
/// Counts writes committed to the OPEN drawing since it was last saved (issue #41).
/// <c>Drawing.CommitChanges</c> does not save: a day of committed edits was lost when the model
/// was reopened, while every result said "committed". Each such result now says it is unsaved,
/// and the active drawing reports how many MCP writes are waiting for a save.
/// </summary>
public sealed class DrawingSaveTracker
{
    public const string UnsavedWarning =
        "Committed to the OPEN drawing, NOT saved: Tekla drops unsaved drawing edits when the model is " +
        "reopened or the drawing is closed without saving. Save with tekla_save_drawing (or " +
        "tekla_close_drawing save=true) after each milestone.";

    private int _unsaved;

    /// <summary>Unsaved MCP writes to the open drawing.</summary>
    public int UnsavedWrites => Volatile.Read(ref _unsaved);

    /// <summary>Records an applied write that changed the open drawing, and labels its result.</summary>
    public void Committed(DrawingWriteResult result)
    {
        if (!result.Applied || result.CreatedCount + result.ModifiedCount + result.DeletedCount == 0) return;
        Interlocked.Increment(ref _unsaved);
        result.DrawingSaved = false;
        if (!result.Warnings.Contains(UnsavedWarning)) result.Warnings.Add(UnsavedWarning);
    }

    /// <summary>The open drawing was saved, or closed (saved or discarded): nothing is pending.</summary>
    public void Reset() => Interlocked.Exchange(ref _unsaved, 0);
}
