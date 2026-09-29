using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Core.Rendering;
using TSG = Tekla.Structures.Geometry3d;
using TSM = Tekla.Structures.Model;
using TSMUI = Tekla.Structures.Model.UI;

namespace TeklaMcp.Tekla;

/// <summary>
/// Live half of <c>tekla_capture_view</c>. Orchestrates the Open API side — which view, camera
/// (<c>ViewCamera</c>), zoom (<c>ViewHandler.ZoomToBoundingBox</c>), temporary colours
/// (<c>ModelObjectVisualization</c>), numbers (<c>GraphicsDrawer.DrawText</c>) — around the pixel
/// copy in <see cref="TeklaWindowCapture"/>, and puts the user's view back afterwards.
///
/// Verified live (Tekla 2023, 2026-09-29): <c>ViewHandler.GetVisibleViews()</c> lists every open
/// view (covered ones too); <c>View.Name</c> is the bare name ("3D") while the window title is
/// "View 4 - 3D"; <c>ViewCamera.DirectionVector</c> is the direction the camera looks along.
/// The mutating calls (camera Modify, ZoomToBoundingBox, temporary states, DrawText and their
/// restore) are TODO(windows) until exercised on a live model — see docs/tekla-api-notes.md.
/// </summary>
public sealed partial class TeklaModelService
{
    private sealed class CameraState
    {
        public TSG.Point Location = new TSG.Point();
        public TSG.Vector Direction = new TSG.Vector();
        public TSG.Vector Up = new TSG.Vector();
        public double ZoomFactor;
        public double FieldOfView;

        public static CameraState From(TSMUI.ViewCamera camera) => new CameraState
        {
            Location = new TSG.Point(camera.Location),
            Direction = new TSG.Vector(camera.DirectionVector),
            Up = new TSG.Vector(camera.UpVector),
            ZoomFactor = camera.ZoomFactor,
            FieldOfView = camera.FieldOfView,
        };

        public void ApplyTo(TSMUI.ViewCamera camera)
        {
            camera.Location = new TSG.Point(Location);
            camera.DirectionVector = new TSG.Vector(Direction);
            camera.UpVector = new TSG.Vector(Up);
            camera.ZoomFactor = ZoomFactor;
            camera.FieldOfView = FieldOfView;
        }
    }

    public ViewCaptureResult CaptureView(ViewCaptureRequest request)
    {
        request = request ?? new ViewCaptureRequest();
        var result = new ViewCaptureResult { Backend = BackendName, Source = "tekla-viewport" };
        var clock = Stopwatch.StartNew();
        try
        {
            var model = GetConnectedModel();
            InGlobalWorkPlane(model, () =>
            {
                CaptureCore(model, request, result);
                return true;
            });
        }
        catch (Exception ex)
        {
            result.Message = ErrorText.Flatten(ex);
        }
        result.CaptureMilliseconds = clock.ElapsedMilliseconds;
        return result;
    }

    private static void CaptureCore(TSM.Model model, ViewCaptureRequest request, ViewCaptureResult result)
    {
        var modelPath = "";
        try { modelPath = model.GetInfo().ModelPath ?? ""; }
        catch { /* the window lookup falls back to the only Tekla window */ }

        // --- which view, which window ------------------------------------------------------
        var views = new List<TSMUI.View>();
        var enumerator = TSMUI.ViewHandler.GetVisibleViews();
        while (enumerator.MoveNext())
            if (enumerator.Current != null) views.Add(enumerator.Current);
        result.OpenViews = views.Select(v => v.Name ?? "").ToList();
        if (views.Count == 0)
        {
            result.Message = "No model view is open in Tekla.";
            return;
        }

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
            result.Message = windowError.Length > 0 ? windowError : "No model-view windows were found inside Tekla.";
            return;
        }

        var activeWindow = window.Active;
        var activeView = activeWindow == null ? null : views.FirstOrDefault(v => TeklaWindowCapture.TitleMatches(activeWindow.Title, v.Name));
        result.ActiveViewName = activeView?.Name ?? activeWindow?.Title;

