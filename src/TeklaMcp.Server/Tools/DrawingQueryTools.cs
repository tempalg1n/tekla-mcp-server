using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>Drawing-list, active-editor, view and drawing-object discovery.</summary>
[McpServerToolType]
public static class DrawingQueryTools
{
    [McpServerTool(Name = "tekla_get_drawing_status")]
    [Description("Check Drawing API connectivity and return the active drawing/editor state.")]
    public static DrawingStatusInfo GetDrawingStatus(ITeklaModelService model) =>
        model.GetDrawingStatus();

    [McpServerTool(Name = "tekla_get_active_drawing")]
    [Description("Return the currently open drawing, or null when the drawing editor is closed.")]
    public static DrawingInfo? GetActiveDrawing(ITeklaModelService model) =>
        model.GetDrawingStatus().ActiveDrawing;

    [McpServerTool(Name = "tekla_list_drawings")]
    [Description("List/search the Tekla drawing list. Each result includes an opaque best-available " +
                 "key, session ID/ID2, type, mark/titles, source model GUID, status flags and dates. " +
                 "If ID/ID2 are zero the fallback key may change after metadata edits; re-list.")]
    public static IReadOnlyList<DrawingInfo> ListDrawings(
        ITeklaModelService model,
        [Description("Exact opaque keys, comma/semicolon separated. Empty = no key filter.")] string? keyIn = null,
        [Description("assembly, single_part, cast_unit, ga, or exact API type.")] string? type = null,
        [Description("Drawing mark substring.")] string? markContains = null,
        [Description("Drawing name substring.")] string? nameContains = null,
        [Description("Substring across Title1/2/3.")] string? titleContains = null,
        [Description("Exact associated model-object GUID.")] string? associatedModelGuid = null,
        [Description("UpToDateStatus substring, e.g. PartsWereModified.")] string? upToDateStatusContains = null,
        [Description("Filter issued state; omit for either.")] bool? isIssued = null,
        [Description("Filter locked state; omit for either.")] bool? isLocked = null,
        [Description("Filter ready-for-issue state; omit for either.")] bool? isReadyForIssue = null,
        [Description("Use drawings selected in the Drawing List dialog only.")] bool selectedOnly = false,
        [Description("Maximum rows (default 200, hard-capped by backend).")] int limit = 200) =>
        model.FindDrawings(
            DrawingToolHelpers.BuildDrawingQuery(
                keyIn, type, markContains, nameContains, titleContains,
                associatedModelGuid, upToDateStatusContains,
                isIssued, isLocked, isReadyForIssue, selectedOnly),
            limit);

    [McpServerTool(Name = "tekla_get_selected_drawings")]
    [Description("Return drawings currently selected in Tekla's Drawing List dialog.")]
    public static IReadOnlyList<DrawingInfo> GetSelectedDrawings(
        ITeklaModelService model,
        [Description("Maximum rows (default 200).")] int limit = 200) =>
        model.FindDrawings(new DrawingQuery { SelectedOnly = true }, limit);

    [McpServerTool(Name = "tekla_get_drawing_summary")]
    [Description("Aggregate drawing count/status/type metrics without opening any drawing.")]
    public static DrawingSummary GetDrawingSummary(
        ITeklaModelService model,
        [Description("Maximum drawings to scan (default 2000).")] int limit = 2000)
    {
        var safeLimit = Math.Max(0, Math.Min(limit, 9999));
        var scanned = model.FindDrawings(new DrawingQuery(), safeLimit + 1);
        var rows = scanned.Take(safeLimit).ToList();
        var summary = new DrawingSummary
        {
            Limit = safeLimit,
            Truncated = scanned.Count > safeLimit,
            Total = rows.Count,
            Issued = rows.Count(d => d.IsIssued),
            IssuedButModified = rows.Count(d => d.IsIssuedButModified),
            Locked = rows.Count(d => d.IsLocked),
            ReadyForIssue = rows.Count(d => d.IsReadyForIssue),
            NotUpToDate = rows.Count(d => !string.Equals(
                d.UpToDateStatus, "DrawingIsUpToDate", StringComparison.OrdinalIgnoreCase)),
            Backend = model.GetDrawingStatus().Backend,
        };
        foreach (var row in rows)
        {
            Bump(summary.CountByType, row.Type);
            Bump(summary.CountByStatus, row.UpToDateStatus);
        }
        return summary;
    }

