using System;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla;

/// <summary>
/// Live half of <c>tekla_save_model</c> / <c>tekla_open_model</c> (backlog §5): component
/// development reopens the test model after every plugin rebuild and used to drive the UI dialog.
///
/// <c>ModelHandler.Open</c> "opens a new model without saving changes to current model" (API doc),
/// so <see cref="OpenModel"/> re-checks <c>IsModelSaved()</c> right before the call and refuses on
/// pending changes unless the caller explicitly discards them. The outcome is read back from
/// <c>Model.GetInfo()</c> rather than trusted from Open's bool: a failed open may still have closed
/// the current model. Compiles 2021–2026. TODO(windows): run live — in particular whether a Tekla
/// dialog (locked model, version upgrade) blocks Open() until someone answers it.
/// </summary>
public sealed partial class TeklaModelService
{
    public ModelFileOperationResult SaveModel(string? comment, bool apply)
    {
        var result = new ModelFileOperationResult { Operation = "save_model", Applied = apply, Backend = BackendName };
        var progress = new WriteProgress();
        try
        {
            GetConnectedModel();
            var handler = new TSM.ModelHandler();
            result.ModelSavedBefore = handler.IsModelSaved();
            if (!apply)
            {
                result.Message = result.ModelSavedBefore == true
                    ? "No unsaved changes — saving would only add a save-history entry."
                    : "The model has unsaved changes; apply=true saves them.";
                return Stamp(result, progress, saved: false);
            }

            progress.BeginWrite();
            var saved = handler.Save(
                "[Tekla MCP] " + (string.IsNullOrWhiteSpace(comment) ? "saved by an agent" : comment!.Trim()),
                Environment.UserName);
            progress.Complete();
            result.ModelSavedAfter = handler.IsModelSaved();
            if (!saved) result.Message = "Tekla refused ModelHandler.Save().";
            return Stamp(result, progress, saved);
        }
        catch (Exception ex)
        {
            progress.Fail(ex);
            result.Message = ErrorText.Flatten(ex);
            return Stamp(result, progress, saved: false);
        }
    }

    public ModelFileOperationResult OpenModel(
        string modelFolder, bool openAutoSaved, bool discardUnsavedChanges, bool apply)
    {
        var folder = (modelFolder ?? "").Trim().Trim('"');
        var result = new ModelFileOperationResult
        {
            Operation = "open_model",
            Applied = apply,
            Backend = BackendName,
            RequestedModelFolder = folder,
        };
        var progress = new WriteProgress();
        try
        {
            var model = GetConnectedModel();
            var handler = new TSM.ModelHandler();
            var before = model.GetInfo().ModelPath ?? "";
            result.OpenModelPath = before;
            result.ModelSavedBefore = handler.IsModelSaved();
            try { result.AutoSavedAvailable = handler.IsModelAutoSaved(folder); } catch { /* informational */ }

            if (ModelPaths.Same(folder, before))
            {
                result.Message = "That model is already open.";
                return Stamp(result, progress, opened: false);
            }
            if (result.ModelSavedBefore == false && !discardUnsavedChanges)
            {
                result.Message = "The open model has unsaved changes, and opening another model DISCARDS them. " +
                                 "Save it first (tekla_save_model), or pass discardUnsavedChanges=true once the user agreed.";
                return Stamp(result, progress, opened: false);
            }
            if (!apply) return Stamp(result, progress, opened: false);

            progress.BeginWrite();
            var opened = handler.Open(folder, openAutoSaved);
            progress.Complete();

            // Trust the model Tekla reports, not the bool.
            var after = new TSM.Model().GetInfo().ModelPath ?? "";
            result.OpenModelPath = after;
            var switched = ModelPaths.Same(folder, after);
            if (!switched && !ModelPaths.Same(before, after))
            {
                result.Outcome = WriteOutcome.Unknown;
                result.Message = $"Open() returned {opened} and Tekla now reports '{after}' — neither the requested " +
                                 "nor the previous model. Check Tekla before doing anything else.";
                return result;
            }
            if (!switched)
                result.Message = "Tekla did not open the model (Open() returned " + opened + "); the previous model is still open.";
            return Stamp(result, progress, switched);
        }
        catch (Exception ex)
        {
            progress.Fail(ex);
            result.Message = ErrorText.Flatten(ex);
            return Stamp(result, progress, opened: false);
        }
    }

    private static ModelFileOperationResult Stamp(ModelFileOperationResult result, WriteProgress progress, bool saved = false, bool opened = false)
    {
        var done = saved || opened;
        result.Outcome = progress.Outcome(result.Applied, done ? 1 : 0, done ? 0 : 1);
        return result;
    }
}