        TSMUI.View? view;
        TeklaWindowCapture.ViewWindow? viewWindow;
        if (string.IsNullOrWhiteSpace(request.ViewName))
        {
            view = activeView;
            viewWindow = activeWindow;
            if (viewWindow == null)
            {
                result.Message = "Tekla reports no active model view. Pass viewName (open views: " + OpenViewList(result) + ").";
                return;
            }
        }
        else
        {
            var name = request.ViewName!.Trim();
            view = views.FirstOrDefault(v => string.Equals((v.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (view == null)
            {
                result.Message = "View '" + name + "' is not open. Open views: " + OpenViewList(result) + ".";
                return;
            }
            var match = view;
            viewWindow = window.Views
                .Where(w => TeklaWindowCapture.TitleMatches(w.Title, match.Name))
                .OrderByDescending(w => w.Active)
                .FirstOrDefault();
            if (viewWindow == null)
            {
                result.Message = "View '" + name + "' is open in Tekla but its window was not found (window titles: " +
                                 string.Join(", ", window.Views.Select(w => "'" + w.Title + "'")) + ").";
                return;
            }
        }
        result.ViewName = view?.Name ?? viewWindow.Title;

        if (viewWindow.Minimized)
        {
            result.Message = "View '" + result.ViewName + "' is minimized inside Tekla. Ask the user to restore it.";
            return;
        }
        if (viewWindow.Covered)
        {
            result.Message = "View '" + result.ViewName + "' is open but hidden behind '" + viewWindow.CoveredBy +
                             "'. Only a view visible on screen can be captured: ask the user to activate it or to tile " +
                             "the view windows. Active view: '" + result.ActiveViewName + "'.";
            return;
        }

        // --- targets --------------------------------------------------------------------------
        var targets = ResolveCaptureTargets(model, request, result);
        var wantsDirection = !string.IsNullOrWhiteSpace(request.Direction);
        var needsView = wantsDirection || (targets.Count > 0 && (request.Zoom || request.Highlight || request.Labels));
        if (needsView && view == null)
        {
            result.Message = "The window '" + viewWindow.Title + "' could not be matched to a Tekla view, so the camera and " +
                             "colours cannot be changed. Capture without guids/direction, or pass viewName.";
            return;
        }

        ViewProjection? projection = null;
        if (wantsDirection && !ViewProjection.TryCreate(request.Direction, out projection, out var directionError))
        {
            result.Message = directionError;
            return;
        }

        var legendBoxes = new List<(CaptureLegendEntry Entry, Box3D? Box)>();
        var budget = SchematicSolidBudget;
        for (var i = 0; i < targets.Count; i++)
        {
            var (mo, info) = targets[i];
            var item = ReadSchematicItem(mo, info, ref budget);
            var entry = new CaptureLegendEntry
            {
                Label = (i + 1).ToString(CultureInfo.InvariantCulture),
                Guid = info.Guid,
                Id = info.Id,
                Type = info.Type,
                Name = info.Name,
                Profile = info.Profile,
            };
            legendBoxes.Add((entry, item == null ? null : SchematicSceneTools.BoundsOf(item)));
            result.Legend.Add(entry);
        }

        // --- change the view, capture, put it back -------------------------------------------
        TSMUI.ViewCamera? camera = null;
        CameraState? saved = null;
        var ghosted = false;
        Bitmap? shot = null;
        long? baseline = null;
        try
        {
            if (needsView)
            {
                using (var before = TeklaWindowCapture.Capture(viewWindow.Surface, out _))
                    if (before != null) baseline = TeklaWindowCapture.Fingerprint(before, out _);

                camera = new TSMUI.ViewCamera { View = view };
                if (camera.Select()) saved = CameraState.From(camera);
                else result.Warnings.Add("The view camera could not be read; it cannot be restored if it changes.");
            }

            if (projection != null && view != null && camera != null)
            {
                if (view.DisplayType != TSMUI.View.DisplayOrientationType.DISPLAY_3D)
                {
                    result.Warnings.Add("direction applies to 3D views; '" + view.Name + "' is a plane view, so it was ignored.");
                }
                else
                {
                    // TODO(windows): verify that Modify() keeps the target in view after a rotation.
                    camera.DirectionVector = new TSG.Vector(projection.Forward.X, projection.Forward.Y, projection.Forward.Z);
                    camera.UpVector = new TSG.Vector(projection.Up.X, projection.Up.Y, projection.Up.Z);
                    if (camera.Modify()) result.CameraChanged = true;
                    else result.Warnings.Add("Tekla refused the camera rotation (ViewCamera.Modify returned false).");
                }
            }

            var zoomBox = targets.Count > 0 && request.Zoom ? UnionBox(legendBoxes.Select(l => l.Box)) : null;
            if (zoomBox == null && result.CameraChanged && view?.WorkArea != null)
                zoomBox = new Box3D(ToPoint3D(view.WorkArea.MinPoint), ToPoint3D(view.WorkArea.MaxPoint));
            if (zoomBox != null && view != null)
            {
                if (targets.Count > 0 && request.Zoom)
                {
                    var size = Math.Max(zoomBox.Max.X - zoomBox.Min.X,
                        Math.Max(zoomBox.Max.Y - zoomBox.Min.Y, zoomBox.Max.Z - zoomBox.Min.Z));
                    zoomBox = SchematicSceneTools.Expand(zoomBox, Math.Max(500, size * 0.15));
                }
                if (TSMUI.ViewHandler.ZoomToBoundingBox(view, new TSG.AABB(ToPoint(zoomBox.Min), ToPoint(zoomBox.Max))))
                    result.CameraChanged = true;
                else
                    result.Warnings.Add("Tekla refused ZoomToBoundingBox.");
            }
            else if (targets.Count > 0 && request.Zoom)
            {
                result.Warnings.Add("None of the targets has readable geometry, so the view was not zoomed.");
            }

            if (targets.Count > 0 && request.Highlight)
            {
                // TODO(windows): verify the temporary states survive the redraw after a zoom.
                if (request.GhostOthers)
                    ghosted = TSMUI.ModelObjectVisualization.SetTransparencyForAll(TSMUI.TemporaryTransparency.SEMITRANSPARENT);
                var ids = targets.Select(t => t.Mo.Identifier).ToList();
                if (TSMUI.ModelObjectVisualization.SetTemporaryState(ids, new TSMUI.Color(1.0, 0.0, 0.0, 1.0)))
                    result.HighlightApplied = true;
                else
                    result.Warnings.Add("Tekla refused the temporary highlight (ModelObjectVisualization.SetTemporaryState).");
            }

            if (targets.Count > 0 && request.Labels)
            {
                if (!viewWindow.Active)
                {
                    result.Warnings.Add("Numbers are drawn by Tekla only in the ACTIVE view; '" + result.ViewName +
                                        "' is not active, so no numbers were drawn (the legend still lists the targets).");
                }
                else
                {
                    // TODO(windows): verify DrawText placement and that RedrawView removes it.
                    var drawer = new TSMUI.GraphicsDrawer();
                    var color = new TSMUI.Color(0.75, 0.0, 0.0);
                    foreach (var (entry, box) in legendBoxes)
                    {
                        if (box == null) continue;
                        var center = new TSG.Point((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, (box.Min.Z + box.Max.Z) / 2);
                        if (drawer.DrawText(center, entry.Label, color)) result.LabelsDrawn = true;
                    }
                }
            }

            var changed = result.CameraChanged || result.HighlightApplied || ghosted || result.LabelsDrawn;
            string captureError;
            shot = changed
                ? CaptureSettled(viewWindow.Surface, baseline, request.SettleMilliseconds, result, out captureError)
                : TeklaWindowCapture.Capture(viewWindow.Surface, out captureError);
            if (!changed && shot != null) result.Settled = true;
            if (shot == null)
            {
                result.Message = captureError;
                return;
            }

            TeklaWindowCapture.Fingerprint(shot, out var uniform);
            if (uniform)
                result.Warnings.Add("The captured frame is a single colour: the view may be empty, not yet drawn, or not " +
                                    "rendered by the graphics driver for capture.");

            if (view != null) result.Camera = ReadCamera(view);
            result.Image = TeklaWindowCapture.Encode(shot, request.MaxWidth, request.MaxHeight, request.Format);
            result.Width = result.Image.Width;
            result.Height = result.Image.Height;
            result.MimeType = result.Image.MimeType;
            result.Captured = true;
        }
        finally
        {
            shot?.Dispose();
            if (request.Restore)
                RestoreView(view, camera, saved, ghosted, result);
            else if (result.CameraChanged || result.HighlightApplied || ghosted || result.LabelsDrawn)
                result.Warnings.Add("restore=false: the view stays zoomed/coloured for the user. Temporary colours and " +
                                    "numbers disappear when Tekla redraws the view.");
        }
    }

    private static void RestoreView(
        TSMUI.View? view, TSMUI.ViewCamera? camera, CameraState? saved, bool ghosted, ViewCaptureResult result)
    {
        if (result.HighlightApplied || ghosted)
        {
            try { result.HighlightCleared = TSMUI.ModelObjectVisualization.ClearAllTemporaryStates(); }
            catch (Exception ex) { result.Warnings.Add("Could not clear the temporary colours: " + ErrorText.Flatten(ex)); }
        }
        if (result.CameraChanged && camera != null && saved != null)
        {
            try
            {
                saved.ApplyTo(camera);
                result.CameraRestored = camera.Modify();
                if (!result.CameraRestored) result.Warnings.Add("Tekla refused to restore the camera (ViewCamera.Modify returned false).");
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Could not restore the camera: " + ErrorText.Flatten(ex));
            }
        }
        if (result.LabelsDrawn && view != null)
        {
            try { result.LabelsCleared = TSMUI.ViewHandler.RedrawView(view); }
            catch (Exception ex) { result.Warnings.Add("Could not redraw the view to remove the numbers: " + ErrorText.Flatten(ex)); }
        }
    }

    private static List<(TSM.ModelObject Mo, ModelObjectInfo Info)> ResolveCaptureTargets(
        TSM.Model model, ViewCaptureRequest request, ViewCaptureResult result)
    {
        var targets = new List<(TSM.ModelObject, ModelObjectInfo)>();
        var max = Math.Max(1, request.MaxTargets);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(TSM.ModelObject mo)
        {
            var info = MapBasic(mo);
            if (info == null || !seen.Add(info.Guid)) return;
            if (targets.Count >= max) return;
            targets.Add((mo, info));
        }

        if (request.UseSelection)
        {
            foreach (var mo in Drain(new TSMUI.ModelObjectSelector().GetSelectedObjects()))
            {
                result.TargetsRequested++;
                Add(mo);
            }
        }
        foreach (var guid in request.Guids.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            result.TargetsRequested++;
            var mo = TrySelectObjectByGuid(model, guid);
            if (mo == null) result.MissingGuids.Add(guid);
            else Add(mo);
        }

        result.TargetsFound = targets.Count;
        if (result.TargetsRequested > targets.Count + result.MissingGuids.Count && targets.Count >= max)
            result.Warnings.Add("Only the first " + max + " targets were used (maxTargets).");
        return targets;
    }

    /// <summary>
    /// Capture until Tekla has finished redrawing: two identical frames in a row, which must also
    /// differ from the frame taken before the change — or, if the picture never changes, have
    /// stayed the same for ~1 s. Gives up after <paramref name="budgetMs"/> and keeps the last frame.
    /// </summary>
    private static Bitmap? CaptureSettled(
        IntPtr surface, long? baseline, int budgetMs, ViewCaptureResult result, out string error)
    {
        error = "";
        budgetMs = Math.Max(300, Math.Min(budgetMs, 10000));
        var clock = Stopwatch.StartNew();
        Thread.Sleep(200);
        Bitmap? last = null;
        long? lastHash = null;
        var differsFromBaseline = false;
        while (true)
        {
            var frame = TeklaWindowCapture.Capture(surface, out error);
            if (frame == null)
            {
                last?.Dispose();
                return null;
            }
            var hash = TeklaWindowCapture.Fingerprint(frame, out _);
            if (baseline.HasValue && hash != baseline.Value) differsFromBaseline = true;
            var stable = lastHash.HasValue && hash == lastHash.Value;
            last?.Dispose();
            last = frame;
            lastHash = hash;

            if (stable && (differsFromBaseline || !baseline.HasValue || clock.ElapsedMilliseconds > 1000))
            {
                result.Settled = true;
                break;
            }
            if (clock.ElapsedMilliseconds >= budgetMs) break;
            Thread.Sleep(150);
        }

        if (!result.Settled)
            result.Warnings.Add("The view was still changing after " + budgetMs + " ms; the picture may show a redraw in progress.");
        else if (baseline.HasValue && !differsFromBaseline)
            result.Warnings.Add("The picture did not change after zoom/highlight — the targets may already have been framed, " +
                                "or Tekla did not apply the change.");
        return last;
    }

    private static ViewCameraInfo? ReadCamera(TSMUI.View view)
    {
        try
        {
            var camera = new TSMUI.ViewCamera { View = view };
            if (!camera.Select()) return null;
            return new ViewCameraInfo
            {
                Location = ToPoint3D(camera.Location),
                Direction = RoundVector(camera.DirectionVector),
                Up = RoundVector(camera.UpVector),
                ZoomFactor = Math.Round(camera.ZoomFactor, 4),
                FieldOfView = Math.Round(camera.FieldOfView, 3),
                Perspective = view.IsPerspectiveViewProjection(),
            };
        }
        catch
        {
            return null;
        }
    }

    private static Point3D RoundVector(TSG.Vector v) =>
        new Point3D(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));

    private static Box3D? UnionBox(IEnumerable<Box3D?> boxes)
    {
        Box3D? total = null;
        foreach (var box in boxes)
        {
            if (box == null) continue;
            total = total == null
                ? new Box3D(new Point3D(box.Min.X, box.Min.Y, box.Min.Z), new Point3D(box.Max.X, box.Max.Y, box.Max.Z))
                : new Box3D(
                    new Point3D(Math.Min(total.Min.X, box.Min.X), Math.Min(total.Min.Y, box.Min.Y), Math.Min(total.Min.Z, box.Min.Z)),
                    new Point3D(Math.Max(total.Max.X, box.Max.X), Math.Max(total.Max.Y, box.Max.Y), Math.Max(total.Max.Z, box.Max.Z)));
        }
        return total;
    }

    private static string OpenViewList(ViewCaptureResult result) =>
        result.OpenViews.Count == 0 ? "none" : string.Join(", ", result.OpenViews.Select(v => "'" + v + "'"));
}