    [McpServerTool(Name = "tekla_find_drawing_issues")]
    [Description("Run drawing-list QA heuristics: issued-but-modified, stale/check-needed, " +
                 "missing mark/name and ready-for-issue drawings that are not up to date.")]
    public static DrawingQaReport FindDrawingIssues(
        ITeklaModelService model,
        [Description("Maximum drawings to inspect (default 2000).")] int limit = 2000)
    {
        var rows = model.FindDrawings(new DrawingQuery(), limit).ToList();
        var report = new DrawingQaReport
        {
            CheckedCount = rows.Count,
            Backend = model.GetDrawingStatus().Backend,
        };
        AddIssue(report, rows.Where(d => d.IsIssuedButModified),
            "issued_but_modified", "Issued drawing has changed since issue.");
        AddIssue(report, rows.Where(d => !string.Equals(
                d.UpToDateStatus, "DrawingIsUpToDate", StringComparison.OrdinalIgnoreCase)),
            "not_up_to_date", "Drawing status is not DrawingIsUpToDate.");
        AddIssue(report, rows.Where(d => d.IsReadyForIssue && !string.Equals(
                d.UpToDateStatus, "DrawingIsUpToDate", StringComparison.OrdinalIgnoreCase)),
            "ready_but_stale", "Drawing is marked ready for issue but is not up to date.");
        AddIssue(report, rows.Where(d => string.IsNullOrWhiteSpace(d.Mark)),
            "missing_mark", "Drawing mark is empty.");
        AddIssue(report, rows.Where(d => string.IsNullOrWhiteSpace(d.Name)),
            "missing_name", "Drawing name is empty.");
        return report;
    }

    [McpServerTool(Name = "tekla_get_drawing_model_objects")]
    [Description("Return full model Identifier values (ID, ID2, GUID) represented by one drawing. " +
                 "Use the opaque key from tekla_list_drawings; an unambiguous exact mark also works.")]
    public static DrawingModelObjectResult GetDrawingModelObjects(
        ITeklaModelService model,
        [Description("Opaque drawing key or unambiguous exact mark.")] string keyOrMark,
        [Description("Zero-based page offset.")] int offset = 0,
        [Description("Page size (default 500, maximum 5000).")] int limit = 500) =>
        model.GetDrawingModelObjects(keyOrMark, offset, limit);

    [McpServerTool(Name = "tekla_get_drawing_sheet")]
    [Description("Return the ACTIVE drawing sheet's actual container width, height and origin in " +
                 "paper millimetres, plus the configured layout sheet size and auto-size mode. " +
                 "Actual and configured sizes are separate because auto-sized drawings may differ.")]
    public static DrawingSheetInfo GetDrawingSheet(ITeklaModelService model) =>
        model.GetDrawingSheet();

    [McpServerTool(Name = "tekla_list_drawing_views")]
    [Description("List all nested views on the active drawing: best-available session View ID/ID2, " +
                 "ephemeral index, name/type, paper frame, scale, restriction box and coordinate systems. " +
                 "Re-list after structural edits.")]
    public static IReadOnlyList<DrawingViewInfo> ListDrawingViews(ITeklaModelService model) =>
        model.GetDrawingViews();

