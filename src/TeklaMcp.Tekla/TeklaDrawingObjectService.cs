using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TS = Tekla.Structures;
using TSD = Tekla.Structures.Drawing;
using TSDI = Tekla.Structures.DrawingInternal;
using TSG = Tekla.Structures.Geometry3d;

namespace TeklaMcp.Tekla;

public sealed partial class TeklaModelService
{
    private sealed class DrawingObjectRecord
    {
        public TSD.DrawingObject Object { get; set; } = null!;
        public TSD.ViewBase View { get; set; } = null!;
        public int Index { get; set; }
        public int ViewIndex { get; set; }
    }

    private static List<TSD.View> EnumerateViews(
        TSD.Drawing drawing,
        bool recursive = true)
    {
        var result = new List<TSD.View>();
        var sheet = drawing.GetSheet();
        var enumerator = recursive ? sheet.GetAllViews() : sheet.GetViews();
        while (enumerator.MoveNext())
            if (enumerator.Current is TSD.View view)
                result.Add(view);
        return result;
    }

    private static DrawingViewInfo MapView(TSD.View view, int index)
    {
        var identifier = GetOwnViewIdentifier(view);
        var info = new DrawingViewInfo
        {
            Index = index,
            ViewId = identifier.ID,
            ViewId2 = identifier.ID2,
            Type = view.ViewType.ToString(),
            Name = view.Name ?? "",
            Origin = ToDto(view.Origin),
            FrameOrigin = ToDto(view.FrameOrigin),
            Width = Math.Round(view.Width, 3),
            Height = Math.Round(view.Height, 3),
            ViewCoordinateSystem = ToDto(view.ViewCoordinateSystem),
            DisplayCoordinateSystem = ToDto(view.DisplayCoordinateSystem),
        };
        try { info.Scale = view.Attributes.Scale; } catch { }
        try
        {
            info.RestrictionMin = ToDto(view.RestrictionBox.MinPoint);
            info.RestrictionMax = ToDto(view.RestrictionBox.MaxPoint);
        }
        catch { }
        try { info.ModelObjectCount = view.GetModelObjects().GetSize(); } catch { }
        return info;
    }

    private static TS.Identifier GetOwnViewIdentifier(TSD.DatabaseObject view)
    {
        try
        {
            // GetViewIdentifier() means "the containing view", which is not the placed
            // View's own identity. Use the DatabaseObject identifier for addressing views.
            // TODO(windows): live-verify DrawingInternal identifiers on 2021–2026.
            return TSDI.DatabaseObjectExtensions.GetIdentifier(view);
        }
        catch
        {
            return new TS.Identifier();
        }
    }

    private static CoordinateSystemInfo ToDto(TSG.CoordinateSystem coordinateSystem) =>
        new CoordinateSystemInfo
        {
            Origin = ToDto(coordinateSystem.Origin),
            AxisX = ToDto(coordinateSystem.AxisX),
            AxisY = ToDto(coordinateSystem.AxisY),
        };

    private static Point3D ToDto(TSG.Point point) =>
        new Point3D(
            Math.Round(point.X, 3),
            Math.Round(point.Y, 3),
            Math.Round(point.Z, 3));

    private static Point3D ToDto(TSG.Vector vector) =>
        new Point3D(
            Math.Round(vector.X, 6),
            Math.Round(vector.Y, 6),
            Math.Round(vector.Z, 6));

    private static TSG.Point ToDrawingPoint(Point3D point) =>
        new TSG.Point(point.X, point.Y, point.Z);

    private static TSG.Vector ToDrawingVector(Point3D point) =>
        new TSG.Vector(point.X, point.Y, point.Z);

