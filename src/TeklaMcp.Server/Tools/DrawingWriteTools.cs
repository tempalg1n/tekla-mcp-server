using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Drawing navigation/lifecycle/output. Every state-changing operation is preview-by-default.
/// </summary>
[McpServerToolType]
public static class DrawingWriteTools
{
    [McpServerTool(Name = "tekla_open_drawing")]
    [Description("Open one drawing in the Tekla drawing editor by opaque key or unambiguous exact " +
                 "mark. Refuses to replace another active drawing. Preview unless apply=true.")]
    public static DrawingWriteResult OpenDrawing(
        ITeklaModelService model,
        [Description("Opaque key from tekla_list_drawings, or unambiguous exact mark.")] string keyOrMark,
        [Description("Show the drawing editor UI. Default true.")] bool showDrawing = true,
        [Description("Set true to open; false returns preview only.")] bool apply = false) =>
        model.OpenDrawing(keyOrMark, showDrawing, apply);

    [McpServerTool(Name = "tekla_save_drawing")]
    [Description("Save the active drawing. Preview unless apply=true.")]
    public static DrawingWriteResult SaveDrawing(
        ITeklaModelService model,
        [Description("Set true to save; false returns preview only.")] bool apply = false) =>
        model.SaveActiveDrawing(apply);

    [McpServerTool(Name = "tekla_close_drawing")]
    [Description("Close the active drawing. Preview unless apply=true. save=false explicitly " +
                 "DISCARDS unsaved editor changes.")]
    public static DrawingWriteResult CloseDrawing(
        ITeklaModelService model,
        [Description("Save changes before closing. false discards unsaved changes.")] bool save = true,
        [Description("Set true to close; false returns preview only.")] bool apply = false) =>
        model.CloseActiveDrawing(save, apply);