    [McpServerTool(Name = "tekla_list_drawing_objects")]
    [Description("List/filter objects in the active drawing. Returns best-available session Object/View " +
                 "IDs, model identifier, geometry/text/visibility and optional UDAs. Prefer non-zero " +
                 "ID:ID2; otherwise use selection/current index and re-list after structural edits.")]
    public static IReadOnlyList<DrawingObjectInfo> ListDrawingObjects(
        ITeklaModelService model,
        [Description("Object IDs as 'id:id2; id:id2'.")] string? objectIds = null,
        [Description("Ephemeral indices, comma/semicolon separated. Prefer objectIds.")] string? indices = null,
        [Description("Exact API types, e.g. Part,Text,Mark,StraightDimensionSet.")] string? types = null,
        [Description("Ephemeral view index; prefer viewId/viewId2.")] int? viewIndex = null,
        [Description("Non-zero session target View ID.")] int? viewId = null,
        [Description("Session target View ID2.")] int? viewId2 = null,
        [Description("Exact represented model GUID.")] string? modelGuid = null,
        [Description("Text substring (Text objects).")] string? textContains = null,
        [Description("Use the current drawing-editor selection only.")] bool useSelection = false,
        [Description("Include children (dimensions, grid lines, etc.).")] bool recursive = true,
        [Description("Include object geometry and bbox. Disable for faster large scans.")] bool includeGeometry = true,
        [Description("Include all available UDAs (slower).")] bool includeUdas = false,
        [Description("Maximum objects (default 200).")] int limit = 200) =>
        model.FindDrawingObjects(
            DrawingToolHelpers.BuildObjectQuery(
                objectIds, indices, types, viewIndex, viewId, viewId2,
                modelGuid, textContains, useSelection, recursive, includeGeometry, includeUdas),
            limit);

    [McpServerTool(Name = "tekla_get_selected_drawing_objects")]
    [Description("Return objects currently selected in the active drawing editor.")]
    public static IReadOnlyList<DrawingObjectInfo> GetSelectedDrawingObjects(
        ITeklaModelService model,
        [Description("Include geometry/bbox.")] bool includeGeometry = true,
        [Description("Include all available UDAs (slower).")] bool includeUdas = false,
        [Description("Maximum objects (default 200).")] int limit = 200) =>
        model.FindDrawingObjects(new DrawingObjectQuery
        {
            UseSelection = true,
            IncludeGeometry = includeGeometry,
            IncludeUdas = includeUdas,
        }, limit);

    [McpServerTool(Name = "tekla_select_drawing_objects")]
    [Description("Select/highlight matching objects in the active drawing editor. UI side effect only.")]
    public static DrawingSelectionResult SelectDrawingObjects(
        ITeklaModelService model,
        [Description("Object IDs as 'id:id2; id:id2'.")] string? objectIds = null,
        [Description("Exact API types, comma/semicolon separated.")] string? types = null,
        [Description("Non-zero session View ID.")] int? viewId = null,
        [Description("Session View ID2.")] int? viewId2 = null,
        [Description("Exact represented model GUID.")] string? modelGuid = null,
        [Description("Text substring.")] string? textContains = null,
        [Description("Maximum selected objects (default 200).")] int limit = 200) =>
        model.SelectDrawingObjects(
            DrawingToolHelpers.BuildObjectQuery(
                objectIds: objectIds, types: types, viewId: viewId, viewId2: viewId2,
                modelGuid: modelGuid, textContains: textContains),
            limit);

    private static void Bump(IDictionary<string, int> dictionary, string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "(none)" : value;
        dictionary[value] = dictionary.TryGetValue(value, out var count) ? count + 1 : 1;
    }

    private static void AddIssue(
        DrawingQaReport report,
        IEnumerable<DrawingInfo> source,
        string code,
        string description)
    {
        var rows = source.ToList();
        if (rows.Count == 0) return;
        report.Issues.Add(new DrawingIssueGroup
        {
            Code = code,
            Description = description,
            Count = rows.Count,
            SampleKeys = rows.Take(20).Select(d => d.Key).ToList(),
        });
    }
}
