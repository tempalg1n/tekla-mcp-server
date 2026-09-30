using System;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Mock;

/// <summary>
/// Mock half of <c>tekla_save_model</c> / <c>tekla_open_model</c>: the open model is state, so a
/// switch shows up in connection info and write targets exactly like a live switch would. The
/// mock model starts "saved"; <see cref="MarkUnsaved"/> lets tests simulate pending changes.
/// </summary>
public sealed partial class MockTeklaModelService
{
    private string _modelName = "MockModel";
    private string _modelPath = "/virtual/mock/MockModel";
    private bool _modelSaved = true;

    /// <summary>Test hook: pretend the open model has unsaved changes.</summary>
    public void MarkUnsaved() => _modelSaved = false;

    public ModelFileOperationResult SaveModel(string? comment, bool apply)
    {
        var result = new ModelFileOperationResult
        {
            Operation = "save_model",
            Applied = apply,
            Backend = BackendName,
            ModelSavedBefore = _modelSaved,
            Outcome = apply ? WriteOutcome.Committed : WriteOutcome.Planned,
        };
        if (apply)
        {
            _modelSaved = true;
            result.ModelSavedAfter = true;
            result.Message = "Mock model saved" + (string.IsNullOrWhiteSpace(comment) ? "." : ": " + comment);
        }
        return result;
    }

    public ModelFileOperationResult OpenModel(string modelFolder, bool openAutoSaved, bool discardUnsavedChanges, bool apply)
    {
        var folder = (modelFolder ?? "").Trim().TrimEnd('/', '\\');
        var result = new ModelFileOperationResult
        {
            Operation = "open_model",
            Applied = apply,
            Backend = BackendName,
            ModelSavedBefore = _modelSaved,
            RequestedModelFolder = folder,
            AutoSavedAvailable = false,
            OpenModelPath = _modelPath,
        };

        if (!_modelSaved && !discardUnsavedChanges)
        {
            result.Outcome = apply ? WriteOutcome.NotWritten : WriteOutcome.Planned;
            result.Message = "The open model has unsaved changes, and opening another model discards them. Save it " +
                             "first (tekla_save_model), or pass discardUnsavedChanges=true once the user agreed.";
            return result;
        }
        if (!apply)
        {
            result.Outcome = WriteOutcome.Planned;
            return result;
        }

        var slash = Math.Max(folder.LastIndexOf('/'), folder.LastIndexOf('\\'));
        _modelName = slash >= 0 ? folder.Substring(slash + 1) : folder;
        _modelPath = folder;
        _modelSaved = true;
        result.OpenModelPath = _modelPath;
        result.Outcome = WriteOutcome.Committed;
        result.Message = "Mock switched to " + _modelName + " (the sample data stays the same).";
        return result;
    }
}
