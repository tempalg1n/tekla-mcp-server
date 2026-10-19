using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using TeklaMcp.Core.Rendering;

namespace TeklaMcp.Mock;

/// <summary>
/// Mock half of the visual tools. The schematic scene is built from the synthetic frame exactly
/// like the live backend builds it from Tekla objects (focus, context region, grids), so the
/// picture and legend exercise the same renderer. There is no Tekla window to capture: the
/// "capture" is a schematic stand-in, labelled as such in the picture and in
/// <see cref="ViewCaptureResult.Source"/> — never presented as a screenshot.
/// </summary>
public sealed partial class MockTeklaModelService
{
    private const string MockViewName = "3D";

    private static readonly List<GridAxisLine> MockLevels = new List<GridAxisLine>
    {
        new GridAxisLine { Label = "+0.000", Position = 0 },
        new GridAxisLine { Label = "+3.000", Position = 3000 },
        new GridAxisLine { Label = "+4.000", Position = 4000 },
    };

    public SchematicScene GetSchematicScene(SchematicSceneRequest request)
    {
        request = request ?? new SchematicSceneRequest();
        var query = request.Query ?? new ObjectQuery();
        var scene = new SchematicScene { Backend = BackendName };
        var focusGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxFocus = Math.Max(1, request.MaxFocusObjects);
        var hasScope = SchematicSceneTools.HasScope(query);

        if (hasScope)
        {
            foreach (var guid in query.GuidIn.Where(g => !string.IsNullOrWhiteSpace(g)))
                if (!_objects.Any(o => Eq(o.Guid, guid.Trim())))
                    scene.MissingGuids.Add(guid.Trim());

            var guidOnly = query.GuidIn.Count > 0 && !query.UseSelection;
            scene.ScannedObjects = guidOnly ? 0 : (query.UseSelection ? _selectedObjects.Count : _objects.Count);
            foreach (var obj in FindObjects(query))
            {
                var item = ToSchematicItem(obj);
                if (item != null && request.Region != null)
                {
                    // An explicit region limits the focus too, as on the live backend.
                    var bounds = SchematicSceneTools.BoundsOf(item);
                    if (bounds == null || !SchematicSceneTools.Intersects(bounds, request.Region)) continue;
                }
                scene.FocusMatched++;
                if (focusGuids.Count >= maxFocus)
                {
                    scene.FocusTruncated = true;
                    continue;
                }
                if (item == null)
                {
                    scene.SkippedNoGeometry++;
                    continue;
                }
                item.Focus = true;
                scene.Items.Add(item);
                focusGuids.Add(obj.Guid);
            }
        }

        var focusBox = SchematicSceneTools.BoundsOf(scene.Items);
        var region = request.Region ??
                     (focusBox != null ? SchematicSceneTools.Expand(focusBox, Math.Max(0, request.ContextMarginMm)) : null);
        if (request.IncludeContext && (region != null || !hasScope))
        {
            var maxContext = Math.Max(0, request.MaxContextObjects);
            var added = 0;
            foreach (var obj in _objects)
            {
                if (focusGuids.Contains(obj.Guid)) continue;
                if (obj.Type == "Bolt" && !request.IncludeBolts) continue;
                var item = ToSchematicItem(obj);
                if (item == null) continue;
                var bounds = SchematicSceneTools.BoundsOf(item);
                if (region != null && (bounds == null || !SchematicSceneTools.Intersects(bounds, region))) continue;
                if (added >= maxContext)
                {
                    scene.ContextTruncated = true;
                    break;
                }
                scene.Items.Add(item);
                added++;
            }
            scene.Region = region;
        }

        if (request.IncludeGrids) scene.Grids.Add(MockGrid());
        return scene;
    }