    [McpServerTool(Name = "tekla_create_drawing")]
    [Description("Create one assembly/single-part/cast-unit/GA drawing. The drawing editor must be " +
                 "closed. modelGuid is required except for GA. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawing(
        ITeklaModelService model,
        [Description("assembly, single_part, cast_unit or ga.")] string type,
        [Description("Source part/assembly/cast-unit GUID. Empty for GA.")] string modelGuid = "",
        [Description("Saved drawing attributes file. Empty = defaults.")] string attributeFile = "",
        [Description("Optional sheet number.")] int? sheetNumber = null,
        [Description("Drawing name (especially useful for GA).")] string name = "",
        [Description("Set true to create; false returns preview only.")] bool apply = false) =>
        model.CreateDrawings(new[]
        {
            new DrawingSpec
            {
                Type = type ?? "",
                ModelGuid = modelGuid ?? "",
                AttributeFile = attributeFile ?? "",
                SheetNumber = sheetNumber,
                Name = name ?? "",
            },
        }, apply);

    [McpServerTool(Name = "tekla_create_drawings")]
    [Description("Batch-create up to 50 assembly/single-part/cast-unit/GA drawings. The editor " +
                 "must be closed. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawings(
        ITeklaModelService model,
        [Description("Structured drawing specifications (maximum 50).")] IReadOnlyList<DrawingSpec> drawings,
        [Description("Set true to create; false returns preview only.")] bool apply = false) =>
        model.CreateDrawings((drawings ?? new List<DrawingSpec>()).Take(50).ToList(), apply);

    [McpServerTool(Name = "tekla_create_drawings_from_rule")]
    [Description("Create drawings using a saved Tekla AutoDrawing rule (safer than attempting " +
                 "arbitrary drawing cloning). The editor must be closed. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingsFromRule(
        ITeklaModelService model,
        [Description("Saved AutoDrawing rule filename understood by Tekla.")] string ruleFile,
        [Description("Part/assembly model GUIDs, comma/semicolon/newline separated (max 50).")] string modelGuids,
        [Description("Set true to run AutoDrawing; false returns preview.")] bool apply = false) =>
        model.CreateDrawingsFromRule(
            ruleFile ?? "",
            ToolHelpers.ParseList(modelGuids).Take(50).ToList(),
            apply);

    [McpServerTool(Name = "tekla_modify_drawings")]
    [Description("Modify name/titles/status flags on matched drawing-list rows. Null fields stay " +
                 "unchanged. Preview unless apply=true; capped by limit.")]
    public static DrawingWriteResult ModifyDrawings(
        ITeklaModelService model,
        [Description("Exact opaque keys, comma/semicolon separated.")] string? keyIn = null,
        [Description("assembly, single_part, cast_unit or ga.")] string? type = null,
        [Description("Mark substring.")] string? markContains = null,
        [Description("Name substring.")] string? nameContains = null,
        [Description("New name; omit to keep.")] string? name = null,
        [Description("New Title1; omit to keep.")] string? title1 = null,
        [Description("New Title2; omit to keep.")] string? title2 = null,
        [Description("New Title3; omit to keep.")] string? title3 = null,
        [Description("New frozen flag; omit to keep.")] bool? isFrozen = null,
        [Description("New locked flag; omit to keep.")] bool? isLocked = null,
        [Description("New master-drawing flag; omit to keep.")] bool? isMasterDrawing = null,
        [Description("New ready-for-issue flag; omit to keep.")] bool? isReadyForIssue = null,
        [Description("Safety cap (default 50).")] int limit = 50,
        [Description("Set true to commit; false returns preview only.")] bool apply = false) =>
        model.ModifyDrawings(
            DrawingToolHelpers.BuildDrawingQuery(
                keyIn: keyIn, type: type, markContains: markContains, nameContains: nameContains),
            new DrawingModification
            {
                Name = name,
                Title1 = title1,
                Title2 = title2,
                Title3 = title3,
                IsFrozen = isFrozen,
                IsLocked = isLocked,
                IsMasterDrawing = isMasterDrawing,
                IsReadyForIssue = isReadyForIssue,
            },
            apply,
            limit);

    [McpServerTool(Name = "tekla_delete_drawings")]
    [Description("Delete matched drawings. Active drawing cannot be deleted. Preview unless " +
                 "apply=true; capped by limit.")]
    public static DrawingWriteResult DeleteDrawings(
        ITeklaModelService model,
        [Description("Exact opaque keys, comma/semicolon separated.")] string? keyIn = null,
        [Description("Drawing type filter.")] string? type = null,
        [Description("Mark substring.")] string? markContains = null,
        [Description("Name substring.")] string? nameContains = null,
        [Description("Use Drawing List selection only.")] bool selectedOnly = false,
        [Description("Safety cap (default 50).")] int limit = 50,
        [Description("Set true to delete; false returns preview only.")] bool apply = false)
    {
        if (string.IsNullOrWhiteSpace(keyIn) &&
            string.IsNullOrWhiteSpace(type) &&
            string.IsNullOrWhiteSpace(markContains) &&
            string.IsNullOrWhiteSpace(nameContains) &&
            !selectedOnly)
            return new DrawingWriteResult
            {
                Operation = "delete_drawings",
                Applied = apply,
                Backend = model.GetDrawingStatus().Backend,
                Message = "Delete requires a key/type/mark/name filter or Drawing List selection.",
            };
        return Operate(
            model, "delete", keyIn, type, markContains,
            nameContains, selectedOnly, limit, apply);
    }

    [McpServerTool(Name = "tekla_issue_drawings")]
    [Description("Issue matched drawings. Preview unless apply=true; capped by limit.")]
    public static DrawingWriteResult IssueDrawings(
        ITeklaModelService model,
        [Description("Exact opaque keys, comma/semicolon separated.")] string? keyIn = null,
        [Description("Mark substring.")] string? markContains = null,
        [Description("Use Drawing List selection only.")] bool selectedOnly = false,
        [Description("Safety cap (default 50).")] int limit = 50,
        [Description("Set true to issue; false returns preview only.")] bool apply = false) =>
        Operate(model, "issue", keyIn, null, markContains, null, selectedOnly, limit, apply);

    [McpServerTool(Name = "tekla_unissue_drawings")]
    [Description("Unissue matched drawings. Preview unless apply=true; capped by limit.")]
    public static DrawingWriteResult UnissueDrawings(
        ITeklaModelService model,
        [Description("Exact opaque keys, comma/semicolon separated.")] string? keyIn = null,
        [Description("Mark substring.")] string? markContains = null,
        [Description("Use Drawing List selection only.")] bool selectedOnly = false,
        [Description("Safety cap (default 50).")] int limit = 50,
        [Description("Set true to unissue; false returns preview only.")] bool apply = false) =>
        Operate(model, "unissue", keyIn, null, markContains, null, selectedOnly, limit, apply);

    [McpServerTool(Name = "tekla_update_drawings")]
    [Description("Update matched drawings from the model. Numbering must be up to date; active " +
                 "drawing cannot be updated. Preview unless apply=true.")]
    public static DrawingWriteResult UpdateDrawings(
        ITeklaModelService model,
        [Description("Exact opaque keys, comma/semicolon separated.")] string? keyIn = null,
        [Description("Mark substring.")] string? markContains = null,
        [Description("Use Drawing List selection only.")] bool selectedOnly = false,
        [Description("Safety cap (default 50).")] int limit = 50,
        [Description("Set true to update; false returns preview only.")] bool apply = false) =>
        Operate(model, "update", keyIn, null, markContains, null, selectedOnly, limit, apply);

    [McpServerTool(Name = "tekla_place_drawing_views")]
    [Description("Auto-place views on the matched ACTIVE drawing. Use a key for the active " +
                 "drawing; preview unless apply=true.")]
    public static DrawingWriteResult PlaceDrawingViews(
        ITeklaModelService model,
        [Description("Opaque active-drawing key.")] string key,
        [Description("Set true to place views; false returns preview only.")] bool apply = false)
    {
        var keys = ToolHelpers.ParseList(key);
        if (keys.Count != 1)
            return new DrawingWriteResult
            {
                Operation = "place_views_drawings",
                Applied = apply,
                Backend = model.GetDrawingStatus().Backend,
                Message = "Exactly one non-empty drawing key is required.",
            };
        return model.OperateDrawings(
            DrawingToolHelpers.BuildDrawingQuery(keyIn: keys[0]),
            "place_views", null, apply, 1);
    }

    [McpServerTool(Name = "tekla_export_drawings_pdf")]
    [Description("Export matched CLOSED drawings to PDF using Tekla printing. outputFile must be " +
                 "an explicit absolute path; for multiple drawings use {mark}/{name} so paths are unique. " +
                 "Preview unless apply=true.")]
    public static DrawingWriteResult ExportDrawingsPdf(
        ITeklaModelService model,
        [Description("Exact opaque keys, comma/semicolon separated.")] string? keyIn = null,
        [Description("Mark substring.")] string? markContains = null,
        [Description("Use Drawing List selection only.")] bool selectedOnly = false,
        [Description("Absolute PDF output path/template. Required; use {mark}/{name} for batches.")] string outputFile = "",
        [Description("Color, BlackAndWhite or GreyScale.")] string colorMode = "Color",
        [Description("Auto, Landscape or Portrait.")] string orientation = "Auto",
        [Description("Auto, A0, A1, A2, A3, A4 or A5.")] string paperSize = "Auto",
        [Description("Auto or Scale.")] string scalingMethod = "Auto",
        [Description("Scale factor when scalingMethod=Scale.")] double scaleFactor = 1.0,
        [Description("Open the generated file after export.")] bool openFileWhenFinished = false,
        [Description("Allow replacing an existing output file. Default false.")] bool overwrite = false,
        [Description("Safety cap (default 50).")] int limit = 50,
        [Description("Set true to export; false returns preview only.")] bool apply = false)
    {
        var invalid = ValidatePrintChoice(
            colorMode, new[] { "Color", "BlackAndWhite", "GreyScale" }, "colorMode") ??
            ValidatePrintChoice(
                orientation, new[] { "Auto", "Landscape", "Portrait" }, "orientation") ??
            ValidatePrintChoice(
                paperSize, new[] { "Auto", "A0", "A1", "A2", "A3", "A4", "A5" }, "paperSize") ??
            ValidatePrintChoice(
                scalingMethod, new[] { "Auto", "Scale" }, "scalingMethod");
        if (invalid != null)
            return new DrawingWriteResult
            {
                Operation = "print_drawings",
                Applied = apply,
                Backend = model.GetDrawingStatus().Backend,
                Message = invalid,
            };

        return model.OperateDrawings(
            DrawingToolHelpers.BuildDrawingQuery(
                keyIn: keyIn, markContains: markContains, selectedOnly: selectedOnly),
            "print",
            new DrawingPrintOptions
            {
                OutputType = "PDF",
                OutputFile = outputFile ?? "",
                ColorMode = colorMode ?? "Color",
                Orientation = orientation ?? "Auto",
                PaperSize = paperSize ?? "Auto",
                ScalingMethod = scalingMethod ?? "Auto",
                ScaleFactor = scaleFactor,
                OpenFileWhenFinished = openFileWhenFinished,
                Overwrite = overwrite,
            },
            apply,
            limit);
    }

    private static string? ValidatePrintChoice(
        string? value,
        IReadOnlyList<string> allowed,
        string parameter)
    {
        if (allowed.Any(item =>
                string.Equals(item, value, System.StringComparison.OrdinalIgnoreCase)))
            return null;
        return "Unknown " + parameter + ". Use " + string.Join(", ", allowed) + ".";
    }

    private static DrawingWriteResult Operate(
        ITeklaModelService model,
        string operation,
        string? keyIn,
        string? type,
        string? markContains,
        string? nameContains,
        bool selectedOnly,
        int limit,
        bool apply) =>
        model.OperateDrawings(
            DrawingToolHelpers.BuildDrawingQuery(
                keyIn: keyIn, type: type, markContains: markContains,
                nameContains: nameContains, selectedOnly: selectedOnly),
            operation, null, apply, limit);
}
