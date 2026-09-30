using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Save the open model and switch Tekla to another model without the UI dialogs (backlog §5:
/// component development reopens its test model after every plugin rebuild). Both are
/// preview-by-default; opening another model is the most disruptive call in this server.
/// </summary>
[McpServerToolType]
public static class ModelLifecycleTools
{
    [McpServerTool(Name = "tekla_save_model")]
    [Description("Save the model open in Tekla (ModelHandler.Save; the comment lands in the save history, " +
                 "prefixed '[Tekla MCP]'). Preview unless apply=true — the preview says whether there are unsaved " +
                 "changes at all. Saving writes the model to disk for everyone who opens it: ask the user first.")]
    public static ModelFileOperationResult SaveModel(
        ITeklaModelService model,
        [Description("Save-history comment. Empty = 'saved by an agent'.")] string? comment = null,
        [Description("Set true to save. Default false = preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null)
        => ToolHelpers.Write(model, expectedModelPath, () => model.SaveModel(comment, apply));

    [McpServerTool(Name = "tekla_open_model")]
    [Description("Switch Tekla to ANOTHER model (ModelHandler.Open). This CLOSES the model the user has open and " +
                 "Tekla DISCARDS its unsaved changes — so the call is refused while there are unsaved changes " +
                 "unless discardUnsavedChanges=true, which you may only pass after the user explicitly agreed to " +
                 "lose them (better: tekla_save_model first). Preview unless apply=true; ALWAYS show the user the " +
                 "preview (current model, unsaved changes, target folder) and get their go-ahead. expectedModelPath " +
                 "here names the model you expect to be CLOSING. The result reports the model Tekla actually has " +
                 "open afterwards; every later write should pass the new path as expectedModelPath. Tekla may show " +
                 "its own dialogs (locked model, version upgrade) that block until someone answers them.")]
    public static ModelFileOperationResult OpenModel(
        ITeklaModelService model,
        [Description("Absolute path of the model FOLDER to open (the folder that contains the .db1).")] string modelFolder,
        [Description("Open the auto-saved state of that model instead of the last save. Default false.")] bool openAutoSaved = false,
        [Description("Allow Tekla to discard unsaved changes of the current model. Only after the user agreed. Default false.")]
        bool discardUnsavedChanges = false,
        [Description("Set true to switch models. Default false = preview.")] bool apply = false,
        [Description("Optional: the model you expect to be CLOSING (folder or .db1). A different open model refuses the call.")]
        string? expectedModelPath = null)
    {
        if (string.IsNullOrWhiteSpace(modelFolder))
            throw new ModelContextProtocol.McpException("modelFolder is required: the absolute path of the model folder.");
        return ToolHelpers.Write(model, expectedModelPath,
            () => model.OpenModel(modelFolder, openAutoSaved, discardUnsavedChanges, apply));
    }
}
