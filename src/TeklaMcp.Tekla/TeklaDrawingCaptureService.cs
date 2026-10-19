using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Core.Rendering;
using TSD = Tekla.Structures.Drawing;
using TSMUI = Tekla.Structures.Model.UI;

namespace TeklaMcp.Tekla;

/// <summary>
/// Live half of <c>tekla_capture_drawing</c>. Read-only: it reads the active drawing, its sheet and
/// view frames through the Drawing API, copies the pixels of the drawing editor's own window
/// (<see cref="TeklaWindowCapture"/>, PrintWindow — never a screen copy) and crops them to the
/// sheet, a view frame or a region located by <see cref="SheetLocator"/>. Nothing in Tekla is
/// zoomed, selected or redrawn: the Open API has no drawing-editor zoom, and moving the user's
/// view is not this tool's business.
///
/// Verified live on Tekla 2021 (model 3155, A2 GA, 2026-10-16): window structure (see
/// <see cref="TeklaWindowCapture"/>), sheet edges found at 2.867 px/mm, both view frames on the
/// drawn views. TODO(windows): the editor window title ("G   [268]") and the drawn sheet edge on
/// 2022–2026 are unverified — <see cref="DrawingCapturePlan.PickEditorWindow"/> falls back to
/// elimination of model-view windows, and a sheet not found degrades to scope "visible".
/// </summary>
public sealed partial class TeklaModelService
{
    public DrawingCaptureResult CaptureDrawing(DrawingCaptureRequest request)
    {
        request = request ?? new DrawingCaptureRequest();
        var result = new DrawingCaptureResult { Backend = BackendName, Source = "tekla-drawing-editor" };
        var clock = Stopwatch.StartNew();
        var handler = GetDrawingHandler(); // no connection = a tool error with the connection diagnosis
        try
        {
            CaptureDrawingCore(handler, request, result);
        }
        catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex))
        {
            result.Captured = false;
            result.Image = null;
            result.Message = ErrorText.Flatten(ex);
        }
        result.CaptureMilliseconds = clock.ElapsedMilliseconds;
        return result;
    }

    private static void CaptureDrawingCore(TSD.DrawingHandler handler, DrawingCaptureRequest request, DrawingCaptureResult result)
    {
        // --- drawing, sheet, views (Drawing API) ---------------------------------------------
        var drawing = TryGetActiveDrawing(handler);
        if (drawing == null)
        {
            result.Message = "No drawing is open in Tekla's drawing editor. Ask the user to open the drawing (or open " +
                             "it with tekla_open_drawing once they agree), then capture again.";
            return;
        }
        try
        {
            var info = MapDrawing(drawing, drawing);
            result.DrawingKey = info.Key;
            result.DrawingMark = info.Mark;
            result.DrawingName = info.Name;
            result.DrawingType = info.Type;
        }
        catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex))
        {
            result.Warnings.Add("The drawing's identity could not be read: " + ErrorText.Flatten(ex));
        }

        var sheet = drawing.GetSheet();
        var sheetWidth = sheet.Width;
        var sheetHeight = sheet.Height;
        result.SheetWidth = Math.Round(sheetWidth, 3);
        result.SheetHeight = Math.Round(sheetHeight, 3);
        try
        {
            var lowerLeft = sheet.GetAxisAlignedBoundingBox().LowerLeft;
            if (Math.Abs(lowerLeft.X) > 0.5 || Math.Abs(lowerLeft.Y) > 0.5)
                result.Warnings.Add("The sheet's lower-left corner is at " + MmText(lowerLeft.X) + "," + MmText(lowerLeft.Y) +
                                    ", not 0,0: sheet coordinates on this picture may be offset by that much.");
        }
        catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex))
        {
            // Only a plausibility check.
        }

        result.Views = ReadCaptureViews(drawing);
        DrawingCaptureView? target = null;
        if (request.ViewId.HasValue)
        {
            target = result.Views.FirstOrDefault(v =>
                v.ViewId == request.ViewId.Value && (!request.ViewId2.HasValue || v.ViewId2 == request.ViewId2.Value));
            if (target == null)
            {
                result.Message = "View " + request.ViewId + (request.ViewId2.HasValue ? ":" + request.ViewId2 : "") +
                                 " is not on the active drawing. Views: " + CaptureViewList(result.Views) + ".";
                return;
            }
            if (target.FrameMin == null || target.FrameMax == null)
            {
                result.Message = "The frame of view " + target.ViewId + " ('" + target.Name + "') could not be read, so it " +
                                 "cannot be located on the picture. Capture the whole sheet instead.";
                return;
            }
            result.View = target;
        }

        // --- which window -------------------------------------------------------------------
        var modelPath = "";
        try { modelPath = GetConnectedModel().GetInfo().ModelPath ?? ""; }
        catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex)) { /* only picks the Tekla window */ }

        var window = TeklaWindowCapture.Find(modelPath, out var windowError);
        if (window == null)
        {
            result.Message = windowError;
            return;
        }
        if (window.Minimized)
        {
            result.Message = "The Tekla window is minimized. Ask the user to restore it, then capture again.";
            return;
        }
        if (window.Views.Count == 0)
        {
            result.Message = windowError.Length > 0 ? windowError : "No document windows were found inside Tekla.";
            return;
        }

        var modelViews = new List<string>();
        try
        {
            var visible = TSMUI.ViewHandler.GetVisibleViews();
            while (visible.MoveNext())
                if (visible.Current != null) modelViews.Add(visible.Current.Name ?? "");
        }
        catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex))
        {
            // Only the fallback match needs model-view names.
        }
        var index = DrawingCapturePlan.PickEditorWindow(
            window.Views.Select(v => v.Title).ToList(), result.DrawingMark,
            title => modelViews.Any(name => TeklaWindowCapture.TitleMatches(title, name)), out var pickError);
        if (index < 0)
        {
            result.Message = pickError;
            return;
        }
        var editor = window.Views[index];
        result.WindowTitle = editor.Title;
        if (editor.Minimized)
        {
            result.Message = "The drawing window '" + editor.Title + "' is minimized inside Tekla. Ask the user to restore it.";
            return;
        }
        if (editor.Covered)
        {
            result.Message = "The drawing window '" + editor.Title + "' is open but hidden behind '" + editor.CoveredBy +
                             "'. Only a drawing visible on screen can be captured: ask the user to bring it forward.";
            return;
        }

        // --- pixels -------------------------------------------------------------------------
        using (var shot = TeklaWindowCapture.CaptureSettled(
                   editor.Surface, null, request.SettleMilliseconds, 0, out var settled, out _, out var captureError))
        {
            if (shot == null)
            {
                result.Message = captureError;
                return;
            }
            result.Settled = settled;
            if (!settled)
                result.Warnings.Add("The drawing was still being redrawn after " +
                                    TeklaWindowCapture.ClampSettleBudget(request.SettleMilliseconds) +
                                    " ms; the picture may show a redraw in progress.");

            var client = TeklaWindowCapture.ClientArea(editor.Surface);
            client.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
            if (client.Width < 16 || client.Height < 16) client = new Rectangle(0, 0, shot.Width, shot.Height);
            using (var area = shot.Clone(client, PixelFormat.Format24bppRgb))
            {
                TeklaWindowCapture.Fingerprint(area, out var uniform);
                if (uniform)
                    result.Warnings.Add("The captured drawing window is a single colour: the drawing may not be drawn yet.");
                ComposeDrawingPicture(area, sheetWidth, sheetHeight, target, request, result);
            }
        }
    }

    /// <summary>Locate the sheet, crop to the requested scope, encode, and map pixels back to the sheet.</summary>
    private static void ComposeDrawingPicture(
        Bitmap area, double sheetWidth, double sheetHeight, DrawingCaptureView? target,
        DrawingCaptureRequest request, DrawingCaptureResult result)
    {
        var mapping = SheetLocator.Locate(area.Width, area.Height, TeklaWindowCapture.ToRgb(area), sheetWidth, sheetHeight);
        result.SheetMapping = mapping;

        Point3D min, max;
        double margin;
        string scope;
        if (target != null)
        {
            (min, max, margin, scope) = (target.FrameMin!, target.FrameMax!, request.MarginMm, "view");
        }
        else if (request.RegionMin != null && request.RegionMax != null)
        {
            (min, max, margin, scope) = (request.RegionMin, request.RegionMax, request.MarginMm, "region");
        }
        else
        {
            (min, max, margin, scope) = (new Point3D(0, 0, 0), new Point3D(sheetWidth, sheetHeight, 0),
                DrawingCapturePlan.SheetMarginMm, "sheet");
        }

        DrawingCapturePlan.Crop? crop = null;
        if (mapping.Found)
        {
            crop = DrawingCapturePlan.ToCrop(mapping, sheetHeight, area.Width, area.Height, min, max, margin);
            if (crop == null)
            {
                var onScreen = DrawingCapturePlan.ToCrop(mapping, sheetHeight, area.Width, area.Height,
                    new Point3D(0, 0, 0), new Point3D(sheetWidth, sheetHeight, 0), 0);
                result.Message = (scope == "view" ? "The frame of view " + target!.ViewId + " ('" + target.Name + "')" : "The region") +
                                 " is outside the part of the sheet shown in the drawing editor" +
                                 (onScreen == null ? "" : " (" + SheetBoxText(onScreen.ShownMin, onScreen.ShownMax) + " mm)") +
                                 ". Ask the user to pan or zoom out to it, then capture again.";
                return;
            }
        }
        else
        {
            result.Warnings.Add("The sheet could not be located on the picture, so it shows the whole drawing window" +
                                (scope == "sheet" ? "" : " instead of the requested " + scope) +
                                " and pixel->sheet coordinates are unknown. " + mapping.Message);
            scope = "visible";
        }
        result.Scope = scope;

        var rect = crop == null ? new Rectangle(0, 0, area.Width, area.Height) : new Rectangle(crop.Left, crop.Top, crop.Width, crop.Height);
        using (var picture = area.Clone(rect, PixelFormat.Format24bppRgb))
        {
            result.Image = TeklaWindowCapture.Encode(picture, request.MaxWidth, request.MaxHeight, request.Format);
        }
        result.Width = result.Image.Width;
        result.Height = result.Image.Height;
        result.MimeType = result.Image.MimeType;
        result.Captured = true;
        if (crop == null) return;

        result.ShownMin = crop.ShownMin;
        result.ShownMax = crop.ShownMax;
        result.Clipped = crop.Clipped;
        if (crop.Clipped)
            result.Warnings.Add("Part of the requested " + scope + " is outside the drawing window; the picture shows " +
                                SheetBoxText(crop.ShownMin, crop.ShownMax) + " mm of the sheet. Ask the user to pan or zoom out if " +
                                "the rest matters.");
        var map = DrawingCapturePlan.ImageMapping(mapping, sheetHeight, crop, result.Image.Width);
        result.PixelToSheet = map;
        foreach (var view in result.Views)
        {
            if (view.FrameMin == null || view.FrameMax == null) continue;
            view.Pixels = DrawingCapturePlan.ToImage(map, result.Image.Width, result.Image.Height, view.FrameMin, view.FrameMax);
            view.OnPicture = view.Pixels != null;
        }
        if (map.MmPerPixel > 0.4)
            result.Warnings.Add("The picture has " + (1 / map.MmPerPixel).ToString("0.#", CultureInfo.InvariantCulture) +
                                " px per paper mm: small text may be unreadable. Capture one view (viewId) or a region, or " +
                                "ask the user to zoom the drawing editor in (the picture is never sharper than the screen).");
    }

    /// <summary>Every view on the sheet with its frame (paper mm), cheaply: no model-object counts.</summary>
    private static List<DrawingCaptureView> ReadCaptureViews(TSD.Drawing drawing)
    {
        var rows = new List<DrawingCaptureView>();
        foreach (var view in EnumerateViews(drawing))
        {
            var identifier = GetOwnViewIdentifier(view);
            var row = new DrawingCaptureView { ViewId = identifier.ID, ViewId2 = identifier.ID2 };
            var frame = new DrawingViewInfo();
            try
            {
                row.Name = view.Name ?? "";
                row.Type = view.ViewType.ToString();
                frame.Origin = ToDto(view.Origin);
                frame.FrameOrigin = ToDto(view.FrameOrigin);
                frame.Width = view.Width;
                frame.Height = view.Height;
            }
            catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex)) { }
            try { row.Scale = view.Attributes.Scale; }
            catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex)) { }
            try
            {
                var box = view.GetAxisAlignedBoundingBox();
                frame.FrameMin = ToDto(box.LowerLeft);
                frame.FrameMax = ToDto(box.UpperRight);
            }
            catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex)) { }
            if (DrawingCapturePlan.TryViewFrame(frame, out var min, out var max))
            {
                row.FrameMin = new Point3D(Math.Round(min.X, 2), Math.Round(min.Y, 2), 0);
                row.FrameMax = new Point3D(Math.Round(max.X, 2), Math.Round(max.Y, 2), 0);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static string CaptureViewList(List<DrawingCaptureView> views) =>
        views.Count == 0 ? "none" : string.Join(", ", views.Select(v => v.ViewId + (v.ViewId2 != 0 ? ":" + v.ViewId2 : "") + " '" + v.Name + "'"));

    private static string SheetBoxText(Point3D min, Point3D max) =>
        MmText(min.X) + "," + MmText(min.Y) + " - " + MmText(max.X) + "," + MmText(max.Y);

    private static string MmText(double value) => Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
}
