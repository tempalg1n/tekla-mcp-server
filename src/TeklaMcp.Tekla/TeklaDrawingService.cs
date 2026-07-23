using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core.Models;
using TS = Tekla.Structures;
using TSD = Tekla.Structures.Drawing;
using TSDI = Tekla.Structures.DrawingInternal;
using TSDUI = Tekla.Structures.Drawing.UI;
using TSG = Tekla.Structures.Geometry3d;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla;

/// <summary>
/// Drawing API implementation. Kept in a separate partial for auditability: the Drawing API
/// has different coordinate systems, persistence rules and editor preconditions than Model.
/// Every write remains preview-by-default.
/// </summary>
public sealed partial class TeklaModelService
{
    private const int DrawingBatchLimit = 50;
    private const int DrawingObjectBatchLimit = 200;

    public DrawingStatusInfo GetDrawingStatus()
    {
        var result = new DrawingStatusInfo { Backend = BackendName };
        try
        {
            var handler = GetDrawingHandler();
            result.Connected = handler.GetConnectionStatus();
            if (!result.Connected)
            {
                result.Message = "Drawing API is not connected. Tekla must be running with a model open.";
                return result;
            }

            var active = TryGetActiveDrawing(handler);
            result.AnyDrawingOpen = active != null;
            if (active != null) result.ActiveDrawing = MapDrawing(active, active);
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        return result;
    }

    public IReadOnlyList<DrawingInfo> FindDrawings(DrawingQuery query, int? limit = null)
    {
        var rows = new List<DrawingInfo>();
        try
        {
            var handler = GetDrawingHandler();
            query = query ?? new DrawingQuery();
            var active = TryGetActiveDrawing(handler);
            var enumerator = query.SelectedOnly
                ? handler.GetDrawingSelector().GetSelected()
                : handler.GetDrawings();

            var cap = NormalizeLimit(limit, 10000);
            if (cap == 0) return rows;
            while (enumerator.MoveNext())
            {
                var drawing = enumerator.Current;
                if (drawing == null) continue;
                var info = MapDrawing(drawing, active);
                if (!MatchesDrawing(info, query)) continue;
                rows.Add(info);
                if (rows.Count >= cap) break;
            }
        }
        catch
        {
            // Query tools degrade to an empty result; status gives the connection error.
        }
        return rows;
    }

    public DrawingModelObjectResult GetDrawingModelObjects(
        string keyOrMark,
        int offset = 0,
        int limit = 500)
    {
        var result = new DrawingModelObjectResult
        {
            Backend = BackendName,
            Offset = Math.Max(0, offset),
        };
        try
        {
            var handler = GetDrawingHandler();
            var drawing = ResolveDrawing(handler, keyOrMark);
            if (drawing == null)
            {
                result.Message = "Drawing not found or key/mark is ambiguous.";
                return result;
            }
            var identifiers = handler.GetModelObjectIdentifiers(drawing)
                .Cast<TS.Identifier>()
                .ToList();
            result.TotalCount = identifiers.Count;
            var cap = Math.Max(0, Math.Min(limit, 5000));
            result.Items = identifiers
                .Skip(result.Offset)
                .Take(cap)
                .Select(MapIdentifier)
                .ToList();
            result.ReturnedCount = result.Items.Count;
            result.Truncated = result.Offset + result.ReturnedCount < result.TotalCount;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        return result;
    }

    public DrawingSheetInfo GetDrawingSheet()
    {
        var result = new DrawingSheetInfo { Backend = BackendName };
        var notes = new List<string>();
        try
        {
            var handler = GetDrawingHandler();
            var active = TryGetActiveDrawing(handler);
            if (active == null)
            {
                result.Message = "No active drawing.";
                return result;
            }

            TSD.ContainerView sheet;
            try
            {
                sheet = active.GetSheet();
            }
            catch (Exception ex)
            {
                result.Message = "Could not read the active drawing sheet: " + ex.Message;
                return result;
            }

            if (sheet == null)
            {
                result.Message = "The active drawing returned no sheet.";
                return result;
            }

            result.Available = true;
            try { result.DrawingKey = MapDrawing(active, active).Key; }
            catch (Exception ex) { notes.Add("Drawing key unavailable: " + ex.Message); }
            try
            {
                result.Width = Math.Round(sheet.Width, 3);
                result.Height = Math.Round(sheet.Height, 3);
            }
            catch (Exception ex)
            {
                notes.Add("Sheet dimensions unavailable: " + ex.Message);
            }
            try { result.Origin = ToDto(sheet.Origin); }
            catch (Exception ex) { notes.Add("Sheet origin unavailable: " + ex.Message); }
            try { result.FrameOrigin = ToDto(sheet.FrameOrigin); }
            catch (Exception ex) { notes.Add("Sheet frame origin unavailable: " + ex.Message); }

            TSD.LayoutAttributes? layout = null;
            try { layout = active.Layout; }
            catch (Exception ex) { notes.Add("Drawing layout unavailable: " + ex.Message); }
            if (layout != null)
            {
                // TODO(windows): verify actual ContainerView dimensions versus Layout.SheetSize
                // for auto-sized sheets in every supported Tekla release.
                try { result.SizeDefinitionMode = layout.SizeDefinitionMode.ToString(); }
                catch (Exception ex) { notes.Add("Layout size mode unavailable: " + ex.Message); }
                try { result.AutoSizeOptions = layout.AutoSizeOptions.ToString(); }
                catch (Exception ex) { notes.Add("Layout auto-size options unavailable: " + ex.Message); }
                try
                {
                    var configuredSize = layout.SheetSize;
                    if (configuredSize != null)
                    {
                        result.LayoutSheetWidth = Math.Round(configuredSize.Width, 3);
                        result.LayoutSheetHeight = Math.Round(configuredSize.Height, 3);
                    }
                }
                catch (Exception ex)
                {
                    notes.Add("Configured layout sheet size unavailable: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            notes.Add(ex.Message);
        }

        if (notes.Count > 0)
            result.Message = string.Join(" ", notes);
        return result;
    }

    public IReadOnlyList<DrawingViewInfo> GetDrawingViews()
    {
        var result = new List<DrawingViewInfo>();
        try
        {
            var handler = GetDrawingHandler();
            var active = TryGetActiveDrawing(handler);
            if (active == null) return result;
            var views = EnumerateViews(active);
            for (var i = 0; i < views.Count; i++)
                result.Add(MapView(views[i], i));
        }
        catch
        {
            // Best-effort read.
        }
        return result;
    }

    public IReadOnlyList<DrawingObjectInfo> FindDrawingObjects(
        DrawingObjectQuery query,
        int? limit = null)
    {
        try
        {
            var handler = GetDrawingHandler();
            var active = TryGetActiveDrawing(handler);
            if (active == null) return new List<DrawingObjectInfo>();
            return GetDrawingObjectRecords(handler, active, query ?? new DrawingObjectQuery(), limit)
                .Select(r => MapDrawingObject(r, query ?? new DrawingObjectQuery()))
                .ToList();
        }
        catch
        {
            return new List<DrawingObjectInfo>();
        }
    }

    public DrawingSelectionResult SelectDrawingObjects(
        DrawingObjectQuery query,
        int? limit = null)
    {
        var result = new DrawingSelectionResult { Backend = BackendName };
        try
        {
            var handler = GetDrawingHandler();
            var active = TryGetActiveDrawing(handler);
            if (active == null)
            {
                result.Message = "No active drawing.";
                return result;
            }

            var effective = query ?? new DrawingObjectQuery();
            effective.UseSelection = false;
            var records = GetDrawingObjectRecords(handler, active, effective, limit ?? DrawingObjectBatchLimit);
            var objects = new ArrayList();
            foreach (var record in records) objects.Add(record.Object);
            var selector = handler.GetDrawingObjectSelector();
            selector.UnselectAllObjects();
            if (objects.Count > 0) selector.SelectObjects(objects, false);

            result.SelectedCount = records.Count;
            result.Preview = records.Take(20).Select(r => MapDrawingObject(r, effective)).ToList();
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        return result;
    }

    public DrawingWriteResult OpenDrawing(string keyOrMark, bool showDrawing, bool apply)
    {
        var result = NewDrawingWriteResult("open_drawing", apply);
        try
        {
            var handler = GetDrawingHandler();
            var drawing = ResolveDrawing(handler, keyOrMark);
            if (drawing == null)
            {
                result.Message = "Drawing not found, or the supplied key/mark is ambiguous. Use tekla_list_drawings and pass a unique key.";
                return result;
            }
            result.PlannedCount = 1;
            result.DrawingPreview.Add(MapDrawing(drawing, TryGetActiveDrawing(handler)));
            if (!apply) return result;

            var active = TryGetActiveDrawing(handler);
            if (active != null && !SameDatabaseObject(active, drawing))
            {
                result.Message = "Another drawing is active. Close it explicitly before opening a different drawing.";
                return result;
            }
            if (active != null)
            {
                result.ModifiedCount = 0;
                result.Message = "The requested drawing is already active.";
                return result;
            }

            if (!handler.SetActiveDrawing(drawing, showDrawing))
            {
                result.Message = "Tekla rejected SetActiveDrawing.";
                return result;
            }
            result.ModifiedCount = 1;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        return result;
    }

    public DrawingWriteResult CloseActiveDrawing(bool save, bool apply)
    {
        var result = NewDrawingWriteResult("close_drawing", apply);
        try
        {
            var handler = GetDrawingHandler();
            var active = TryGetActiveDrawing(handler);
            if (active == null)
            {
                result.Message = "No active drawing.";
                return result;
            }
            result.PlannedCount = 1;
            result.DrawingPreview.Add(MapDrawing(active, active));
            if (!apply) return result;
            if (!handler.CloseActiveDrawing(save))
            {
                result.Message = "Tekla rejected CloseActiveDrawing.";
                return result;
            }
            result.ModifiedCount = 1;
            if (!save)
                result.Message = "Active drawing closed WITHOUT saving; unsaved editor changes were discarded.";
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        return result;
    }

    public DrawingWriteResult SaveActiveDrawing(bool apply)
    {
        var result = NewDrawingWriteResult("save_drawing", apply);
        try
        {
            var handler = GetDrawingHandler();
            var active = TryGetActiveDrawing(handler);
            if (active == null)
            {
                result.Message = "No active drawing.";
                return result;
            }
            result.PlannedCount = 1;
            result.DrawingPreview.Add(MapDrawing(active, active));
            if (!apply) return result;
            if (!handler.SaveActiveDrawing())
            {
                result.Message = "Tekla rejected SaveActiveDrawing.";
                return result;
            }
            result.ModifiedCount = 1;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        return result;
    }

    public DrawingWriteResult CreateDrawings(IReadOnlyList<DrawingSpec> specs, bool apply)
    {
        var result = NewDrawingWriteResult("create_drawings", apply);
        var safeSpecs = (specs ?? new List<DrawingSpec>()).Take(DrawingBatchLimit).ToList();
        result.PlannedCount = safeSpecs.Count;
        if (!apply)
        {
            foreach (var spec in safeSpecs)
                result.DrawingPreview.Add(PreviewDrawing(spec));
            return result;
        }

        try
        {
            var handler = GetDrawingHandler();
            if (TryGetActiveDrawing(handler) != null)
            {
                result.Message = "Drawing creation requires the drawing editor to be closed. Close the active drawing explicitly.";
                return result;
            }

            var model = GetConnectedModel();
            foreach (var spec in safeSpecs)
            {
                try
                {
                    var drawing = CreateDrawing(model, spec);
                    if (drawing == null)
                        throw new InvalidOperationException("Unsupported drawing type or invalid model GUID.");
                    if (!drawing.Insert())
                        throw new InvalidOperationException("Tekla rejected drawing Insert().");

                    // Insert is already a persistent mutation. Account for it before any
                    // optional follow-up so a rejected rename/commit cannot be reported as
                    // "nothing created".
                    result.CreatedCount++;
                    result.DrawingPreview.Add(MapDrawing(drawing, null));
                    if (!string.IsNullOrWhiteSpace(spec.Name))
                    {
                        drawing.Name = spec.Name;
                        if (!drawing.Modify())
                            result.Warnings.Add(
                                DrawingLabel(drawing) +
                                ": drawing was inserted, but Tekla rejected the requested Name.");
                    }
                    StampDrawingOrigin(drawing, "mcp:create_drawing");
                    CommitDrawingChanges(
                        drawing, "Tekla MCP: create drawing", result);
                }
                catch (Exception exItem)
                {
                    result.Errors.Add(DescribeDrawingSpec(spec) + ": " + exItem.Message);
                }
            }
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        return result;
    }

    public DrawingWriteResult CreateDrawingsFromRule(
        string ruleFile,
        IReadOnlyList<string> modelGuids,
        bool apply)
    {
        var result = NewDrawingWriteResult("create_drawings_from_rule", apply);
        var guids = (modelGuids ?? new List<string>()).Take(DrawingBatchLimit).ToList();
        result.PlannedCount = guids.Count;
        foreach (var guid in guids)
            result.DrawingPreview.Add(new DrawingInfo
            {
                Key = "(preview)",
                Type = "AutoDrawingRule",
                Name = ruleFile ?? "",
                AssociatedModelGuid = guid,
            });
        if (!apply) return result;
        try
        {
            var handler = GetDrawingHandler();
            if (TryGetActiveDrawing(handler) != null)
            {
                result.Message = "AutoDrawing creation requires the drawing editor to be closed.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(ruleFile))
            {
                result.Message = "ruleFile is required.";
                return result;
            }

            var identifiers = new List<TS.Identifier>();
            foreach (var guidText in guids)
            {
                if (Guid.TryParse(guidText, out var guid))
                    identifiers.Add(new TS.Identifier(guid));
                else
                    result.Errors.Add("Invalid model GUID: " + guidText + ".");
            }
            if (identifiers.Count == 0) return result;

            var beforeDrawings = EnumerateDrawings(handler);
            var beforeKeys = new HashSet<string>(
                beforeDrawings.Select(drawing => MapDrawing(drawing, null).Key),
                StringComparer.OrdinalIgnoreCase);
            var drawingsBefore = beforeDrawings.Count;
            var rule = new TSD.Automation.AutoDrawingRule(ruleFile);
            TSD.Automation.AutoDrawingsStatusEnum status;
            var apiSucceeded =
                TSD.Automation.DrawingCreator.CreateDrawings(
                    rule, identifiers, out status);
            // The API can report false after creating a subset. Always diff the drawing
            // list so the result reflects durable partial work.
            var after = EnumerateDrawings(handler)
                .Select(drawing => new
                {
                    Drawing = drawing,
                    Info = MapDrawing(drawing, null),
                })
                .ToList();
            var created = after
                .Where(row => !beforeKeys.Contains(row.Info.Key))
                .Take(DrawingBatchLimit)
                .ToList();
            result.CreatedCount = created.Count;
            result.DrawingPreview = created.Select(row => row.Info).ToList();
            foreach (var row in created)
            {
                try
                {
                    StampDrawingOrigin(
                        row.Drawing, "mcp:create_drawing_from_rule");
                    CommitDrawingChanges(
                        row.Drawing,
                        "Tekla MCP: create drawing from AutoDrawing rule",
                        result);
                }
                catch (Exception exItem)
                {
                    result.Errors.Add(
                        DrawingLabel(row.Drawing) + ": origin stamp/commit: " + exItem.Message);
                }
            }
            if (!apiSucceeded)
            {
                if (result.CreatedCount > 0)
                    result.Warnings.Add(
                        "Tekla AutoDrawing returned false after creating " +
                        result.CreatedCount + " drawing(s).");
                else
                    result.Message = "Tekla AutoDrawing failed: " + status + ".";
            }
            if (result.Message == null)
                result.Message = "AutoDrawing status: " + status + ". Submitted " +
                    identifiers.Count + " model object(s); created drawing count was measured before/after " +
                    drawingsBefore + " -> " + after.Count + ".";
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        return result;
    }

    public DrawingWriteResult ModifyDrawings(
        DrawingQuery query,
        DrawingModification modification,
        bool apply,
        int? limit = null)
    {
        var result = NewDrawingWriteResult("modify_drawings", apply);
        modification = modification ?? new DrawingModification();
        if (modification.Name == null &&
            modification.Title1 == null &&
            modification.Title2 == null &&
            modification.Title3 == null &&
            !modification.IsFrozen.HasValue &&
            !modification.IsLocked.HasValue &&
            !modification.IsMasterDrawing.HasValue &&
            !modification.IsReadyForIssue.HasValue)
        {
            result.Message = "No drawing fields were supplied to modify.";
            return result;
        }
        try
        {
            var handler = GetDrawingHandler();
            var keyError = ValidateUniqueDrawingKeys(handler, query);
            if (keyError != null)
            {
                result.Message = keyError;
                return result;
            }
            var active = TryGetActiveDrawing(handler);
            var drawings = ResolveDrawings(handler, query, limit ?? DrawingBatchLimit);
            result.PlannedCount = drawings.Count;
            result.DrawingPreview.AddRange(drawings.Take(20).Select(d => MapDrawing(d, active)));
            if (!apply) return result;

            foreach (var drawing in drawings)
            {
                try
                {
                    ApplyDrawingModification(drawing, modification);
                    if (!drawing.Modify()) throw new InvalidOperationException("Tekla rejected drawing Modify().");
                    StampDrawingOrigin(drawing, "mcp:modify_drawing");
                    result.ModifiedCount++;
                    CommitDrawingChanges(
                        drawing, "Tekla MCP: modify drawing", result);
                }
                catch (Exception exItem)
                {
                    result.Errors.Add(DrawingLabel(drawing) + ": " + exItem.Message);
                }
            }
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        return result;
    }

    public DrawingWriteResult OperateDrawings(
        DrawingQuery query,
        string operation,
        DrawingPrintOptions? printOptions,
        bool apply,
        int? limit = null)
    {
        var op = (operation ?? "").Trim().ToLowerInvariant();
        var result = NewDrawingWriteResult(op + "_drawings", apply);
        try
        {
            var handler = GetDrawingHandler();
            var keyError = ValidateUniqueDrawingKeys(handler, query);
            if (keyError != null)
            {
                result.Message = keyError;
                return result;
            }
            var active = TryGetActiveDrawing(handler);
            var drawings = ResolveDrawings(handler, query, limit ?? DrawingBatchLimit);
            result.PlannedCount = drawings.Count;
            result.DrawingPreview.AddRange(drawings.Take(20).Select(d => MapDrawing(d, active)));
            if (op == "print")
            {
                var options = printOptions ?? new DrawingPrintOptions();
                if (string.Equals(options.OutputType, "PDF", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(options.OutputFile))
                {
                    result.Message =
                        "PDF export requires an explicit absolute outputFile. Use {mark}/{name} " +
                        "in a batch template.";
                    return result;
                }
                foreach (var drawing in drawings)
                {
                    var path = ResolvePrintOutput(drawing, options);
                    if (!string.IsNullOrWhiteSpace(path)) result.OutputFiles.Add(path);
                }
                if (result.OutputFiles.Any(path => !System.IO.Path.IsPathRooted(path)))
                {
                    result.Message =
                        "Every print output path must be absolute; relative paths depend on the server process directory.";
                    return result;
                }
                if (result.OutputFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    result.OutputFiles.Count)
                {
                    result.Message =
                        "The output template resolves multiple drawings to the same file. " +
                        "Use {mark} and/or {name} to make every path unique.";
                    return result;
                }
                if (!options.Overwrite)
                {
                    var existing = result.OutputFiles
                        .Where(System.IO.File.Exists)
                        .ToList();
                    if (existing.Count > 0)
                    {
                        result.Message =
                            "Export was not started because output file(s) already exist and overwrite=false.";
                        result.Errors.AddRange(existing);
                        return result;
                    }
                }
            }
            if (!apply) return result;
            if (op == "print" && active != null)
            {
                result.Message =
                    "Printing requires the drawing editor to be closed. Tekla may close a different " +
                    "active drawing implicitly, so save and close it explicitly first.";
                return result;
            }

            foreach (var drawing in drawings.ToList())
            {
                try
                {
                    if (active != null && SameDatabaseObject(active, drawing) &&
                        (op == "delete" || op == "update" || op == "print"))
                        throw new InvalidOperationException(op + " cannot operate on the active drawing. Close it first.");

                    switch (op)
                    {
                        case "delete":
                            if (!drawing.Delete()) throw new InvalidOperationException("Tekla rejected Delete().");
                            result.DeletedCount++;
                            CommitDrawingChanges(
                                drawing, "Tekla MCP: delete drawing", result);
                            break;
                        case "issue":
                            StampDrawingOrigin(drawing, "mcp:issue_drawing");
                            if (!handler.IssueDrawing(drawing)) throw new InvalidOperationException("Tekla rejected IssueDrawing().");
                            result.ModifiedCount++;
                            break;
                        case "unissue":
                            StampDrawingOrigin(drawing, "mcp:unissue_drawing");
                            if (!handler.UnissueDrawing(drawing)) throw new InvalidOperationException("Tekla rejected UnissueDrawing().");
                            result.ModifiedCount++;
                            break;
                        case "update":
                            StampDrawingOrigin(drawing, "mcp:update_drawing");
                            if (!handler.UpdateDrawing(drawing)) throw new InvalidOperationException(
                                "Tekla rejected UpdateDrawing(); numbering must be up to date.");
                            result.ModifiedCount++;
                            break;
                        case "place_views":
                            if (active == null || !SameDatabaseObject(active, drawing))
                                throw new InvalidOperationException(
                                    "PlaceViews requires this drawing to be active.");
                            if (!drawing.PlaceViews()) throw new InvalidOperationException("Tekla rejected PlaceViews().");
                            StampDrawingOrigin(drawing, "mcp:place_drawing_views");
                            result.ModifiedCount++;
                            CommitDrawingChanges(
                                drawing, "Tekla MCP: place drawing views", result);
                            break;
                        case "print":
                            PrintOneDrawing(handler, drawing, printOptions ?? new DrawingPrintOptions(), result);
                            result.ModifiedCount++;
                            break;
                        default:
                            throw new ArgumentException("Unknown operation. Use delete, issue, unissue, update, place_views or print.");
                    }
                }
                catch (Exception exItem)
                {
                    result.Errors.Add(DrawingLabel(drawing) + ": " + exItem.Message);
                }
            }
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        return result;
    }

    private static TSD.DrawingHandler GetDrawingHandler()
    {
        EnsureTeklaReady();
        try { TSD.DrawingEnumeratorBase.AutoFetch = true; } catch { }
        var handler = new TSD.DrawingHandler();
        if (!handler.GetConnectionStatus())
            throw new InvalidOperationException(
                "Drawing API is not connected. Is Tekla Structures running with a model open?");
        return handler;
    }

    private static TSD.Drawing? TryGetActiveDrawing(TSD.DrawingHandler handler)
    {
        try { return handler.GetActiveDrawing(); }
        catch { return null; }
    }

    private static List<TSD.Drawing> ResolveDrawings(
        TSD.DrawingHandler handler,
        DrawingQuery query,
        int? limit)
    {
        var result = new List<TSD.Drawing>();
        query = query ?? new DrawingQuery();
        var active = TryGetActiveDrawing(handler);
        var enumerator = query.SelectedOnly
            ? handler.GetDrawingSelector().GetSelected()
            : handler.GetDrawings();
        var cap = NormalizeLimit(limit, DrawingBatchLimit);
        if (cap == 0) return result;
        while (enumerator.MoveNext())
        {
            var drawing = enumerator.Current;
            if (drawing == null) continue;
            if (!MatchesDrawing(MapDrawing(drawing, active), query)) continue;
            result.Add(drawing);
            if (result.Count >= cap) break;
        }
        return result;
    }

    private static List<TSD.Drawing> EnumerateDrawings(TSD.DrawingHandler handler)
    {
        var result = new List<TSD.Drawing>();
        var enumerator = handler.GetDrawings();
        while (enumerator.MoveNext())
            if (enumerator.Current != null)
                result.Add(enumerator.Current);
        return result;
    }

    private static string? ValidateUniqueDrawingKeys(
        TSD.DrawingHandler handler,
        DrawingQuery query)
    {
        if (query?.KeyIn == null || query.KeyIn.Count == 0) return null;
        var rows = EnumerateDrawings(handler)
            .Select(drawing => MapDrawing(drawing, null))
            .ToList();
        foreach (var key in query.KeyIn.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var count = rows.Count(row =>
                string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase));
            if (count != 1)
                return "Drawing key '" + key + "' matched " + count +
                       " rows. Re-list drawings and use a unique non-zero identifier key.";
        }
        return null;
    }

    private static TSD.Drawing? ResolveDrawing(TSD.DrawingHandler handler, string keyOrMark)
    {
        if (string.IsNullOrWhiteSpace(keyOrMark)) return null;
        var exactKey = new List<TSD.Drawing>();
        var exactMark = new List<TSD.Drawing>();
        var enumerator = handler.GetDrawings();
        while (enumerator.MoveNext())
        {
            var drawing = enumerator.Current;
            if (drawing == null) continue;
            var info = MapDrawing(drawing, null);
            if (string.Equals(info.Key, keyOrMark, StringComparison.OrdinalIgnoreCase))
                exactKey.Add(drawing);
            if (string.Equals(info.Mark, keyOrMark, StringComparison.OrdinalIgnoreCase))
                exactMark.Add(drawing);
        }
        if (exactKey.Count > 0) return exactKey.Count == 1 ? exactKey[0] : null;
        return exactMark.Count == 1 ? exactMark[0] : null;
    }

    private static DrawingInfo MapDrawing(TSD.Drawing drawing, TSD.Drawing? active)
    {
        var identifier = GetDrawingIdentifier(drawing);
        var info = new DrawingInfo
        {
            DrawingId = identifier.ID,
            DrawingId2 = identifier.ID2,
            DrawingGuid = SafeGuid(identifier),
            Type = DrawingTypeName(drawing),
            Mark = drawing.Mark ?? "",
            Name = drawing.Name ?? "",
            Title1 = drawing.Title1 ?? "",
            Title2 = drawing.Title2 ?? "",
            Title3 = drawing.Title3 ?? "",
            IsFrozen = drawing.IsFrozen,
            IsIssued = drawing.IsIssued,
            IsIssuedButModified = drawing.IsIssuedButModified,
            IsLocked = drawing.IsLocked,
            IsLockedBy = drawing.IsLockedBy ?? "",
            IsMasterDrawing = drawing.IsMasterDrawing,
            IsReadyForIssue = drawing.IsReadyForIssue,
            IsReadyForIssueBy = drawing.IsReadyForIssueBy ?? "",
            UpToDateStatus = drawing.UpToDateStatus.ToString(),
            CreationDate = NullableDate(drawing.CreationDate),
            ModificationDate = NullableDate(drawing.ModificationDate),
            IssuingDate = NullableDate(drawing.IssuingDate),
            OutputDate = NullableDate(drawing.OutputDate),
            IsActive = active != null && SameDatabaseObject(active, drawing),
        };

        if (drawing is TSD.AssemblyDrawing assembly)
        {
            info.AssociatedModelGuid = SafeGuid(assembly.AssemblyIdentifier);
            info.SheetNumber = assembly.SheetNumber;
        }
        else if (drawing is TSD.SinglePartDrawing single)
        {
            info.AssociatedModelGuid = SafeGuid(single.PartIdentifier);
            info.SheetNumber = single.SheetNumber;
        }
        else if (drawing is TSD.CastUnitDrawing cast)
        {
            info.AssociatedModelGuid = SafeGuid(cast.CastUnitIdentifier);
            info.SheetNumber = cast.SheetNumber;
        }

        try { info.PlotFileName = drawing.GetPlotFileName(false) ?? ""; }
        catch { }
        info.Key = BuildDrawingKey(info);
        return info;
    }

    private static TS.Identifier GetDrawingIdentifier(TSD.DatabaseObject drawing)
    {
        try
        {
            // TODO(windows): live-verify DrawingInternal identifiers on all supported versions.
            return TSDI.DatabaseObjectExtensions.GetIdentifier(drawing);
        }
        catch
        {
            return new TS.Identifier();
        }
    }

    private static string BuildDrawingKey(DrawingInfo info)
    {
        if (info.DrawingId != 0 || info.DrawingId2 != 0)
            return "drawing:" + info.DrawingId.ToString(CultureInfo.InvariantCulture) + ":" +
                   info.DrawingId2.ToString(CultureInfo.InvariantCulture);
        return "drawing:" + EscapeKey(info.Type) + "|" + EscapeKey(info.AssociatedModelGuid) + "|" +
               (info.SheetNumber?.ToString(CultureInfo.InvariantCulture) ?? "") + "|" +
               EscapeKey(info.Mark) + "|" + EscapeKey(info.Name);
    }

    private static string EscapeKey(string value) => (value ?? "")
        .Replace("%", "%25")
        .Replace("|", "%7C")
        .Replace(",", "%2C")
        .Replace(";", "%3B")
        .Replace("\r", "%0D")
        .Replace("\n", "%0A");

    private static bool MatchesDrawing(DrawingInfo info, DrawingQuery query)
    {
        if (query.KeyIn != null && query.KeyIn.Count > 0 &&
            !query.KeyIn.Any(k => string.Equals(k, info.Key, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!string.IsNullOrWhiteSpace(query.Type) &&
            !DrawingTypeMatches(info.Type, query.Type!)) return false;
        if (!ContainsIgnoreCase(info.Mark, query.MarkContains)) return false;
        if (!ContainsIgnoreCase(info.Name, query.NameContains)) return false;
        if (!string.IsNullOrWhiteSpace(query.TitleContains) &&
            !ContainsIgnoreCase(info.Title1 + "\n" + info.Title2 + "\n" + info.Title3, query.TitleContains))
            return false;
        if (!string.IsNullOrWhiteSpace(query.AssociatedModelGuid) &&
            !string.Equals(info.AssociatedModelGuid, query.AssociatedModelGuid, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!ContainsIgnoreCase(info.UpToDateStatus, query.UpToDateStatusContains)) return false;
        if (query.IsIssued.HasValue && info.IsIssued != query.IsIssued.Value) return false;
        if (query.IsLocked.HasValue && info.IsLocked != query.IsLocked.Value) return false;
        if (query.IsReadyForIssue.HasValue && info.IsReadyForIssue != query.IsReadyForIssue.Value) return false;
        return true;
    }

    private static bool ContainsIgnoreCase(string value, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        (value ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool DrawingTypeMatches(string actual, string requested) =>
        string.Equals(actual, NormalizeDrawingType(requested), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDrawingType(string? raw)
    {
        switch ((raw ?? "").Trim().Replace("-", "_").ToLowerInvariant())
        {
            case "assembly":
            case "assembly_drawing":
            case "assemblydrawing": return "AssemblyDrawing";
            case "single":
            case "single_part":
            case "single_part_drawing":
            case "singlepartdrawing": return "SinglePartDrawing";
            case "cast":
            case "cast_unit":
            case "cast_unit_drawing":
            case "castunitdrawing": return "CastUnitDrawing";
            case "ga":
            case "general_arrangement":
            case "general_arrangement_drawing":
            case "gadrawing":
            case "generalarrangementdrawing": return "GeneralArrangementDrawing";
            default: return raw ?? "";
        }
    }

    private static string DrawingTypeName(TSD.Drawing drawing) =>
        drawing is TSD.GADrawing ? "GeneralArrangementDrawing" : drawing.GetType().Name;

    private static bool SameDatabaseObject(TSD.DatabaseObject left, TSD.DatabaseObject right)
    {
        try
        {
            if (left.IsSameDatabaseObject(right)) return true;
        }
        catch
        {
            // Fall through to the best available own identifiers.
        }
        var leftId = GetDrawingIdentifier(left);
        var rightId = GetDrawingIdentifier(right);
        if ((leftId.ID != 0 || leftId.ID2 != 0) &&
            (rightId.ID != 0 || rightId.ID2 != 0))
            return leftId.ID == rightId.ID && leftId.ID2 == rightId.ID2;
        return ReferenceEquals(left, right);
    }

    private static DateTime? NullableDate(DateTime value) =>
        value == DateTime.MinValue ? (DateTime?)null : value;

    private static string SafeGuid(TS.Identifier identifier)
    {
        try
        {
            var guid = identifier.GUID;
            return guid == Guid.Empty ? "" : guid.ToString();
        }
        catch { return ""; }
    }

    private static TeklaIdentifierInfo MapIdentifier(TS.Identifier identifier) =>
        new TeklaIdentifierInfo
        {
            Id = identifier.ID,
            Id2 = identifier.ID2,
            Guid = SafeGuid(identifier),
            Key = (identifier.ID != 0 || identifier.ID2 != 0)
                ? identifier.ID.ToString(CultureInfo.InvariantCulture) + ":" +
                  identifier.ID2.ToString(CultureInfo.InvariantCulture)
                : SafeGuid(identifier),
        };

    private static int NormalizeLimit(int? limit, int hardLimit)
    {
        if (!limit.HasValue) return hardLimit;
        return Math.Max(0, Math.Min(limit.Value, hardLimit));
    }

    private static DrawingWriteResult NewDrawingWriteResult(string operation, bool apply) =>
        new DrawingWriteResult { Operation = operation, Applied = apply, Backend = BackendName };

    private static DrawingInfo PreviewDrawing(DrawingSpec spec) =>
        new DrawingInfo
        {
            Key = "(preview)",
            Type = NormalizeDrawingType(spec.Type),
            Name = spec.Name ?? "",
            AssociatedModelGuid = spec.ModelGuid ?? "",
            SheetNumber = spec.SheetNumber,
        };

    private static TSD.Drawing? CreateDrawing(TSM.Model model, DrawingSpec spec)
    {
        var type = NormalizeDrawingType(spec.Type);
        if (type == "GeneralArrangementDrawing")
        {
            if (!string.IsNullOrWhiteSpace(spec.AttributeFile))
                return new TSD.GADrawing(spec.Name ?? "", spec.AttributeFile);
            return new TSD.GADrawing();
        }

        if (!Guid.TryParse(spec.ModelGuid, out var guid)) return null;
        var identifier = new TS.Identifier(guid);
        var modelObject = model.SelectModelObject(identifier);
        if (modelObject == null) return null;

        if (type == "AssemblyDrawing")
        {
            var assemblyIdentifier = modelObject is TSM.Part part
                ? part.GetAssembly().Identifier
                : modelObject.Identifier;
            if (spec.SheetNumber.HasValue && !string.IsNullOrWhiteSpace(spec.AttributeFile))
                return new TSD.AssemblyDrawing(assemblyIdentifier, spec.SheetNumber.Value, spec.AttributeFile);
            if (spec.SheetNumber.HasValue)
                return new TSD.AssemblyDrawing(assemblyIdentifier, spec.SheetNumber.Value);
            if (!string.IsNullOrWhiteSpace(spec.AttributeFile))
                return new TSD.AssemblyDrawing(assemblyIdentifier, spec.AttributeFile);
            return new TSD.AssemblyDrawing(assemblyIdentifier);
        }

        if (type == "SinglePartDrawing")
        {
            if (spec.SheetNumber.HasValue && !string.IsNullOrWhiteSpace(spec.AttributeFile))
                return new TSD.SinglePartDrawing(modelObject.Identifier, spec.SheetNumber.Value, spec.AttributeFile);
            if (spec.SheetNumber.HasValue)
                return new TSD.SinglePartDrawing(modelObject.Identifier, spec.SheetNumber.Value);
            if (!string.IsNullOrWhiteSpace(spec.AttributeFile))
                return new TSD.SinglePartDrawing(modelObject.Identifier, spec.AttributeFile);
            return new TSD.SinglePartDrawing(modelObject.Identifier);
        }

        if (type == "CastUnitDrawing")
        {
            var castIdentifier = modelObject is TSM.Part part
                ? part.GetAssembly().Identifier
                : modelObject.Identifier;
            if (spec.SheetNumber.HasValue && !string.IsNullOrWhiteSpace(spec.AttributeFile))
                return new TSD.CastUnitDrawing(castIdentifier, spec.SheetNumber.Value, spec.AttributeFile);
            if (spec.SheetNumber.HasValue)
                return new TSD.CastUnitDrawing(castIdentifier, spec.SheetNumber.Value);
            if (!string.IsNullOrWhiteSpace(spec.AttributeFile))
                return new TSD.CastUnitDrawing(castIdentifier, spec.AttributeFile);
            return new TSD.CastUnitDrawing(castIdentifier);
        }
        return null;
    }

    private static void ApplyDrawingModification(TSD.Drawing drawing, DrawingModification modification)
    {
        if (modification.Name != null) drawing.Name = modification.Name;
        if (modification.Title1 != null) drawing.Title1 = modification.Title1;
        if (modification.Title2 != null) drawing.Title2 = modification.Title2;
        if (modification.Title3 != null) drawing.Title3 = modification.Title3;
        if (modification.IsFrozen.HasValue) drawing.IsFrozen = modification.IsFrozen.Value;
        if (modification.IsLocked.HasValue) drawing.IsLocked = modification.IsLocked.Value;
        if (modification.IsMasterDrawing.HasValue) drawing.IsMasterDrawing = modification.IsMasterDrawing.Value;
        if (modification.IsReadyForIssue.HasValue)
            drawing.IsReadyForIssue = modification.IsReadyForIssue.Value;
    }

    private static void StampDrawingOrigin(TSD.DatabaseObject obj, string value)
    {
        try
        {
            if (!obj.SetUserProperty("MCP_ORIGIN", value)) return;
            obj.Modify();
        }
        catch
        {
            // TODO(windows): Drawing UDA support varies by object and environment.
        }
    }

    private static void CommitDrawingChanges(
        TSD.Drawing drawing,
        string message,
        DrawingWriteResult result)
    {
        if (drawing.CommitChanges(message)) return;
        result.Warnings.Add(
            "Tekla returned false from Drawing.CommitChanges after database operations had " +
            "already been attempted. Changes may be partial; inspect the drawing and use Ctrl+Z if needed.");
        throw new InvalidOperationException("Tekla rejected Drawing.CommitChanges().");
    }

    private static string DescribeDrawingSpec(DrawingSpec spec) =>
        NormalizeDrawingType(spec.Type) + " " + (spec.ModelGuid ?? spec.Name ?? "");

    private static string DrawingLabel(TSD.Drawing drawing) =>
        DrawingTypeName(drawing) + " " + (drawing.Mark ?? drawing.Name ?? "");

    private static void PrintOneDrawing(
        TSD.DrawingHandler handler,
        TSD.Drawing drawing,
        DrawingPrintOptions options,
        DrawingWriteResult result)
    {
        var attributes = new TSD.DPMPrinterAttributes
        {
            OutputType = ParseEnum(options.OutputType, TSD.DotPrintOutputType.PDF),
            ColorMode = ParseEnum(options.ColorMode, TSD.DotPrintColor.Color),
            Orientation = ParseEnum(options.Orientation, TSD.DotPrintOrientationType.Auto),
            PaperSize = ParseEnum(options.PaperSize, TSD.DotPrintPaperSize.Auto),
            ScalingMethod = ParseEnum(options.ScalingMethod, TSD.DotPrintScalingType.Auto),
            ScaleFactor = options.ScaleFactor <= 0 ? 1.0 : options.ScaleFactor,
            NumberOfCopies = Math.Max(1, options.NumberOfCopies),
            OpenFileWhenFinished = options.OpenFileWhenFinished,
            PrinterName = options.PrinterName ?? "",
            OutputFileName = options.OutputFile ?? "",
        };

        var outputFile = ResolvePrintOutput(drawing, options);
        bool ok;
        if (!string.IsNullOrWhiteSpace(outputFile))
        {
            if (!options.Overwrite && System.IO.File.Exists(outputFile))
                throw new InvalidOperationException(
                    "Output file already exists and overwrite=false: " + outputFile);
            attributes.OutputFileName = outputFile;
            ok = handler.PrintDrawing(drawing, attributes, outputFile);
            if (ok && !result.OutputFiles.Contains(outputFile))
                result.OutputFiles.Add(outputFile);
        }
        else
        {
            ok = handler.PrintDrawing(drawing, attributes);
        }
        if (!ok) throw new InvalidOperationException("Tekla rejected PrintDrawing().");
    }

    private static string ResolvePrintOutput(TSD.Drawing drawing, DrawingPrintOptions options)
    {
        var template = options.OutputFile ?? "";
        if (string.IsNullOrWhiteSpace(template))
        {
            try { return drawing.GetPlotFileName(false) ?? ""; }
            catch { return ""; }
        }
        return template
            .Replace("{mark}", SafeFileToken(drawing.Mark))
            .Replace("{name}", SafeFileToken(drawing.Name));
    }

    private static string SafeFileToken(string? value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string((value ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static T ParseEnum<T>(string? value, T fallback) where T : struct
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (Enum.TryParse(value ?? "", true, out T parsed)) return parsed;
        throw new ArgumentException(
            "Unknown " + typeof(T).Name + " value '" + value + "'. Use one of: " +
            string.Join(", ", Enum.GetNames(typeof(T))) + ".");
    }
}