    private static List<DrawingObjectRecord> GetDrawingObjectRecords(
        TSD.DrawingHandler handler,
        TSD.Drawing drawing,
        DrawingObjectQuery query,
        int? limit)
    {
        var records = new List<DrawingObjectRecord>();
        var cap = NormalizeLimit(limit, DrawingObjectBatchLimit);
        if (cap == 0) return records;
        var views = EnumerateViews(drawing, query.Recursive);

        List<TSD.DrawingObject>? selected = null;
        if (query.UseSelection)
        {
            selected = new List<TSD.DrawingObject>();
            var enumerator = handler.GetDrawingObjectSelector().GetSelected();
            while (enumerator.MoveNext())
                if (enumerator.Current != null)
                    selected.Add(enumerator.Current);
        }

        var globalIndex = 0;
        bool TryAdd(TSD.DrawingObject obj, TSD.ViewBase view, int viewIndex)
        {
            var record = new DrawingObjectRecord
            {
                Object = obj,
                View = view,
                Index = globalIndex++,
                ViewIndex = viewIndex,
            };
            if (selected != null &&
                !selected.Any(o => SameDatabaseObject(o, record.Object))) return false;
            if (!MatchesDrawingObject(record, query)) return false;
            records.Add(record);
            return records.Count >= cap;
        }

        for (var viewIndex = 0; viewIndex < views.Count; viewIndex++)
        {
            var view = views[viewIndex];
            // Views are already enumerated recursively when requested. Reading only direct
            // children here avoids returning a child-view object once via its parent and again
            // via the child itself.
            var enumerator = view.GetObjects();
            while (enumerator.MoveNext())
            {
                var obj = enumerator.Current;
                if (obj == null) continue;
                if (TryAdd(obj, view, viewIndex)) return records;
            }
        }

        // Sheet-owned annotations/graphics are not children of a placed View and would
        // otherwise be invisible to list/select/modify/delete. GetObjects() (not
        // GetAllObjects()) avoids walking into the placed views and duplicating their content.
        var sheet = drawing.GetSheet();
        var sheetObjects = sheet.GetObjects();
        while (sheetObjects.MoveNext())
        {
            var obj = sheetObjects.Current;
            if (obj == null || obj is TSD.ViewBase) continue;
            if (TryAdd(obj, sheet, -1)) return records;
        }
        return records;
    }