    public ViewCaptureResult CaptureView(ViewCaptureRequest request)
    {
        request = request ?? new ViewCaptureRequest();
        var result = new ViewCaptureResult
        {
            Backend = BackendName,
            Source = "mock-schematic",
            ViewName = MockViewName,
            ActiveViewName = MockViewName,
            OpenViews = new List<string> { MockViewName },
        };

        if (!string.IsNullOrWhiteSpace(request.ViewName) && !Eq(request.ViewName!.Trim(), MockViewName))
        {
            result.Message = "View '" + request.ViewName + "' is not open. Open views: " + MockViewName + ".";
            return result;
        }

        var guids = request.Guids.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList();
        var hasTargets = guids.Count > 0 || request.UseSelection;
        result.TargetsRequested = request.UseSelection ? _selectedObjects.Count : guids.Count;

        var scene = GetSchematicScene(new SchematicSceneRequest
        {
            Query = hasTargets ? new ObjectQuery { GuidIn = guids, UseSelection = request.UseSelection } : new ObjectQuery(),
            MaxFocusObjects = Math.Max(1, request.MaxTargets),
            IncludeContext = true,
            ContextMarginMm = 1e7, // a real view shows the whole model around the targets
        });
        result.MissingGuids = scene.MissingGuids;
        result.TargetsFound = scene.Items.Count(i => i.Focus);

        var width = Math.Max(320, Math.Min(request.MaxWidth, 1280));
        var render = SchematicRenderer.Render(scene, new SchematicRenderOptions
        {
            View = string.IsNullOrWhiteSpace(request.Direction) ? "iso" : request.Direction!,
            Width = width,
            Height = Math.Max(240, Math.Min(request.MaxHeight, width * 3 / 4)),
            Labels = request.Labels ? "number" : "none",
            ColorBy = "class",
            FitToFocus = hasTargets && request.Zoom,
            ContextColored = !(hasTargets && request.Highlight && request.GhostOthers),
            HighlightColor = hasTargets && request.Highlight ? "#E00000" : null,
            Banner = "MOCK BACKEND: schematic stand-in, not a Tekla screenshot",
        });

        if (!render.Rendered || render.Image == null)
        {
            result.Message = render.Message ?? "Mock rendering failed.";
            return result;
        }

        result.Captured = true;
        result.Image = render.Image;
        result.Width = render.Width;
        result.Height = render.Height;
        result.MimeType = render.Image.MimeType;
        result.Settled = true;
        result.Camera = new ViewCameraInfo
        {
            Location = render.PixelOrigin,
            Direction = render.ViewDirection,
            Up = render.ScreenUp,
            ZoomFactor = render.MmPerPixel,
        };
        foreach (var entry in render.Legend.Where(e => e.Focus))
        {
            result.Legend.Add(new CaptureLegendEntry
            {
                Label = entry.Label,
                Guid = entry.Guid,
                Id = entry.Id,
                Type = entry.Type,
                Name = entry.Name,
                Profile = entry.Profile,
            });
        }
        result.Warnings.Add("Mock backend: this is a schematic drawn from the synthetic model, not a Tekla viewport " +
                            "capture; zoom/highlight/direction are simulated in the drawing and nothing was changed.");
        result.Warnings.AddRange(render.Warnings);
        if (!string.IsNullOrWhiteSpace(request.Format) && !Eq(request.Format!.Trim(), "png"))
            result.Warnings.Add("Mock backend always returns PNG.");
        return result;
    }

