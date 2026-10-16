using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Core.Rendering;

namespace TeklaMcp.Server.Tools;

/// <summary>Drawing-list, active-editor, view and drawing-object discovery, and a picture of the open drawing.</summary>
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
                 "ID:ID2; otherwise use selection/current index and re-list after structural edits. " +
                 "Model-linked objects (Part, Bolt, ...) carry no geometry of their own in the Drawing " +
                 "API, so their points stay empty — for exact part geometry (arcs, radii, chords) call " +
                 "tekla_get_part_curve_geometry with the object's modelGuid and this viewId. Marks " +
                 "come with their content (markContent: property name + current value, or fixed " +
                 "text), the whole text in 'text', isEmptyMark, and the related model object " +
                 "(modelGuid/modelId, relatedObjectType); modelGuid and textContains filter marks too, " +
                 "and emptyMarksOnly=true finds marks that show nothing (clean them up with " +
                 "tekla_delete_drawing_objects). Straight dimension sets come with points, upDirection " +
                 "and dimensionDistance (view units at the current scale) + dimensionDistancePaperMm. " +
                 "includeModelGeometry=true adds, for parts and bolts, modelGeometry in the view's " +
                 "display coordinates (box from the solid's vertices with the z depth range, bolt " +
                 "positions); zRange/region/nameContains/profileContains then filter — they apply to " +
                 "the first 'limit' objects listed, so narrow with types/viewId first.")]
    public static IReadOnlyList<DrawingObjectInfo> ListDrawingObjects(
        ITeklaModelService model,
        [Description("Object IDs as 'id:id2; id:id2'.")] string? objectIds = null,
        [Description("Ephemeral indices, comma/semicolon separated. Prefer objectIds.")] string? indices = null,
        [Description("Exact API types, e.g. Part,Text,Mark,StraightDimensionSet.")] string? types = null,
        [Description("Ephemeral view index; prefer viewId/viewId2.")] int? viewIndex = null,
        [Description("Non-zero session target View ID.")] int? viewId = null,
        [Description("Session target View ID2.")] int? viewId2 = null,
        [Description("Exact represented model GUID.")] string? modelGuid = null,
        [Description("Text substring (Text objects and mark text).")] string? textContains = null,
        [Description("Use the current drawing-editor selection only.")] bool useSelection = false,
        [Description("Include children (dimensions, grid lines, etc.).")] bool recursive = true,
        [Description("Include object geometry and bbox. Disable for faster large scans.")] bool includeGeometry = true,
        [Description("Include all available UDAs (slower).")] bool includeUdas = false,
        [Description("Maximum objects (default 200).")] int limit = 200,
        [Description("Only marks whose content shows nothing.")] bool emptyMarksOnly = false,
        [Description("Parts and bolts: add their model geometry in the view's display coordinates " +
                     "(solid-vertex box with z depth range, bolt positions) — one solid read per part.")] bool includeModelGeometry = false,
        [Description("With includeModelGeometry: depth range 'zMin,zMax' (view z, +z toward the viewer); bolts keep only positions inside.")] string? zRange = null,
        [Description("With includeModelGeometry: x/y region 'xMin,yMin,xMax,yMax' in view coordinates the object must overlap.")] string? region = null,
        [Description("With includeModelGeometry: model-object name substring.")] string? nameContains = null,
        [Description("With includeModelGeometry: profile substring.")] string? profileContains = null)
    {
        var query = DrawingToolHelpers.BuildObjectQuery(
            objectIds, indices, types, viewIndex, viewId, viewId2,
            modelGuid, textContains, useSelection, recursive, includeGeometry, includeUdas,
            emptyMarksOnly);
        query.IncludeModelGeometry = includeModelGeometry;
        var z = ToolHelpers.ParseNums(zRange);
        if (z.Count == 2) { query.ZMin = System.Math.Min(z[0], z[1]); query.ZMax = System.Math.Max(z[0], z[1]); }
        var r = ToolHelpers.ParseNums(region);
        if (r.Count == 4)
        {
            query.RegionMin = new Point3D(System.Math.Min(r[0], r[2]), System.Math.Min(r[1], r[3]), 0);
            query.RegionMax = new Point3D(System.Math.Max(r[0], r[2]), System.Math.Max(r[1], r[3]), 0);
        }
        query.NameContains = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains;
        query.ProfileContains = string.IsNullOrWhiteSpace(profileContains) ? null : profileContains;
        if ((!string.IsNullOrWhiteSpace(zRange) && z.Count != 2) || (!string.IsNullOrWhiteSpace(region) && r.Count != 4))
            throw new ModelContextProtocol.McpException("zRange must be 'zMin,zMax' and region 'xMin,yMin,xMax,yMax'.");
        if (!includeModelGeometry && (query.ZMin.HasValue || query.RegionMin != null ||
                                      query.NameContains != null || query.ProfileContains != null))
            throw new ModelContextProtocol.McpException(
                "zRange, region, nameContains and profileContains need includeModelGeometry=true.");
        return model.FindDrawingObjects(query, limit);
    }

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

    [McpServerTool(Name = "tekla_capture_drawing", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "SCREENSHOT of the drawing open in Tekla's DRAWING EDITOR — exactly what Tekla draws (dimensions, marks, " +
        "texts, symbols, view labels, title block) — returned as an image you can look at, plus JSON. Use it to " +
        "CHECK your own drawing edits before reporting success: dimension lines on top of parts, a radius dimension " +
        "drawn as a full circle, a wrong view label or overlapping marks are obvious on the picture and invisible in " +
        "property lists. Scope: the whole sheet (default), one view's frame (viewId/viewId2 from " +
        "tekla_list_drawing_views, plus marginMm) or a sheet region ('minX,minY,maxX,maxY' in sheet paper mm). " +
        "Read-only: nothing in Tekla is zoomed, selected or changed, and only Tekla's own drawing window is " +
        "captured, never other windows. The picture is never sharper than the user's screen: at the editor's " +
        "zoom a whole A2 sheet has ~3 px per mm, so capture a view or region to read small text, or ask the user " +
        "to zoom in. JSON: scope (sheet|view|region|visible), shownMin/shownMax (sheet mm on the picture), " +
        "pixelToSheet (sheetX = originX + px*mmPerPixel, sheetY = originY - py*mmPerPixel), views[] with each " +
        "view's frame and pixel box on the picture, clipped, warnings. The sheet is located from its edges on the " +
        "screen; when the editor is zoomed in past the sheet's edges the WHOLE drawing window is returned with " +
        "scope='visible' and no coordinates — say so, or ask the user to zoom out. Needs an open drawing visible on " +
        "screen (not minimized or hidden behind a model view). Windows + live Tekla only; the mock backend returns " +
        "a stand-in of the sheet and view frames labelled as such (source='mock-schematic'). A picture costs " +
        "about 1-3k tokens; png (default) keeps thin lines crisp.")]
    public static IEnumerable<ContentBlock> CaptureDrawing(
        ITeklaModelService model,
        [Description("Non-zero session View ID of the view to frame (tekla_list_drawing_views). Default: the whole sheet.")]
        int? viewId = null,
        [Description("Session View ID2, with viewId.")] int? viewId2 = null,
        [Description("Sheet region 'minX,minY,maxX,maxY' in sheet paper mm (origin = sheet lower-left, like " +
                     "tekla_get_drawing_sheet and view frames). Not together with viewId.")]
        string? region = null,
        [Description("Paper mm shown around the view frame or region. Default 5.")] double marginMm = 5,
        [Description("Maximum picture width in px. Default 1600.")] int maxWidth = 1600,
        [Description("Maximum picture height in px. Default 1600.")] int maxHeight = 1600,
        [Description("png (default) | jpeg.")] string format = "png")
    {
        if (viewId.HasValue && viewId.Value == 0)
            throw new McpException("viewId must be a non-zero session View ID from tekla_list_drawing_views.");
        if (viewId.HasValue && !string.IsNullOrWhiteSpace(region))
            throw new McpException("Pass viewId or region, not both.");

        var request = new DrawingCaptureRequest
        {
            ViewId = viewId,
            ViewId2 = viewId.HasValue ? viewId2 : null,
            MarginMm = marginMm < 0 ? 0 : Math.Min(marginMm, 200),
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Format = string.IsNullOrWhiteSpace(format) ? "png" : format,
        };
        if (!string.IsNullOrWhiteSpace(region))
        {
            if (!DrawingCapturePlan.TryParseRegion(region, out var regionMin, out var regionMax, out var regionError))
                throw new McpException(regionError);
            request.RegionMin = regionMin;
            request.RegionMax = regionMax;
        }

        var result = model.CaptureDrawing(request);
        if (!result.Captured || result.Image == null)
            throw new McpException(result.Message ?? "The drawing could not be captured.");

        var image = result.Image;
        result.Image = null;
        return ModelVisualTools.Answer(image, result);
    }

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