    private static bool MatchesDrawingObject(DrawingObjectRecord record, DrawingObjectQuery query)
    {
        if (query.IndexIn != null && query.IndexIn.Count > 0 &&
            !query.IndexIn.Contains(record.Index)) return false;

        if (query.TypeIn != null && query.TypeIn.Count > 0 &&
            !query.TypeIn.Any(type => string.Equals(
                type, record.Object.GetType().Name, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (query.ViewIndex.HasValue && query.ViewIndex.Value != record.ViewIndex) return false;

        if (query.ViewId.HasValue)
        {
            if (query.ViewId.Value == 0 && (!query.ViewId2.HasValue || query.ViewId2.Value == 0))
                return false;
            var viewIdentifier = GetOwnViewIdentifier(record.View);
            if (viewIdentifier.ID != query.ViewId.Value ||
                (query.ViewId2.HasValue && viewIdentifier.ID2 != query.ViewId2.Value))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(query.ModelGuid))
        {
            if (!(record.Object is TSD.ModelObject modelObject) ||
                !string.Equals(SafeGuid(modelObject.ModelIdentifier), query.ModelGuid,
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(query.TextContains))
        {
            var text = record.Object is TSD.Text textObject ? textObject.TextString ?? "" : "";
            if (text.IndexOf(query.TextContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        if (query.ObjectIds != null && query.ObjectIds.Count > 0)
        {
            var validIds = query.ObjectIds
                .Where(id => id.Id != 0 || id.Id2 != 0)
                .ToList();
            if (validIds.Count == 0) return false;
            var objectIdentifier = GetDrawingIdentifier(record.Object);
            if (!validIds.Any(id =>
                    id.Id == objectIdentifier.ID && id.Id2 == objectIdentifier.ID2))
                return false;
        }
        return true;
    }

    private static DrawingObjectInfo MapDrawingObject(
        DrawingObjectRecord record,
        DrawingObjectQuery query)
    {
        var objectIdentifier = GetDrawingIdentifier(record.Object);
        var viewIdentifier = GetOwnViewIdentifier(record.View);
        var info = new DrawingObjectInfo
        {
            Index = record.Index,
            ObjectId = objectIdentifier.ID,
            ObjectId2 = objectIdentifier.ID2,
            ViewIndex = record.ViewIndex,
            ViewId = viewIdentifier.ID,
            ViewId2 = viewIdentifier.ID2,
            ViewName = record.View is TSD.View placedView ? placedView.Name ?? "" : "(sheet)",
            CoordinateSpace = record.ViewIndex < 0 ? "sheet" : "view",
            Type = record.Object.GetType().Name,
        };

        if (record.Object is TSD.ModelObject modelObject)
        {
            info.ModelId = modelObject.ModelIdentifier.ID;
            info.ModelId2 = modelObject.ModelIdentifier.ID2;
            info.ModelGuid = SafeGuid(modelObject.ModelIdentifier);
        }
        if (record.Object is TSD.IHideable hideable)
        {
            try { info.IsHidden = hideable.Hideable.IsHidden; } catch { }
        }
        if (record.Object is TSD.Text text)
            info.Text = text.TextString ?? "";

        if (query.IncludeGeometry)
            MapDrawingObjectGeometry(record.Object, info);
        if (query.IncludeUdas)
            ReadDrawingObjectUdas(record.Object, info.Udas);
        return info;
    }

    private static void MapDrawingObjectGeometry(TSD.DrawingObject obj, DrawingObjectInfo info)
    {
        try
        {
            if (obj is TSD.Text text)
            {
                info.Text = text.TextString ?? "";
                info.Points.Add(ToDto(text.InsertionPoint));
            }
            else if (obj is TSD.Line line)
            {
                info.Points.Add(ToDto(line.StartPoint));
                info.Points.Add(ToDto(line.EndPoint));
                info.Bulge = line.Bulge;
            }
            else if (obj is TSD.Rectangle rectangle)
            {
                info.Points.Add(ToDto(rectangle.StartPoint));
                info.Points.Add(ToDto(rectangle.EndPoint));
            }
            else if (obj is TSD.Circle circle)
            {
                info.Points.Add(ToDto(circle.CenterPoint));
                info.Radius = circle.Radius;
            }
            else if (obj is TSD.Arc arc)
            {
                info.Points.Add(ToDto(arc.StartPoint));
                info.Points.Add(ToDto(arc.EndPoint));
                info.Radius = arc.Radius;
            }
            else if (obj is TSD.Polygon polygon)
            {
                AddPoints(info.Points, polygon.Points);
                info.Bulge = polygon.Bulge;
            }
            else if (obj is TSD.Polyline polyline)
            {
                AddPoints(info.Points, polyline.Points);
                info.Bulge = polyline.Bulge;
            }
            else if (obj is TSD.Cloud cloud)
            {
                AddPoints(info.Points, cloud.Points);
                info.Bulge = cloud.Bulge;
            }
            else if (obj is TSD.MarkBase mark)
            {
                info.Points.Add(ToDto(mark.InsertionPoint));
            }
            else if (obj is TSD.AngleDimension angle)
            {
                info.Points.Add(ToDto(angle.Origin));
                info.Points.Add(ToDto(angle.Point1));
                info.Points.Add(ToDto(angle.Point2));
            }
        }
        catch
        {
            // Geometry is best-effort; a malformed object still appears in the listing.
        }

        try
        {
            if (obj is TSD.IAxisAlignedBoundingBox bounded)
            {
                var box = bounded.GetAxisAlignedBoundingBox();
                var points = new[] { box.LowerLeft, box.LowerRight, box.UpperLeft, box.UpperRight };
                info.BoundingMin = new Point3D(
                    points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
                info.BoundingMax = new Point3D(
                    points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
            }
        }
        catch { }
    }

    private static void AddPoints(ICollection<Point3D> target, TSD.PointList source)
    {
        foreach (var point in source.ToArray()) target.Add(ToDto(point));
    }

    private static void ReadDrawingObjectUdas(
        TSD.DatabaseObject obj,
        IDictionary<string, string> target)
    {
        try
        {
            if (obj.GetStringUserProperties(out var strings))
                foreach (var pair in strings.Take(100)) target[pair.Key] = pair.Value ?? "";
            if (obj.GetIntegerUserProperties(out var integers))
                foreach (var pair in integers.Take(100))
                    target[pair.Key] = pair.Value.ToString(CultureInfo.InvariantCulture);
            if (obj.GetDoubleUserProperties(out var doubles))
                foreach (var pair in doubles.Take(100))
                    target[pair.Key] = pair.Value.ToString("G", CultureInfo.InvariantCulture);
        }
        catch
        {
            // TODO(windows): availability depends on the object type and environment.
        }
    }

    public DrawingWriteResult CreateDrawingViews(
        IReadOnlyList<DrawingViewSpec> specs,
        bool apply)
    {
        var result = NewDrawingWriteResult("create_drawing_views", apply);
        var safeSpecs = (specs ?? new List<DrawingViewSpec>()).Take(DrawingBatchLimit).ToList();
        result.PlannedCount = safeSpecs.Count;
        if (!apply)
        {
            foreach (var spec in safeSpecs)
                result.ViewPreview.Add(new DrawingViewInfo
                {
                    Index = -1,
                    Type = spec.Type ?? "",
                    Name = spec.Name ?? "",
                    Origin = spec.InsertionPoint,
                    Scale = spec.Scale,
                });
            return result;
        }

        var createdViews = new List<TSD.View>();
        try
        {
            var handler = GetDrawingHandler();
            var active = TryGetActiveDrawing(handler);
            if (active == null)
            {
                result.Message = "No active drawing.";
                return result;
            }

            foreach (var spec in safeSpecs)
            {
                try
                {
                    var view = CreateOneDrawingView(active, spec);
                    if (view == null) throw new InvalidOperationException("Tekla rejected view creation.");
                    // Static Create* methods and the GA Insert path have already mutated
                    // the active drawing. Account for that before optional follow-ups.
                    createdViews.Add(view);
                    result.CreatedCount++;
                    if (!string.IsNullOrWhiteSpace(spec.Name))
                    {
                        view.Name = spec.Name;
                        if (!view.Modify())
                            result.Warnings.Add(
                                (spec.Type ?? "view") +
                                ": view was created, but Tekla rejected the requested Name.");
                    }
                    StampDrawingOrigin(view, "mcp:create_drawing_view");
                }
                catch (Exception exItem)
                {
                    result.Errors.Add((spec.Type ?? "view") + ": " + ErrorText.Flatten(exItem));
                }
            }

            if (result.CreatedCount > 0)
                CommitDrawingChanges(
                    active, "Tekla MCP: create drawing views", result);
            var allViews = EnumerateViews(active);
            result.ViewPreview = createdViews.Select(view =>
            {
                var index = allViews.FindIndex(candidate =>
                    SameDatabaseObject(candidate, view));
                return MapView(view, index);
            }).ToList();
        }
        catch (Exception ex)
        {
            result.Message = ErrorText.Flatten(ex);
        }
        return result;
    }

    public DrawingWriteResult ModifyDrawingViews(
        IReadOnlyList<DrawingViewModification> modifications,
        bool apply)
    {
        var result = NewDrawingWriteResult("modify_drawing_views", apply);
        var safe = (modifications ?? new List<DrawingViewModification>())
            .Take(DrawingBatchLimit).ToList();
        try
        {
            var handler = GetDrawingHandler();
            var active = TryGetActiveDrawing(handler);
            if (active == null)
            {
                result.Message = "No active drawing.";
                return result;
            }
            var views = EnumerateViews(active);
            foreach (var modification in safe)
            {
                var view = ResolveView(views, modification.ViewIndex, modification.ViewId, modification.ViewId2);
                if (view == null)
                {
                    result.Errors.Add("View not found: index " + modification.ViewIndex + ".");
                    continue;
                }
                result.PlannedCount++;
                result.ViewPreview.Add(MapView(view, views.IndexOf(view)));
                if (!apply) continue;

                try
                {
                    if (modification.Delete)
                    {
                        if (!view.Delete())
                            throw new InvalidOperationException("Tekla rejected view Delete().");
                        result.DeletedCount++;
                        continue;
                    }
                    ApplyViewModification(view, modification);
                    if (!view.Modify()) throw new InvalidOperationException("Tekla rejected view Modify().");
                    StampDrawingOrigin(view, "mcp:modify_drawing_view");
                    result.ModifiedCount++;
                }
                catch (Exception exItem)
                {
                    result.Errors.Add((view.Name ?? "view") + ": " + ErrorText.Flatten(exItem));
                }
            }
            if (apply && (result.ModifiedCount > 0 || result.DeletedCount > 0))
                CommitDrawingChanges(
                    active, "Tekla MCP: modify drawing views", result);
        }
        catch (Exception ex)
        {
            result.Message = ErrorText.Flatten(ex);
        }
        return result;
    }

    private static TSD.View? CreateOneDrawingView(TSD.Drawing drawing, DrawingViewSpec spec)
    {
        var attributes = string.IsNullOrWhiteSpace(spec.AttributeFile)
            ? new TSD.View.ViewAttributes()
            : new TSD.View.ViewAttributes(spec.AttributeFile);
        if (spec.Scale.HasValue) attributes.Scale = spec.Scale.Value;
        var insertion = ToDrawingPoint(spec.InsertionPoint);
        TSD.View? created;

        switch ((spec.Type ?? "").Trim().Replace("-", "_").ToLowerInvariant())
        {
            case "front":
                return TSD.View.CreateFrontView(drawing, insertion, attributes, out created) ? created : null;
            case "top":
                return TSD.View.CreateTopView(drawing, insertion, attributes, out created) ? created : null;
            case "back":
                return TSD.View.CreateBackView(drawing, insertion, attributes, out created) ? created : null;
            case "bottom":
                return TSD.View.CreateBottomView(drawing, insertion, attributes, out created) ? created : null;
            case "3d":
            case "_3d":
                return TSD.View.Create3dView(drawing, insertion, attributes, out created) ? created : null;
            case "ga_model":
                if (!(drawing is TSD.GADrawing))
                    throw new ArgumentException("ga_model views require an active general-arrangement drawing.");
                if (spec.ViewCoordinateSystem == null ||
                    spec.DisplayCoordinateSystem == null ||
                    spec.RestrictionMin == null ||
                    spec.RestrictionMax == null)
                    throw new ArgumentException(
                        "ga_model requires view/display coordinate systems and restriction bounds.");
                var viewCoordinateSystem = new TSG.CoordinateSystem(
                    ToDrawingPoint(spec.ViewCoordinateSystem.Origin),
                    ToDrawingVector(spec.ViewCoordinateSystem.AxisX),
                    ToDrawingVector(spec.ViewCoordinateSystem.AxisY));
                var displayCoordinateSystem = new TSG.CoordinateSystem(
                    ToDrawingPoint(spec.DisplayCoordinateSystem.Origin),
                    ToDrawingVector(spec.DisplayCoordinateSystem.AxisX),
                    ToDrawingVector(spec.DisplayCoordinateSystem.AxisY));
                var restrictionBox = new TSG.AABB(
                    ToDrawingPoint(spec.RestrictionMin),
                    ToDrawingPoint(spec.RestrictionMax));
                var gaView = string.IsNullOrWhiteSpace(spec.AttributeFile)
                    ? new TSD.View(
                        drawing.GetSheet(), viewCoordinateSystem,
                        displayCoordinateSystem, restrictionBox)
                    : new TSD.View(
                        drawing.GetSheet(), viewCoordinateSystem,
                        displayCoordinateSystem, restrictionBox, spec.AttributeFile);
                gaView.Origin = insertion;
                if (spec.Scale.HasValue)
                {
                    var gaAttributes = gaView.Attributes;
                    gaAttributes.Scale = spec.Scale.Value;
                    gaView.Attributes = gaAttributes;
                }
                // Unlike the static Create* helpers, this GA constructor only creates
                // an in-memory instance.
                return gaView.Insert() ? gaView : null;
        }

        var views = EnumerateViews(drawing);
        var sourceId = spec.SourceViewId.HasValue &&
                       (spec.SourceViewId.Value != 0 || (spec.SourceViewId2 ?? 0) != 0)
            ? spec.SourceViewId
            : null;
        var source = ResolveView(
            views, spec.SourceViewIndex, sourceId, spec.SourceViewId2);
        if (source == null)
            throw new ArgumentException("section/detail views require sourceViewId/sourceViewId2.");
        var points = (spec.Points ?? new List<Point3D>()).Select(ToDrawingPoint).ToList();
        var markAttributes = string.IsNullOrWhiteSpace(spec.MarkAttributeFile)
            ? new TSD.SectionMarkBase.SectionMarkAttributes()
            : new TSD.SectionMarkBase.SectionMarkAttributes(spec.MarkAttributeFile);

        switch ((spec.Type ?? "").Trim().Replace("-", "_").ToLowerInvariant())
        {
            case "section":
                if (points.Count < 2) throw new ArgumentException("section requires two points.");
                TSD.SectionMark sectionMark;
                return TSD.View.CreateSectionView(
                    source, points[0], points[1], insertion,
                    spec.DepthUp ?? 0, spec.DepthDown ?? 0,
                    attributes, markAttributes, out created, out sectionMark)
                    ? created : null;
            case "curved_section":
                if (points.Count < 3) throw new ArgumentException("curved_section requires three points.");
                TSD.CurvedSectionMark curvedMark;
                return TSD.View.CreateCurvedSectionView(
                    source, points[0], points[1], points[2], insertion,
                    spec.DepthUp ?? 0, spec.DepthDown ?? 0,
                    attributes, markAttributes, out created, out curvedMark)
                    ? created : null;
            case "detail":
                if (points.Count < 3)
                    throw new ArgumentException("detail requires center, boundary and label points.");
                var detailAttributes = string.IsNullOrWhiteSpace(spec.MarkAttributeFile)
                    ? new TSD.DetailMark.DetailMarkAttributes()
                    : new TSD.DetailMark.DetailMarkAttributes(spec.MarkAttributeFile);
                TSD.DetailMark detailMark;
                return TSD.View.CreateDetailView(
                    source, points[0], points[1], points[2], insertion,
                    attributes, detailAttributes, out created, out detailMark)
                    ? created : null;
            default:
                throw new ArgumentException(
                    "Unknown view type. Use front, top, back, bottom, 3d, ga_model, section, curved_section or detail.");
        }
    }

    private static TSD.View? ResolveView(
        IReadOnlyList<TSD.View> views,
        int index,
        int? id,
        int? id2)
    {
        if (id.HasValue)
        {
            if (id.Value == 0 && (!id2.HasValue || id2.Value == 0)) return null;
            return views.FirstOrDefault(view =>
            {
                var identifier = GetOwnViewIdentifier(view);
                return identifier.ID == id.Value &&
                       (!id2.HasValue || identifier.ID2 == id2.Value);
            });
        }
        return index >= 0 && index < views.Count ? views[index] : null;
    }

    private static void ApplyViewModification(
        TSD.View view,
        DrawingViewModification modification)
    {
        if (modification.Name != null) view.Name = modification.Name;
        if (modification.Origin != null) view.Origin = ToDrawingPoint(modification.Origin);
        if (modification.Width.HasValue) view.Width = modification.Width.Value;
        if (modification.Height.HasValue) view.Height = modification.Height.Value;
        if (modification.Scale.HasValue)
        {
            var attributes = view.Attributes;
            attributes.Scale = modification.Scale.Value;
            view.Attributes = attributes;
        }
        if (modification.RotateXDegrees.HasValue)
        {
            if (!view.RotateViewOnAxisX(modification.RotateXDegrees.Value))
                throw new InvalidOperationException("Tekla rejected RotateViewOnAxisX().");
        }
        if (modification.RotateYDegrees.HasValue)
        {
            if (!view.RotateViewOnAxisY(modification.RotateYDegrees.Value))
                throw new InvalidOperationException("Tekla rejected RotateViewOnAxisY().");
        }
        if (modification.RotateZDegrees.HasValue)
        {
            if (!view.RotateViewOnAxisZ(modification.RotateZDegrees.Value))
                throw new InvalidOperationException("Tekla rejected RotateViewOnAxisZ().");
        }
        if (modification.RotateOnDrawingPlaneDegrees.HasValue)
        {
            if (!view.RotateViewOnDrawingPlane(modification.RotateOnDrawingPlaneDegrees.Value))
                throw new InvalidOperationException("Tekla rejected RotateViewOnDrawingPlane().");
        }
    }
}