    /// <summary>
    /// Stand-in for <c>tekla_capture_drawing</c>: the active mock drawing's sheet and view frames,
    /// drawn with the same crop and pixel mapping (<see cref="DrawingCapturePlan"/>) as the live
    /// backend, under a MOCK BACKEND banner. The mock "editor" shows the whole sheet with a 10 mm
    /// border, at the scale that fits the requested scope into maxWidth × maxHeight.
    /// </summary>
    public DrawingCaptureResult CaptureDrawing(DrawingCaptureRequest request)
    {
        request = request ?? new DrawingCaptureRequest();
        var result = new DrawingCaptureResult { Backend = BackendName, Source = "mock-schematic" };
        var active = ResolveMockDrawing(_activeDrawingKey ?? "");
        if (active == null)
        {
            result.Message = "No drawing is open in the drawing editor. Open one with tekla_open_drawing, then capture again.";
            return result;
        }
        result.DrawingKey = active.Key;
        result.DrawingMark = active.Mark;
        result.DrawingName = active.Name;
        result.DrawingType = active.Type;
        result.WindowTitle = "MOCK " + active.Mark;

        var sheet = GetDrawingSheet();
        var sheetWidth = sheet.Width ?? 841;
        var sheetHeight = sheet.Height ?? 594;
        result.SheetWidth = sheetWidth;
        result.SheetHeight = sheetHeight;

        foreach (var view in GetActiveMockDrawingViews())
        {
            var row = new DrawingCaptureView
            {
                ViewId = view.ViewId,
                ViewId2 = view.ViewId2,
                Name = view.Name,
                Type = view.Type,
                Scale = view.Scale,
            };
            if (DrawingCapturePlan.TryViewFrame(view, out var frameMin, out var frameMax))
            {
                row.FrameMin = frameMin;
                row.FrameMax = frameMax;
            }
            result.Views.Add(row);
        }

        Point3D min, max;
        double margin;
        if (request.ViewId.HasValue)
        {
            var target = result.Views.FirstOrDefault(v =>
                v.ViewId == request.ViewId.Value && (!request.ViewId2.HasValue || v.ViewId2 == request.ViewId2.Value));
            if (target?.FrameMin == null || target.FrameMax == null)
            {
                result.Message = "View " + request.ViewId + " is not on the active drawing. Views: " +
                                 string.Join(", ", result.Views.Select(v => v.ViewId + " '" + v.Name + "'")) + ".";
                return result;
            }
            result.View = target;
            (min, max, margin, result.Scope) = (target.FrameMin, target.FrameMax, request.MarginMm, "view");
        }
        else if (request.RegionMin != null && request.RegionMax != null)
        {
            (min, max, margin, result.Scope) = (request.RegionMin, request.RegionMax, request.MarginMm, "region");
        }
        else
        {
            (min, max, margin, result.Scope) = (new Point3D(0, 0, 0), new Point3D(sheetWidth, sheetHeight, 0),
                DrawingCapturePlan.SheetMarginMm, "sheet");
        }

        const double Border = 10;
        margin = Math.Max(0, margin);
        var maxWidth = Math.Max(160, Math.Min(request.MaxWidth, 2400));
        var maxHeight = Math.Max(120, Math.Min(request.MaxHeight, 2400));
        var scale = Math.Min(4.0, Math.Min(
            maxWidth / Math.Max(1, max.X - min.X + 2 * margin + 1),
            maxHeight / Math.Max(1, max.Y - min.Y + 2 * margin + 1)));
        var mapping = new SheetPixelMapping
        {
            Found = true,
            Method = "mock",
            PixelsPerMm = scale,
            SheetLeftPx = Border * scale,
            SheetTopPx = Border * scale,
            EdgesFound = 4,
        };
        result.SheetMapping = mapping;
        var pictureWidth = (int)Math.Ceiling((sheetWidth + 2 * Border) * scale);
        var pictureHeight = (int)Math.Ceiling((sheetHeight + 2 * Border) * scale);
        var crop = DrawingCapturePlan.ToCrop(mapping, sheetHeight, pictureWidth, pictureHeight, min, max, margin);
        if (crop == null)
        {
            result.Message = "The " + result.Scope + " is outside the sheet shown in the (mock) drawing editor.";
            return result;
        }

        var canvas = new RasterCanvas(crop.Width, crop.Height, Rgb.FromHex(0xD9D9D9));
        double X(double sheetX) => mapping.SheetLeftPx + sheetX * scale - crop.Left;
        double Y(double sheetY) => mapping.SheetTopPx + (sheetHeight - sheetY) * scale - crop.Top;
        var ink = Rgb.FromHex(0x1F3B73);
        canvas.FillRect(X(0), Y(sheetHeight), X(sheetWidth), Y(0), Rgb.FromHex(0xFEFEFB));
        canvas.StrokeRect(X(0), Y(sheetHeight), X(sheetWidth), Y(0), ink);
        foreach (var view in result.Views.Where(v => v.FrameMin != null && v.FrameMax != null))
        {
            canvas.StrokeRect(X(view.FrameMin!.X), Y(view.FrameMax!.Y), X(view.FrameMax.X), Y(view.FrameMin.Y), ink);
            canvas.DrawText((int)X(view.FrameMin.X) + 4, (int)Y(view.FrameMax.Y) + 4,
                view.Name + " 1:" + (view.Scale ?? 1), ink, scale >= 2 ? 2 : 1);
        }
        canvas.FillRect(0, 0, crop.Width, 22, Rgb.FromHex(0xC62828), 0.12);
        canvas.DrawText(6, 3, "MOCK BACKEND: stand-in, not a Tekla drawing", Rgb.FromHex(0xC62828), 2);

        result.Image = new RenderedImage
        {
            Bytes = PngEncoder.Encode(canvas),
            MimeType = "image/png",
            Width = canvas.Width,
            Height = canvas.Height,
        };
        result.Width = canvas.Width;
        result.Height = canvas.Height;
        result.MimeType = "image/png";
        result.Captured = true;
        result.Settled = true;
        result.Clipped = crop.Clipped;
        result.ShownMin = crop.ShownMin;
        result.ShownMax = crop.ShownMax;
        result.PixelToSheet = DrawingCapturePlan.ImageMapping(mapping, sheetHeight, crop, canvas.Width);
        foreach (var view in result.Views.Where(v => v.FrameMin != null && v.FrameMax != null))
        {
            view.Pixels = DrawingCapturePlan.ToImage(result.PixelToSheet, canvas.Width, canvas.Height, view.FrameMin!, view.FrameMax!);
            view.OnPicture = view.Pixels != null;
        }
        if (crop.Clipped)
            result.Warnings.Add("Part of the requested " + result.Scope + " lies outside the sheet shown in the (mock) editor.");
        result.Warnings.Add("Mock backend: a stand-in drawn from the mock drawing's sheet size and view frames, not a capture " +
                            "of Tekla's drawing editor; no drawing content (parts, dimensions, marks) is shown.");
        if (!string.IsNullOrWhiteSpace(request.Format) && !Eq(request.Format.Trim(), "png"))
            result.Warnings.Add("Mock backend always returns PNG.");
        return result;
    }

