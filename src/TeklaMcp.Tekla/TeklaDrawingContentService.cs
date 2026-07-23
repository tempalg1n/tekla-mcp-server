using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TS = Tekla.Structures;
using TSD = Tekla.Structures.Drawing;
using TSG = Tekla.Structures.Geometry3d;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla;

public sealed partial class TeklaModelService
{
    private sealed class CreatedDrawingObject
    {
        public TSD.DrawingObject Object { get; set; } = null!;
        public bool AlreadyInserted { get; set; }
    }

    public DrawingWriteResult CreateDrawingObjects(
        IReadOnlyList<DrawingObjectSpec> specs,
        bool apply)
    {
        var result = NewDrawingWriteResult("create_drawing_objects", apply);
        var safeSpecs = (specs ?? new List<DrawingObjectSpec>())
            .Take(DrawingObjectBatchLimit).ToList();
        result.PlannedCount = safeSpecs.Count;
        if (!apply)
        {
            foreach (var spec in safeSpecs)
                result.ObjectPreview.Add(PreviewDrawingObject(spec));
            return result;
        }

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
            var createdRecords = new List<DrawingObjectRecord>();
            foreach (var spec in safeSpecs)
            {
                try
                {
                    var target = ResolveTargetView(active, views, spec);
                    var created = CreateOneDrawingObject(target, spec);
                    if (created.Object == null)
                        throw new InvalidOperationException("Drawing object creation returned null.");
                    if (!created.AlreadyInserted && !created.Object.Insert())
                        throw new InvalidOperationException("Tekla rejected drawing object Insert().");

                    StampDrawingOrigin(created.Object, "mcp:create_drawing_object");
                    createdRecords.Add(new DrawingObjectRecord
                    {
                        Object = created.Object,
                        View = target,
                        Index = -1,
                        ViewIndex = target is TSD.View placed
                            ? views.FindIndex(view => SameDatabaseObject(view, placed))
                            : -1,
                    });
                    result.CreatedCount++;
                }
                catch (Exception exItem)
                {
                    result.Errors.Add((spec.Kind ?? "object") + ": " + ErrorText.Flatten(exItem));
                }
            }

            if (result.CreatedCount > 0)
                CommitDrawingChanges(
                    active, "Tekla MCP: create drawing objects", result);
            result.ObjectPreview = createdRecords
                .Select(record => MapDrawingObject(record, new DrawingObjectQuery()))
                .ToList();
        }
        catch (Exception ex)
        {
            result.Message = ErrorText.Flatten(ex);
        }
        return result;
    }

    public DrawingWriteResult ModifyDrawingObjects(
        DrawingObjectQuery query,
        DrawingObjectModification modification,
        bool apply,
        int? limit = null)
    {
        modification = modification ?? new DrawingObjectModification();
        var result = NewDrawingWriteResult(
            modification.Delete ? "delete_drawing_objects" : "modify_drawing_objects", apply);
        if (!modification.Delete &&
            modification.Text == null &&
            modification.MoveBy == null &&
            string.IsNullOrWhiteSpace(modification.Visibility) &&
            string.IsNullOrWhiteSpace(modification.AttributeFile))
        {
            result.Message = "No drawing-object changes were supplied.";
            return result;
        }
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
            var records = GetDrawingObjectRecords(
                handler, active, effective, limit ?? DrawingObjectBatchLimit);
            result.PlannedCount = records.Count;
            result.ObjectPreview.AddRange(
                records.Take(20).Select(r => MapDrawingObject(r, effective)));
            if (!apply) return result;

            foreach (var record in records)
            {
                try
                {
                    if (modification.Delete)
                    {
                        if (!record.Object.Delete())
                            throw new InvalidOperationException("Tekla rejected Delete().");
                        result.DeletedCount++;
                        continue;
                    }

                    ApplyDrawingObjectModification(record.Object, modification);
                    if (!record.Object.Modify())
                        throw new InvalidOperationException("Tekla rejected Modify().");
                    StampDrawingOrigin(record.Object, "mcp:modify_drawing_object");
                    result.ModifiedCount++;
                }
                catch (Exception exItem)
                {
                    result.Errors.Add(
                        record.Object.GetType().Name + " #" + record.Index + ": " + ErrorText.Flatten(exItem));
                }
            }

            if (result.ModifiedCount > 0 || result.DeletedCount > 0)
                CommitDrawingChanges(
                    active, "Tekla MCP: modify drawing objects", result);
        }
        catch (Exception ex)
        {
            result.Message = ErrorText.Flatten(ex);
        }
        return result;
    }

    public DrawingWriteResult OperateDrawingMarks(
        DrawingObjectQuery query,
        string operation,
        bool apply,
        int? limit = null)
    {
        var op = (operation ?? "").Trim().ToLowerInvariant();
        var result = NewDrawingWriteResult(op + "_drawing_marks", apply);
        try
        {
            var handler = GetDrawingHandler();
            var active = TryGetActiveDrawing(handler);
            if (active == null)
            {
                result.Message = "No active drawing.";
                return result;
            }
            var records = GetDrawingObjectRecords(
                    handler, active, query ?? new DrawingObjectQuery(),
                    limit ?? DrawingObjectBatchLimit)
                .Where(r => r.Object is TSD.MarkBase)
                .ToList();
            result.PlannedCount = records.Count;
            result.ObjectPreview.AddRange(records.Take(20)
                .Select(r => MapDrawingObject(r, query ?? new DrawingObjectQuery())));
            if (!apply) return result;

            var marks = records.Select(r => (TSD.MarkBase)r.Object).ToList();
            if (op == "merge")
            {
                if (marks.Count < 2)
                    throw new ArgumentException("Merge requires at least two compatible marks.");
                List<TSD.MarkBase> merged;
                if (!TSD.Operations.Operation.MergeMarks(marks, out merged))
                    throw new InvalidOperationException("Tekla rejected MergeMarks().");
                result.ModifiedCount = marks.Count;
                result.CreatedCount = merged?.Count ?? 0;
            }
            else if (op == "split")
            {
                if (marks.Count == 0)
                    throw new ArgumentException("No marks matched.");
                if (!TSD.Operations.Operation.SplitMarks(marks))
                    throw new InvalidOperationException("Tekla rejected SplitMarks().");
                result.ModifiedCount = marks.Count;
            }
            else
            {
                throw new ArgumentException("operation must be merge or split.");
            }
            CommitDrawingChanges(
                active, "Tekla MCP: " + op + " drawing marks", result);
        }
        catch (Exception ex)
        {
            result.Message = ErrorText.Flatten(ex);
        }
        return result;
    }

    private static DrawingObjectInfo PreviewDrawingObject(DrawingObjectSpec spec) =>
        new DrawingObjectInfo
        {
            Index = -1,
            ViewIndex = spec.ViewIndex,
            ViewId = spec.ViewId ?? 0,
            ViewId2 = spec.ViewId2 ?? 0,
            Type = NormalizeDrawingObjectKind(spec.Kind),
            ModelGuid = spec.ModelGuid ?? "",
            Text = spec.Text ?? "",
            Points = (spec.Points ?? new List<Point3D>()).ToList(),
            Radius = spec.Radius,
            Bulge = spec.Bulge,
        };

    private static string NormalizeDrawingObjectKind(string? kind)
    {
        switch ((kind ?? "").Trim().Replace("-", "_").ToLowerInvariant())
        {
            case "straight_dimension":
            case "dimension": return "StraightDimensionSet";
            case "angle_dimension": return "AngleDimension";
            case "radius_dimension": return "RadiusDimension";
            case "curved_radial": return "CurvedDimensionSetRadial";
            case "curved_orthogonal": return "CurvedDimensionSetOrthogonal";
            case "level_mark": return "LevelMark";
            default:
                var text = (kind ?? "").Trim();
                return text.Length == 0
                    ? ""
                    : char.ToUpperInvariant(text[0]) + text.Substring(1).ToLowerInvariant();
        }
    }

    private static TSD.ViewBase ResolveTargetView(
        TSD.Drawing drawing,
        IReadOnlyList<TSD.View> views,
        DrawingObjectSpec spec)
    {
        if (spec.ViewIndex == -1 && !spec.ViewId.HasValue)
        {
            if (!string.Equals(spec.CoordinateSpace, "sheet", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "The sheet target requires coordinateSpace=sheet (paper millimetres).");
            return drawing.GetSheet();
        }

        var view = ResolveView(views, spec.ViewIndex, spec.ViewId, spec.ViewId2);
        if (view == null) throw new ArgumentException("Target view not found.");
        if (string.Equals(spec.CoordinateSpace, "sheet", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("sheet coordinates require viewIndex=-1 (the drawing sheet).");
        return view;
    }

    private static CreatedDrawingObject CreateOneDrawingObject(
        TSD.ViewBase target,
        DrawingObjectSpec spec)
    {
        var points = TransformPoints(target, spec);
        var kind = (spec.Kind ?? "").Trim().Replace("-", "_").ToLowerInvariant();
        switch (kind)
        {
            case "text":
                RequirePoints(points, 1, kind);
                return New(new TSD.Text(
                    target, points[0], spec.Text ?? "",
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.Text.TextAttributes()
                        : new TSD.Text.TextAttributes(spec.AttributeFile)));
            case "line":
                RequirePoints(points, 2, kind);
                var line = new TSD.Line(
                    target, points[0], points[1],
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.Line.LineAttributes()
                        : new TSD.Line.LineAttributes(spec.AttributeFile));
                if (spec.Bulge.HasValue) line.Bulge = spec.Bulge.Value;
                return New(line);
            case "rectangle":
                RequirePoints(points, 1, kind);
                TSD.Rectangle rectangle;
                var rectangleAttributes = string.IsNullOrWhiteSpace(spec.AttributeFile)
                    ? new TSD.Rectangle.RectangleAttributes()
                    : new TSD.Rectangle.RectangleAttributes(spec.AttributeFile);
                if (points.Count >= 2)
                    rectangle = new TSD.Rectangle(target, points[0], points[1], rectangleAttributes);
                else if (spec.Width.HasValue && spec.Height.HasValue)
                    rectangle = new TSD.Rectangle(
                        target, points[0], spec.Width.Value, spec.Height.Value, rectangleAttributes);
                else
                    throw new ArgumentException("rectangle requires two points or width+height.");
                if (spec.AngleDegrees.HasValue) rectangle.Angle = spec.AngleDegrees.Value;
                return New(rectangle);
            case "circle":
                RequirePoints(points, 1, kind);
                if (!spec.Radius.HasValue || spec.Radius.Value <= 0)
                    throw new ArgumentException("circle requires radius > 0.");
                return New(new TSD.Circle(
                    target, points[0], spec.Radius.Value,
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.Circle.CircleAttributes()
                        : new TSD.Circle.CircleAttributes(spec.AttributeFile)));
            case "arc":
                RequirePoints(points, 2, kind);
                if (points.Count >= 3)
                    return New(new TSD.Arc(
                        target, points[0], points[1], points[2],
                        string.IsNullOrWhiteSpace(spec.AttributeFile)
                            ? new TSD.Arc.ArcAttributes()
                            : new TSD.Arc.ArcAttributes(spec.AttributeFile)));
                if (!spec.Radius.HasValue || spec.Radius.Value <= 0)
                    throw new ArgumentException("arc requires three points or two points + radius.");
                return New(new TSD.Arc(
                    target, points[0], points[1], spec.Radius.Value,
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.Arc.ArcAttributes()
                        : new TSD.Arc.ArcAttributes(spec.AttributeFile)));
            case "polyline":
                RequirePoints(points, 2, kind);
                var polyline = new TSD.Polyline(
                    target, ToPointList(points),
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.Polyline.PolylineAttributes()
                        : new TSD.Polyline.PolylineAttributes(spec.AttributeFile));
                if (spec.Bulge.HasValue) polyline.Bulge = spec.Bulge.Value;
                return New(polyline);
            case "polygon":
                RequirePoints(points, 3, kind);
                var polygon = new TSD.Polygon(
                    target, ToPointList(points),
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.Polygon.PolygonAttributes()
                        : new TSD.Polygon.PolygonAttributes(spec.AttributeFile));
                if (spec.Bulge.HasValue) polygon.Bulge = spec.Bulge.Value;
                return New(polygon);
            case "cloud":
                RequirePoints(points, 3, kind);
                var cloud = new TSD.Cloud(
                    target, ToPointList(points),
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.Cloud.CloudAttributes()
                        : new TSD.Cloud.CloudAttributes(spec.AttributeFile));
                if (spec.Bulge.HasValue) cloud.Bulge = spec.Bulge.Value;
                return New(cloud);
            case "straight_dimension":
            case "dimension":
                RequirePoints(points, 2, kind);
                var up = spec.UpDirection == null
                    ? new TSG.Vector(0, 1, 0)
                    : ToDrawingVector(spec.UpDirection);
                var straightHandler = new TSD.StraightDimensionSetHandler();
                TSD.StraightDimensionSet straight;
                if (string.IsNullOrWhiteSpace(spec.AttributeFile))
                {
                    straight = straightHandler.CreateDimensionSet(
                        target, ToPointList(points), up, spec.Distance ?? 10);
                }
                else
                {
                    var straightAttributes =
                        new TSD.StraightDimensionSet.StraightDimensionSetAttributes(
                            null, spec.AttributeFile);
                    straight = straightHandler.CreateDimensionSet(
                        target, ToPointList(points), up, spec.Distance ?? 10, straightAttributes);
                }
                if (straight == null) throw new InvalidOperationException("CreateDimensionSet returned null.");
                return New(straight, true);
            case "angle_dimension":
                RequirePoints(points, 3, kind);
                return New(new TSD.AngleDimension(
                    target, points[0], points[1], points[2], spec.Distance ?? 10,
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.AngleDimensionAttributes()
                        : new TSD.AngleDimensionAttributes(spec.AttributeFile)));
            case "radius_dimension":
                RequirePoints(points, 3, kind);
                return New(new TSD.RadiusDimension(
                    target, points[0], points[1], points[2], spec.Distance ?? 10,
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.RadiusDimensionAttributes()
                        : new TSD.RadiusDimensionAttributes(spec.AttributeFile)));
            case "curved_radial":
            case "curved_orthogonal":
                RequirePoints(points, 4, kind);
                var arcPoints = points.Take(3).ToList();
                var dimensionPoints = ToPointList(points.Skip(3));
                var curvedHandler = new TSD.CurvedDimensionSetHandler();
                TSD.DrawingObject? curved;
                if (kind == "curved_radial")
                    curved = string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? curvedHandler.CreateCurvedDimensionSetRadial(
                            target, arcPoints[0], arcPoints[1], arcPoints[2],
                            dimensionPoints, spec.Distance ?? 10)
                        : curvedHandler.CreateCurvedDimensionSetRadial(
                            target, arcPoints[0], arcPoints[1], arcPoints[2],
                            dimensionPoints, spec.Distance ?? 10,
                            new TSD.CurvedDimensionSetRadial.CurvedDimensionSetRadialAttributes(
                                spec.AttributeFile));
                else
                    curved = string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? curvedHandler.CreateCurvedDimensionSetOrthogonal(
                            target, arcPoints[0], arcPoints[1], arcPoints[2],
                            dimensionPoints, spec.Distance ?? 10)
                        : curvedHandler.CreateCurvedDimensionSetOrthogonal(
                            target, arcPoints[0], arcPoints[1], arcPoints[2],
                            dimensionPoints, spec.Distance ?? 10,
                            new TSD.CurvedDimensionSetOrthogonal.CurvedDimensionSetOrthogonalAttributes(
                                spec.AttributeFile));
                if (curved == null) throw new InvalidOperationException("Curved dimension creation returned null.");
                return New(curved, true);
            case "mark":
                if (!(target is TSD.View markView))
                    throw new ArgumentException("mark requires a real drawing view, not the sheet.");
                var modelObject = ResolveDrawingModelObject(markView, spec.ModelGuid);
                var mark = new TSD.Mark(modelObject);
                if (!string.IsNullOrWhiteSpace(spec.AttributeFile))
                    mark.Attributes = new TSD.Mark.MarkAttributes(modelObject, spec.AttributeFile);
                if (points.Count > 0) mark.InsertionPoint = points[0];
                return New(mark);
            case "level_mark":
                RequirePoints(points, 2, kind);
                var levelMark = new TSD.LevelMark(
                    target, points[0], points[1],
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.LevelMark.LevelMarkAttributes()
                        : new TSD.LevelMark.LevelMarkAttributes(spec.AttributeFile));
                if (!string.IsNullOrWhiteSpace(spec.SubType) &&
                    Enum.TryParse(spec.SubType, true, out TSD.LevelMark.LevelMarkType levelType))
                    levelMark.SubType = levelType;
                return New(levelMark);
            case "symbol":
                RequirePoints(points, 1, kind);
                var symbolInfo = string.IsNullOrWhiteSpace(spec.SymbolFile)
                    ? TSD.SymbolInfo.Default
                    : new TSD.SymbolInfo(spec.SymbolFile, spec.SymbolIndex ?? 0);
                return New(new TSD.Symbol(
                    target, points[0], symbolInfo,
                    string.IsNullOrWhiteSpace(spec.AttributeFile)
                        ? new TSD.Symbol.SymbolAttributes()
                        : new TSD.Symbol.SymbolAttributes(spec.AttributeFile)));
            default:
                throw new ArgumentException(
                    "Unknown kind. Use text, line, rectangle, circle, arc, polyline, polygon, cloud, " +
                    "straight_dimension, angle_dimension, radius_dimension, curved_radial, " +
                    "curved_orthogonal, mark, level_mark or symbol.");
        }
    }

    private static CreatedDrawingObject New(TSD.DrawingObject obj, bool alreadyInserted = false) =>
        new CreatedDrawingObject { Object = obj, AlreadyInserted = alreadyInserted };

    private static List<TSG.Point> TransformPoints(TSD.ViewBase target, DrawingObjectSpec spec)
    {
        var result = (spec.Points ?? new List<Point3D>()).Select(ToDrawingPoint).ToList();
        var space = (spec.CoordinateSpace ?? "view").Trim().ToLowerInvariant();
        if (space == "view" || space == "sheet") return result;
        if (space != "model")
            throw new ArgumentException("coordinateSpace must be view, model or sheet.");
        if (!(target is TSD.View view))
            throw new ArgumentException("model coordinates require a real drawing view.");

        var model = GetConnectedModel();
        var workPlaneHandler = model.GetWorkPlaneHandler();
        var previous = workPlaneHandler.GetCurrentTransformationPlane();
        try
        {
            if (!workPlaneHandler.SetCurrentTransformationPlane(new TSM.TransformationPlane()))
                throw new InvalidOperationException(
                    "Could not switch to the global work plane for coordinate conversion.");
            var global = new TSG.CoordinateSystem(
                new TSG.Point(0, 0, 0),
                new TSG.Vector(1, 0, 0),
                new TSG.Vector(0, 1, 0));
            var toView = TSG.MatrixFactory.ByCoordinateSystems(
                global, view.DisplayCoordinateSystem);
            return result.Select(toView.Transform).ToList();
        }
        finally
        {
            workPlaneHandler.SetCurrentTransformationPlane(previous);
        }
    }

    private static void RequirePoints(IReadOnlyCollection<TSG.Point> points, int count, string kind)
    {
        if (points.Count < count)
            throw new ArgumentException(kind + " requires at least " + count + " point(s).");
    }

    private static TSD.PointList ToPointList(IEnumerable<TSG.Point> points)
    {
        var result = new TSD.PointList();
        foreach (var point in points) result.Add(point);
        return result;
    }

    private static TSD.ModelObject ResolveDrawingModelObject(TSD.View view, string modelGuid)
    {
        if (!Guid.TryParse(modelGuid, out var guid))
            throw new ArgumentException("mark requires a valid modelGuid.");
        var enumerator = view.GetModelObjects(new TS.Identifier(guid));
        while (enumerator.MoveNext())
            if (enumerator.Current is TSD.ModelObject modelObject)
                return modelObject;
        throw new InvalidOperationException("The model object is not represented in the target view.");
    }

    private static void ApplyDrawingObjectModification(
        TSD.DrawingObject obj,
        DrawingObjectModification modification)
    {
        if (modification.Text != null)
        {
            if (!(obj is TSD.Text text))
                throw new InvalidOperationException("text can only be changed on Text objects.");
            text.TextString = modification.Text;
        }

        if (modification.MoveBy != null)
        {
            if (!(obj is TSD.IMovableRelative movable))
                throw new InvalidOperationException(obj.GetType().Name + " does not support relative movement.");
            if (!movable.MoveObjectRelative(ToDrawingVector(modification.MoveBy)))
                throw new InvalidOperationException("Tekla rejected MoveObjectRelative().");
        }

        if (!string.IsNullOrWhiteSpace(modification.Visibility))
        {
            if (!(obj is TSD.IHideable hideable))
                throw new InvalidOperationException(obj.GetType().Name + " does not support visibility.");
            switch (modification.Visibility!.Trim().Replace("-", "_").ToLowerInvariant())
            {
                case "drawing": hideable.Hideable.HideFromDrawing(); break;
                case "view": hideable.Hideable.HideFromDrawingView(); break;
                case "show_drawing": hideable.Hideable.ShowInDrawing(); break;
                case "show_view": hideable.Hideable.ShowInDrawingView(); break;
                default:
                    throw new ArgumentException(
                        "visibility must be drawing, view, show_drawing or show_view.");
            }
        }

        if (!string.IsNullOrWhiteSpace(modification.AttributeFile))
        {
            var attributes = obj.Attributes;
            if (!attributes.LoadAttributes(modification.AttributeFile))
                throw new InvalidOperationException(
                    "Could not load attributes file '" + modification.AttributeFile + "'.");
            obj.Attributes = attributes;
        }
    }
}
