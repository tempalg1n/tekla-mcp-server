namespace TeklaMcp.Core.Models;

/// <summary>Preview/result of saving the open model or switching Tekla to another model.</summary>
public sealed class ModelFileOperationResult
{
    /// <summary>"save_model" or "open_model".</summary>
    public string Operation { get; set; } = "";

    /// <summary>False = preview, nothing done. True = apply was requested (success: <see cref="Outcome"/>).</summary>
    public bool Applied { get; set; }

    /// <summary>planned / not_written / committed / unknown (see <see cref="TeklaMcp.Core.WriteOutcome"/>).</summary>
    public string Outcome { get; set; } = "";

    /// <summary>The model that was open when the call started (the one saved, or the one closed).</summary>
    public WriteTarget? Target { get; set; }

    /// <summary>Tekla's IsModelSaved() before the call: false = there are unsaved changes.</summary>
    public bool? ModelSavedBefore { get; set; }

    /// <summary>IsModelSaved() after a save.</summary>
    public bool? ModelSavedAfter { get; set; }

    /// <summary>open_model: the folder that was asked for.</summary>
    public string? RequestedModelFolder { get; set; }

    /// <summary>open_model: whether Tekla has auto-saved data for that folder.</summary>
    public bool? AutoSavedAvailable { get; set; }

    /// <summary>open_model: the model path Tekla reports after the call.</summary>
    public string? OpenModelPath { get; set; }

    public string Backend { get; set; } = "";
    public string? Message { get; set; }
}