    private SchematicItem? ToSchematicItem(ModelObjectInfo obj)
    {
        var item = new SchematicItem
        {
            Guid = obj.Guid,
            Id = obj.Id,
            Type = obj.Type,
            Name = obj.Name,
            Profile = obj.Profile,
            Material = obj.Material,
            Class = obj.Class,
            AssemblyPos = obj.AssemblyPos,
        };

        switch (obj.Type)
        {
            case "PolyBeam" when obj.Profile == ArchProfile:
                item.Shape = "line";
                item.GeometrySource = "centerline";
                item.Points = ArchPolyline(24);
                return item;

            case "Beam" when obj.StartX.HasValue && obj.EndX.HasValue:
                item.Shape = "line";
                item.GeometrySource = "reference-line";
                item.Points = new List<Point3D>
                {
                    new Point3D(obj.StartX!.Value, obj.StartY ?? 0, obj.StartZ ?? 0),
                    new Point3D(obj.EndX!.Value, obj.EndY ?? 0, obj.EndZ ?? 0),
                };
                return item;

            case "ContourPlate" when obj.CenterX.HasValue:
            {
                // The synthetic base plates only carry a centre; draw their 400×400 outline.
                double cx = obj.CenterX!.Value, cy = obj.CenterY ?? 0, cz = obj.CenterZ ?? 0;
                item.Shape = "polygon";
                item.GeometrySource = "contour";
                item.Points = new List<Point3D>
                {
                    new Point3D(cx - 200, cy - 200, cz), new Point3D(cx + 200, cy - 200, cz),
                    new Point3D(cx + 200, cy + 200, cz), new Point3D(cx - 200, cy + 200, cz),
                };
                return item;
            }

            case "Bolt" when obj.CenterX.HasValue:
                item.Shape = "points";
                item.GeometrySource = "bolt-positions";
                item.Points = new List<Point3D> { new Point3D(obj.CenterX!.Value, obj.CenterY ?? 0, obj.CenterZ ?? 0) };
                return item;
        }

        if (obj.MinX.HasValue && obj.MaxX.HasValue)
        {
            item.Shape = "box";
            item.GeometrySource = "solid-aabb";
            item.Box = new Box3D(
                new Point3D(obj.MinX!.Value, obj.MinY ?? 0, obj.MinZ ?? 0),
                new Point3D(obj.MaxX!.Value, obj.MaxY ?? 0, obj.MaxZ ?? 0));
            return item;
        }
        return null;
    }

    /// <summary>The synthetic arch as a polyline on its 5000 mm circle (what GetCenterLine would give).</summary>
    private static List<Point3D> ArchPolyline(int segments)
    {
        var radius = Math.Sqrt(Math.Pow(ArchStart.X - ArchCenter.X, 2) + Math.Pow(ArchStart.Z - ArchCenter.Z, 2));
        var a0 = Math.Atan2(ArchStart.Z - ArchCenter.Z, ArchStart.X - ArchCenter.X);
        var a1 = Math.Atan2(ArchEnd.Z - ArchCenter.Z, ArchEnd.X - ArchCenter.X);
        var points = new List<Point3D>();
        for (var i = 0; i <= segments; i++)
        {
            var a = a0 + (a1 - a0) * i / segments;
            points.Add(new Point3D(
                Math.Round(ArchCenter.X + radius * Math.Cos(a), 2),
                ArchCenter.Y,
                Math.Round(ArchCenter.Z + radius * Math.Sin(a), 2)));
        }
        return points;
    }

    private static SchematicGrid MockGrid()
    {
        var grid = GridMath.FromFlat(Grids);
        grid.Levels.AddRange(MockLevels.Select(l => new GridAxisLine { Label = l.Label, Position = l.Position }));
        return grid;
    }
}
