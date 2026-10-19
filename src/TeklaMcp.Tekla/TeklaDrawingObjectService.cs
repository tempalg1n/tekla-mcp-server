using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using TS = Tekla.Structures;
using TSD = Tekla.Structures.Drawing;
using TSDI = Tekla.Structures.DrawingInternal;
using TSG = Tekla.Structures.Geometry3d;
using TSM = Tekla.Structures.Model;

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

    private const int DrawingEnumeratorFaultLimit = 64;

    // Some drawing objects cannot be materialized over the Tekla remoting channel:
    // e.g. enumerating a DetailMark throws SerializationException because
    // DetailMarkSymbolAttributes is not [Serializable] (observed on Tekla 2023).
    // MoveNext() faults on such objects; skip them instead of failing the whole
    // enumeration, and stop if the enumerator keeps faulting without advancing.
    private static bool SafeMoveNext(IEnumerator enumerator)
    {
        for (var faults = 0; faults < DrawingEnumeratorFaultLimit; faults++)
        {
            try { return enumerator.MoveNext(); }
            catch { }
        }
        return false;
    }

    private static readonly object AutoFetchToggle = new object();

    /// <summary>
    /// The objects of a view or the sheet, read into a list. With
    /// <c>DrawingEnumeratorBase.AutoFetch</c> on, Tekla serializes the owner's whole snapshot in
    /// the enumerator's constructor, so one DetailMark makes <c>GetObjects()</c> ITSELF throw the
    /// SerializationException (live Tekla 2021) — <see cref="SafeMoveNext"/> never gets a chance,
    /// and the listing, edits and deletes of the whole drawing used to fail with it. Such an owner
    /// is read again without AutoFetch, one object per MoveNext, where the faulting objects are
    /// skipped. Connection failures propagate.
    /// </summary>
    private static List<TSD.DrawingObject> ReadOwnedObjects(TSD.ViewBase owner, Type[]? types = null)
    {
        try
        {
            return Drain(types == null ? owner.GetObjects() : owner.GetObjects(types));
        }
        catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex))
        {
            lock (AutoFetchToggle)
            {
                var autoFetch = TSD.DrawingEnumeratorBase.AutoFetch;
                try
                {
                    TSD.DrawingEnumeratorBase.AutoFetch = false;
                    return Drain(types == null ? owner.GetObjects() : owner.GetObjects(types));
                }
                finally
                {
                    TSD.DrawingEnumeratorBase.AutoFetch = autoFetch;
                }
            }
        }
    }

    private static List<TSD.DrawingObject> Drain(TSD.DrawingObjectEnumerator enumerator)
    {
        var objects = new List<TSD.DrawingObject>();
        while (SafeMoveNext(enumerator))
        {
            TSD.DrawingObject? current;
            try { current = enumerator.Current; }
            catch { continue; }
            if (current != null) objects.Add(current);
        }
        return objects;
    }

    private static List<TSD.View> EnumerateViews(
        TSD.Drawing drawing,
        bool recursive = true)
    {
        var result = new List<TSD.View>();
        var sheet = drawing.GetSheet();
        var enumerator = recursive ? sheet.GetAllViews() : sheet.GetViews();
        while (SafeMoveNext(enumerator))
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
            var frame = view.GetAxisAlignedBoundingBox();
            info.FrameMin = ToDto(frame.LowerLeft);
            info.FrameMax = ToDto(frame.UpperRight);
        }
        catch { }
        try
        {
            info.RestrictionMin = ToDto(view.RestrictionBox.MinPoint);
            info.RestrictionMax = ToDto(view.RestrictionBox.MaxPoint);
        }
        catch { }
        try { info.ModelObjectCount = view.GetModelObjects().GetSize(); } catch { }
        try { info.LabelText = ReadViewLabel(view.Attributes.TagsAttributes); } catch { }
        return info;
    }

    private static string ReadViewLabel(TSD.View.ViewMarkTagsAttributes tags)
    {
        var parts = new List<string>();
        foreach (var tag in new[] { tags.TagA1, tags.TagA2, tags.TagA3, tags.TagA4, tags.TagA5 })
        {
            if (tag?.TagContent == null) continue;
            var elements = new List<MarkContentElement>();
            FlattenContent(tag.TagContent, elements);
            var text = MarkText.Compose(elements);
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join(" | ", parts);
    }

    /// <summary>Sets the view label (issue #35): tag A1 = the text, optionally the other tags emptied.</summary>
    private static void ApplyViewLabel(TSD.View view, DrawingViewModification modification)
    {
        var attributes = view.Attributes;
        if (modification.LabelText != null)
        {
            var tags = attributes.TagsAttributes;
            var font = FirstLabelFont(tags.TagA1?.TagContent);
            var content = new TSD.ContainerElement();
            content.Add(new TSD.TextElement(modification.LabelText, new TSD.FontAttributes(
                font?.Color ?? TSD.DrawingColors.Black,
                modification.LabelHeight ?? font?.Height ?? 3.5,
                string.IsNullOrWhiteSpace(modification.LabelFont) ? font?.Name ?? "Arial" : modification.LabelFont,
                font?.Italic ?? false,
                font?.Bold ?? false)));
            tags.TagA1 = new TSD.View.ViewMarkTagAttributes(
                new TSG.Vector(0, 0, 0), TSD.TagLocation.MiddleLevelOfSymbol, TSD.TextAlignment.Center, content);
            if (modification.ClearOtherTags)
            {
                tags.TagA2 = new TSD.View.ViewMarkTagAttributes();
                tags.TagA3 = new TSD.View.ViewMarkTagAttributes();
                tags.TagA4 = new TSD.View.ViewMarkTagAttributes();
                tags.TagA5 = new TSD.View.ViewMarkTagAttributes();
            }
            attributes.TagsAttributes = tags;
        }
        switch ((modification.LabelVertical ?? "").Trim().ToLowerInvariant())
        {
            case "": break;
            case "top": attributes.LabelPositionVertical = TSD.View.VerticalLabelPosition.Top; break;
            case "bottom": attributes.LabelPositionVertical = TSD.View.VerticalLabelPosition.Bottom; break;
            default: throw new ArgumentException("labelVertical must be top or bottom.");
        }
        switch ((modification.LabelHorizontal ?? "").Trim().ToLowerInvariant())
        {
            case "": break;
            case "centered_by_frame": attributes.LabelPositionHorizontal = TSD.View.HorizontalLabelPosition.CenteredByViewFrame; break;
            case "centered_by_restriction_box": attributes.LabelPositionHorizontal = TSD.View.HorizontalLabelPosition.CenteredByViewRestrictionBox; break;
            case "left_by_frame": attributes.LabelPositionHorizontal = TSD.View.HorizontalLabelPosition.LeftAlignedByViewFrame; break;
            case "right_by_frame": attributes.LabelPositionHorizontal = TSD.View.HorizontalLabelPosition.RightAlignedByViewFrame; break;
            case "left_by_restriction_box": attributes.LabelPositionHorizontal = TSD.View.HorizontalLabelPosition.LeftAlignByViewRestrictionBox; break;
            case "right_by_restriction_box": attributes.LabelPositionHorizontal = TSD.View.HorizontalLabelPosition.RightAlignByViewRestrictionBox; break;
            default: throw new ArgumentException("labelHorizontal is not one of the documented positions.");
        }
        view.Attributes = attributes;
    }

    private static TSD.FontAttributes? FirstLabelFont(TSD.ContainerElement? content)
    {
        if (content == null) return null;
        foreach (var element in content)
            if (element is TSD.TextElement text && text.Font != null) return text.Font;
        return null;
    }

    /// <summary>
    /// Renames the section/detail mark named <paramref name="oldName"/> (issue #35). Matches by
    /// mark name because the Open API exposes no mark ↔ view link; refuses an ambiguous match.
    /// Detail marks may not enumerate on Tekla 2021 (SerializationException), so a detail view's
    /// mark can be missed — the warning says so.
    /// </summary>
    private static void RenameLinkedMark(
        IReadOnlyList<TSD.View> views, string oldName, string newName, DrawingWriteResult result)
    {
        var candidates = new List<TSD.DrawingObject>();
        foreach (var view in views)
        {
            try
            {
                foreach (var current in ReadOwnedObjects(
                             view, new[] { typeof(TSD.SectionMark), typeof(TSD.CurvedSectionMark), typeof(TSD.DetailMark) }))
                {
                    switch (current)
                    {
                        case TSD.SectionMarkBase section when section.Attributes?.MarkName == oldName:
                            candidates.Add(section);
                            break;
                        case TSD.DetailMark detail when detail.Attributes?.MarkName == oldName:
                            candidates.Add(detail);
                            break;
                    }
                }
            }
            catch
            {
                // A view whose marks fault keeps the search going.
            }
        }

        if (candidates.Count != 1)
        {
            result.Warnings.Add(candidates.Count == 0
                ? $"No section or detail mark named '{oldName}' was found to rename (detail marks may not be readable on Tekla 2021); rename it by hand."
                : $"{candidates.Count} marks are named '{oldName}'; none was renamed — rename the right one by hand.");
            return;
        }
        var mark = candidates[0];
        if (mark is TSD.SectionMarkBase sectionMark)
        {
            var attributes = sectionMark.Attributes;
            attributes.MarkName = newName;
            sectionMark.Attributes = attributes;
        }
        else if (mark is TSD.DetailMark detailMark)
        {
            var attributes = detailMark.Attributes;
            attributes.MarkName = newName;
            detailMark.Attributes = attributes;
        }
        if (mark.Modify())
            result.Message = Aggregation.Append(result.Message,
                $"Renamed the {mark.GetType().Name} '{oldName}' to '{newName}' with its view.");
        else
            result.Warnings.Add($"Tekla rejected renaming the {mark.GetType().Name} '{oldName}'.");
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
        int? limit,
        int hardLimit = DrawingObjectBatchLimit)
    {
        var records = new List<DrawingObjectRecord>();
        var cap = NormalizeLimit(limit, hardLimit);
        if (cap == 0) return records;
        ResolveModelIdFilter(query);
        var views = EnumerateViews(drawing, query.Recursive);

        List<TSD.DrawingObject>? selected = null;
        if (query.UseSelection)
        {
            selected = new List<TSD.DrawingObject>();
            var enumerator = handler.GetDrawingObjectSelector().GetSelected();
            while (SafeMoveNext(enumerator))
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
            try
            {
                if (!MatchesDrawingObject(record, query)) return false;
            }
            catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex))
            {
                return false; // an object that faults while being read is skipped, not the drawing
            }
            records.Add(record);
            return records.Count >= cap;
        }

        for (var viewIndex = 0; viewIndex < views.Count; viewIndex++)
        {
            var view = views[viewIndex];
            // Views are already enumerated recursively when requested. Reading only direct
            // children here avoids returning a child-view object once via its parent and again
            // via the child itself.
            foreach (var obj in ReadOwnedObjects(view))
                if (TryAdd(obj, view, viewIndex)) return records;
        }

        // Sheet-owned annotations/graphics are not children of a placed View and would
        // otherwise be invisible to list/select/modify/delete. GetObjects() (not
        // GetAllObjects()) avoids walking into the placed views and duplicating their content.
        var sheet = drawing.GetSheet();
        foreach (var obj in ReadOwnedObjects(sheet))
        {
            if (obj is TSD.ViewBase) continue;
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

        var mark = record.Object as TSD.Mark;
        if (query.EmptyMarksOnly && (mark == null || ReadMark(mark).IsEmpty != true)) return false;

        if (!string.IsNullOrWhiteSpace(query.ModelGuid) || query.ModelId.HasValue)
        {
            var identifier = record.Object is TSD.ModelObject modelObject
                ? modelObject.ModelIdentifier
                : mark != null ? ReadMark(mark).Related?.ModelIdentifier : null;
            // Drawing objects carry only the model ID on Tekla 2021 (GUID empty): match either.
            if (identifier == null ||
                !(string.Equals(SafeGuid(identifier), query.ModelGuid, StringComparison.OrdinalIgnoreCase) ||
                  (query.ModelId.HasValue && identifier.ID == query.ModelId.Value)))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(query.TextContains))
        {
            var text = record.Object is TSD.Text textObject ? textObject.TextString ?? ""
                : mark != null ? ReadMark(mark).Text : "";
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
        if (record.Object is TSD.Mark mark)
        {
            var content = ReadMark(mark);
            info.Text = content.Text;
            info.MarkContent = content.Elements;
            info.IsEmptyMark = content.IsEmpty;
            if (content.Related != null)
            {
                info.RelatedObjectType = content.Related.GetType().Name;
                info.ModelId = content.Related.ModelIdentifier.ID;
                info.ModelId2 = content.Related.ModelIdentifier.ID2;
                info.ModelGuid = SafeGuid(content.Related.ModelIdentifier);
            }
        }

        if (query.IncludeGeometry)
        {
            MapDrawingObjectGeometry(record.Object, info);
            if (info.DimensionDistance.HasValue)
                info.DimensionDistancePaperMm = info.DimensionDistance.Value / ViewScale(record.View);
        }
        if (query.IncludeUdas)
            ReadDrawingObjectUdas(record.Object, info.Udas);
        return info;
    }

    private sealed class MarkReading
    {
        public string Text { get; set; } = "";
        public List<MarkContentElement>? Elements { get; set; }
        /// <summary>Null when the content could not be read (never guessed as empty).</summary>
        public bool? IsEmpty { get; set; }
        public TSD.ModelObject? Related { get; set; }
    }

    /// <summary>
    /// A mark's content and the model object it marks (issue #32), best-effort: a mark that
    /// faults during remoting still lists, just without content. Cached per mark object for the
    /// duration of one listing (filtering and mapping read it once).
    /// </summary>
    private static MarkReading ReadMark(TSD.Mark mark)
    {
        lock (MarkReadings)
        {
            if (MarkReadings.TryGetValue(mark, out var cached)) return cached;
        }
        var reading = new MarkReading();
        try
        {
            var content = mark.Attributes?.Content;
            if (content != null)
            {
                reading.Elements = new List<MarkContentElement>();
                FlattenContent(content, reading.Elements);
                // Not GetUnformattedString(): it frames an empty mark as "{\n}\n" (Tekla 2021).
                reading.Text = MarkText.Compose(reading.Elements);
                reading.IsEmpty = reading.Text.Length == 0;
            }
        }
        catch
        {
            // TODO(windows): content of every mark type is unverified; a fault leaves IsEmpty null.
        }
        try
        {
            var related = mark.GetRelatedObjects();
            while (SafeMoveNext(related))
                if (related.Current is TSD.ModelObject modelObject)
                {
                    reading.Related = modelObject;
                    break;
                }
        }
        catch
        {
            // The related object is optional context.
        }
        lock (MarkReadings)
        {
            if (MarkReadings.TryGetValue(mark, out var raced)) return raced;
            MarkReadings.Add(mark, reading);
        }
        return reading;
    }

    /// <summary>Content elements in order; nested containers (frames) are flattened.</summary>
    private static void FlattenContent(TSD.ContainerElement content, List<MarkContentElement> into)
    {
        foreach (var element in content)
        {
            switch (element)
            {
                case TSD.PropertyElement property:
                    into.Add(new MarkContentElement { Kind = "property", Name = property.Name ?? "", Value = property.Value ?? "" });
                    break;
                case TSD.TextElement fixedText:
                    into.Add(new MarkContentElement { Kind = "text", Value = fixedText.Value ?? "" });
                    break;
                case TSD.NewLineElement _:
                    into.Add(new MarkContentElement { Kind = "newline", Value = "" });
                    break;
                case TSD.ContainerElement nested:
                    FlattenContent(nested, into);
                    break;
                case TSD.SymbolElement _:
                    into.Add(new MarkContentElement { Kind = "symbol", Value = "" });
                    break;
                case TSD.ElementBase other:
                    into.Add(new MarkContentElement
                    {
                        Kind = other.GetType().Name,
                        Value = MarkText.StripFraming(other.GetUnformattedString()),
                    });
                    break;
            }
        }
    }

    /// <summary>Per-object cache; entries die with the mark objects of the listing.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TSD.Mark, MarkReading> MarkReadings =
        new System.Runtime.CompilerServices.ConditionalWeakTable<TSD.Mark, MarkReading>();

    /// <summary>
    /// A ModelGuid filter would never match on Tekla 2021, whose drawing objects carry only the
    /// model ID: resolve the GUID to its ID once, through the model.
    /// </summary>
    private static void ResolveModelIdFilter(DrawingObjectQuery query)
    {
        if (query.ModelId.HasValue || !Guid.TryParse(query.ModelGuid, out var guid)) return;
        try
        {
            var mo = GetConnectedModel().SelectModelObject(new TS.Identifier(guid));
            if (mo != null && mo.Identifier.ID != 0) query.ModelId = mo.Identifier.ID;
        }
        catch
        {
            // The GUID comparison still runs.
        }
    }

    /// <summary>
    /// Fills modelGuid for rows that carry only the model ID (Tekla 2021 drawing objects and the
    /// objects their marks point at), so the GUID can go straight into model tools.
    /// </summary>
    private static void ResolveModelGuids(IEnumerable<DrawingObjectInfo> rows)
    {
        TSM.Model? model = null;
        foreach (var row in rows)
        {
            if (row.ModelId == 0 || !string.IsNullOrEmpty(row.ModelGuid)) continue;
            try
            {
                model ??= GetConnectedModel();
                var mo = model.SelectModelObject(new TS.Identifier(row.ModelId));
                if (mo != null && mo.Identifier.GUID != Guid.Empty) row.ModelGuid = mo.Identifier.GUID.ToString();
            }
            catch
            {
                return; // best-effort: the IDs are still there
            }
        }
    }

    private static void AddDistinctPoint(List<Point3D> points, Point3D point)
    {
        if (!points.Any(p => Math.Abs(p.X - point.X) < 0.01 && Math.Abs(p.Y - point.Y) < 0.01 &&
                             Math.Abs(p.Z - point.Z) < 0.01))
            points.Add(point);
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
            else if (obj is TSD.StraightDimensionSet dimensionSet)
            {
                // Points come from the child dimensions; Distance is in view units (issue #26).
                info.DimensionDistance = dimensionSet.Distance;
                try { info.DimensionPlacing = dimensionSet.Attributes.Placing.Placing.ToString(); } catch { }
                var children = dimensionSet.GetObjects();
                while (SafeMoveNext(children))
                {
                    if (!(children.Current is TSD.StraightDimension dimension)) continue;
                    AddDistinctPoint(info.Points, ToDto(dimension.StartPoint));
                    AddDistinctPoint(info.Points, ToDto(dimension.EndPoint));
                    if (info.UpDirection == null) info.UpDirection = ToDto(dimension.UpDirection);
                }
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
        foreach (var spec in safeSpecs)
            if (ViewPlacement.Normalize(spec.PlaceBy) == null)
            {
                result.Message = "placeBy must be origin, frame_lower_left or frame_center.";
                return result;
            }
        if (!apply)
        {
            foreach (var spec in safeSpecs)
                result.ViewPreview.Add(PreviewView(spec, result));
            return result;
        }

        var createdViews = new List<TSD.View>();
        var placements = new List<KeyValuePair<TSD.View, DrawingViewSpec>>();
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
                    var view = CreateOneDrawingView(active, spec, out var createdMark);
                    if (view == null) throw new InvalidOperationException("Tekla rejected view creation.");
                    // Static Create* methods and the GA Insert path have already mutated
                    // the active drawing. Account for that before optional follow-ups.
                    createdViews.Add(view);
                    result.CreatedCount++;
                    if (ViewPlacement.Normalize(spec.PlaceBy) != ViewPlacement.Origin)
                        placements.Add(new KeyValuePair<TSD.View, DrawingViewSpec>(view, spec));
                    if (createdMark != null)
                    {
                        var markIdentifier = GetDrawingIdentifier(createdMark);
                        result.ObjectPreview.Add(new DrawingObjectInfo
                        {
                            Index = -1,
                            ObjectId = markIdentifier.ID,
                            ObjectId2 = markIdentifier.ID2,
                            Type = createdMark.GetType().Name,
                            ViewIndex = spec.SourceViewIndex,
                            ViewId = spec.SourceViewId ?? 0,
                            ViewId2 = spec.SourceViewId2 ?? 0,
                        });
                    }
                    // Tekla 2021 ignores the ViewAttributes handed to CreateSectionView /
                    // CreateDetailView (issue #34): the new view takes the parent's filters and
                    // marks. Load the file now, keeping what creation set.
                    if (createdMark != null && !string.IsNullOrWhiteSpace(spec.AttributeFile))
                    {
                        LoadViewAttributeFile(view, new DrawingViewModification { AttributeFile = spec.AttributeFile });
                        if (!view.Modify())
                            result.Warnings.Add((spec.Type ?? "view") +
                                                ": view was created, but Tekla rejected the attribute file '" +
                                                spec.AttributeFile + "' afterwards.");
                    }
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
                    result.Errors.Add((spec.Type ?? "view") + ": " + DrawingFailure(result, exItem));
                }
            }

            if (result.CreatedCount > 0)
                CommitActiveDrawingChanges(
                    active, "Tekla MCP: create drawing views", result);
            if (placements.Count > 0 && PlaceViewsByFrame(placements, result))
                CommitActiveDrawingChanges(
                    active, "Tekla MCP: place drawing views by frame", result);
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
            result.Message = DrawingFailure(result, ex);
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
            var loaded = new List<KeyValuePair<TSD.View, int?>>();
            var modified = new List<TSD.View>();
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
                    if (!string.IsNullOrWhiteSpace(modification.AttributeFile))
                    {
                        var before = CountViewModelObjects(view);
                        LoadViewAttributeFile(view, modification);
                        loaded.Add(new KeyValuePair<TSD.View, int?>(view, before));
                    }
                    double? oldScale = null;
                    if (modification.Scale.HasValue && modification.KeepMarkOffsets)
                        try { oldScale = view.Attributes.Scale; } catch { }
                    var oldName = view.Name ?? "";
                    ApplyViewModification(view, modification);
                    if (modification.LabelText != null || modification.LabelVertical != null ||
                        modification.LabelHorizontal != null)
                        ApplyViewLabel(view, modification);
                    if (!view.Modify()) throw new InvalidOperationException("Tekla rejected view Modify().");
                    StampDrawingOrigin(view, "mcp:modify_drawing_view");
                    result.ModifiedCount++;
                    modified.Add(view);
                    if (oldScale.HasValue && Math.Abs(oldScale.Value - modification.Scale!.Value) > 1e-9)
                        KeepMarkOffsets(view, oldScale.Value, modification.Scale.Value, result);
                    if (modification.RenameMark && modification.Name != null && modification.Name != oldName &&
                        oldName.Length > 0)
                        RenameLinkedMark(views, oldName, modification.Name, result);
                }
                catch (Exception exItem)
                {
                    result.Errors.Add((view.Name ?? "view") + ": " + DrawingFailure(result, exItem));
                }
            }
            if (apply && (result.ModifiedCount > 0 || result.DeletedCount > 0))
                CommitActiveDrawingChanges(
                    active, "Tekla MCP: modify drawing views", result);
            if (modified.Count > 0)
            {
                // Read the modified views back: the preview above holds their state BEFORE the
                // edit (a new scale or label came back as the old one, live Tekla 2021).
                result.ViewPreview.Clear();
                var after = EnumerateViews(active);
                foreach (var view in modified)
                {
                    view.Select();
                    result.ViewPreview.Add(MapView(view, after.FindIndex(v => SameDatabaseObject(v, view))));
                }
            }
            if (loaded.Count > 0)
            {
                // Did the file's filters take effect? Tekla caches view filters by attribute-file
                // NAME within a session, so an edited .vi.npfilter under the same name may not.
                foreach (var pair in loaded)
                {
                    result.Message = Aggregation.Append(result.Message,
                        $"View '{pair.Key.Name}': {pair.Value?.ToString() ?? "?"} model objects before the attribute file, " +
                        $"{CountViewModelObjects(pair.Key)?.ToString() ?? "?"} after.");
                }
                // Tekla 2021: GetAxisAlignedBoundingBox() stays stale within the call that loaded.
                result.Warnings.Add(
                    "The frames returned here may still be the ones from before the attribute file (Tekla " +
                    "updates them later); re-list the views with tekla_list_drawing_views before laying them out.");
            }
        }
        catch (Exception ex)
        {
            result.Message = DrawingFailure(result, ex);
        }
        return result;
    }

    /// <summary>Preview of a view to create; for frame placement an estimated origin and frame.</summary>
    private static DrawingViewInfo PreviewView(DrawingViewSpec spec, DrawingWriteResult result)
    {
        var info = new DrawingViewInfo
        {
            Index = -1,
            Type = spec.Type ?? "",
            Name = spec.Name ?? "",
            Origin = spec.InsertionPoint,
            Scale = spec.Scale,
        };
        var kind = (spec.Type ?? "").Trim().Replace("-", "_").ToLowerInvariant();
        if ((kind == "section" || kind == "curved_section") && spec.Points != null && spec.Points.Count >= 2)
            result.Message = Aggregation.Append(result.Message,
                SectionViews.Describe(spec.Points[0], spec.Points[spec.Points.Count - 1],
                    spec.DepthUp ?? 0, spec.DepthDown ?? 0));
        if (spec.RestrictionMin == null || spec.RestrictionMax == null || !spec.Scale.HasValue) return info;

        var placeBy = ViewPlacement.Normalize(spec.PlaceBy)!;
        ViewPlacement.EstimateFrame(spec.InsertionPoint, spec.RestrictionMin, spec.RestrictionMax,
            spec.Scale.Value, out var frameMin, out var frameMax);
        if (placeBy != ViewPlacement.Origin)
        {
            var origin = ViewPlacement.OriginForFrame(placeBy, spec.InsertionPoint, spec.InsertionPoint, frameMin, frameMax);
            ViewPlacement.EstimateFrame(origin, spec.RestrictionMin, spec.RestrictionMax,
                spec.Scale.Value, out frameMin, out frameMax);
            info.Origin = origin;
        }
        info.FrameMin = frameMin;
        info.FrameMax = frameMax;
        result.Warnings.Add(
            "frameMin/frameMax in the preview are estimates from the restriction box and scale: Tekla adds " +
            "frame margins (grid labels, ~13–23 mm) that are known only after the insert" +
            (placeBy == ViewPlacement.Origin ? "." : "; with apply the view is moved by its real frame."));
        return info;
    }

    /// <summary>
    /// Second pass for placeBy=frame_* (issue #33): with the views inserted and committed, read
    /// each frame (with its margins) and move the origin so the frame anchor lands on the
    /// requested point. Returns true when a view was moved and needs a commit.
    /// </summary>
    private static bool PlaceViewsByFrame(
        IReadOnlyList<KeyValuePair<TSD.View, DrawingViewSpec>> placements, DrawingWriteResult result)
    {
        var moved = false;
        foreach (var pair in placements)
        {
            var view = pair.Key;
            var spec = pair.Value;
            var placeBy = ViewPlacement.Normalize(spec.PlaceBy)!;
            try
            {
                view.Select();
                var frame = view.GetAxisAlignedBoundingBox();
                var origin = ViewPlacement.OriginForFrame(
                    placeBy, spec.InsertionPoint, ToDto(view.Origin), ToDto(frame.LowerLeft), ToDto(frame.UpperRight));
                view.Origin = ToDrawingPoint(origin);
                if (!view.Modify())
                {
                    result.Warnings.Add((view.Name ?? "view") +
                                        ": created at the model-origin placement; Tekla rejected the move by frame.");
                    continue;
                }
                moved = true;
                if (HasHiddenMarks(view))
                    result.Warnings.Add((view.Name ?? "view") +
                                        ": the view has hidden marks, and Tekla counts them in the frame — a hidden " +
                                        "mark far from its part moves the frame and the label (issue #33).");
                view.Select();
                var placed = view.GetAxisAlignedBoundingBox();
                if (!ViewPlacement.IsPlaced(placeBy, spec.InsertionPoint, ToDto(placed.LowerLeft), ToDto(placed.UpperRight)))
                    result.Warnings.Add((view.Name ?? "view") +
                                        ": the frame changed while moving; check frameMin/frameMax and adjust with tekla_modify_drawing_view.");
            }
            catch (Exception ex)
            {
                // TODO(windows): frame semantics of GetAxisAlignedBoundingBox after Insert are unverified live.
                result.Warnings.Add((view.Name ?? "view") + ": could not place by frame — " + ErrorText.Flatten(ex));
            }
        }
        return moved;
    }

    /// <summary>
    /// Loads a view attributes file (issues #28, #34) and puts back what the file would
    /// overwrite: <c>LoadAttributes</c> + <c>Modify</c> replaces scale, view size, the label
    /// (<c>TagsAttributes</c>) and the Name (Tekla 2021, ~20 detail/section views). The recipe
    /// that worked live: keep them, load, restore, Modify, then the restriction box and Modify
    /// again — the caller's final <c>Modify()</c> stores the restored box and origin.
    /// </summary>
    private static void LoadViewAttributeFile(TSD.View view, DrawingViewModification modification)
    {
        var restriction = view.RestrictionBox;
        var origin = view.Origin;
        var name = view.Name;
        // Load into a NEW object: LoadAttributes rewrites the TagsAttributes instance it holds in
        // place, so a label "kept" by reference came back as the file's label (live Tekla 2021).
        var kept = view.Attributes;
        var scale = kept.Scale;
        var tags = kept.TagsAttributes;
        var labelVertical = kept.LabelPositionVertical;
        var labelHorizontal = kept.LabelPositionHorizontal;

        var attributes = new TSD.View.ViewAttributes();
        if (!attributes.LoadAttributes(modification.AttributeFile))
            throw new InvalidOperationException(
                "Could not load view attributes file '" + modification.AttributeFile + "'.");
        if (modification.KeepScale) attributes.Scale = scale;
        if (modification.KeepLabel)
        {
            attributes.TagsAttributes = tags;
            attributes.LabelPositionVertical = labelVertical;
            attributes.LabelPositionHorizontal = labelHorizontal;
        }
        view.Attributes = attributes;
        if (modification.KeepName) view.Name = name;
        if (!view.Modify())
            throw new InvalidOperationException("Tekla rejected Modify() after loading '" + modification.AttributeFile + "'.");
        view.Select();
        if (modification.KeepRestrictionBox) view.RestrictionBox = restriction;
        if (modification.KeepOrigin) view.Origin = origin;
        if (modification.KeepName) view.Name = name;
    }

    /// <summary>
    /// After a rescale, moves every leader-line mark's insertion point so its paper offset from
    /// the leader start is kept (issue #39). Best-effort per mark; the count goes into the message.
    /// </summary>
    private static void KeepMarkOffsets(TSD.View view, double oldScale, double newScale, DrawingWriteResult result)
    {
        int moved = 0, failed = 0;
        try
        {
            foreach (var current in ReadOwnedObjects(view, new[] { typeof(TSD.Mark) }))
            {
                if (!(current is TSD.Mark mark) || !(mark.Placing is TSD.LeaderLinePlacing leader)) continue;
                try
                {
                    var target = ViewPlacement.RescaleMarkOffset(
                        ToDto(leader.StartPoint), ToDto(mark.InsertionPoint), oldScale, newScale);
                    mark.InsertionPoint = ToDrawingPoint(target);
                    if (mark.Modify()) moved++;
                    else failed++;
                }
                catch
                {
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add((view.Name ?? "view") + ": marks were not repositioned after the rescale — " +
                                ErrorText.Flatten(ex));
            return;
        }
        result.Message = Aggregation.Append(result.Message,
            $"View '{view.Name}': {moved} leader-line mark(s) moved to keep their paper offset at 1:{newScale:0.###}" +
            (failed > 0 ? $"; {failed} could not be moved." : ".") +
            " Dimension lines keep their paper offset by themselves — do not rescale their distances.");
    }

    /// <summary>
    /// Parts' and bolts' model geometry in the view's display coordinates (issue #38): the solid's
    /// own vertices (never the global AABB's corners — wrong through a tilted view) and the bolt
    /// positions, through the same global → view matrix as coordinateSpace=model.
    /// </summary>
    private static List<(DrawingObjectRecord Record, DrawingModelGeometry? Geometry)> WithModelGeometry(
        IReadOnlyList<DrawingObjectRecord> records, DrawingObjectQuery query, int cap)
    {
        var matches = new List<(DrawingObjectRecord Record, DrawingModelGeometry? Geometry)>();
        var model = GetConnectedModel();
        InGlobalWorkPlane(model, () =>
        {
            foreach (var record in records)
            {
                if (matches.Count >= cap) break;
                var geometry = ReadModelGeometry(model, record, query);
                if (ViewGeometry.Matches(geometry, query)) matches.Add((record, geometry));
            }
            return true;
        });
        return matches;
    }

    /// <summary>One part's or bolt group's geometry; null for other objects. The solid is read
    /// only when the name/profile filters pass.</summary>
    private static DrawingModelGeometry? ReadModelGeometry(TSM.Model model, DrawingObjectRecord record, DrawingObjectQuery query)
    {
        if (!(record.Object is TSD.ModelObject drawingObject) || !(record.View is TSD.View view)) return null;
        if (!(drawingObject is TSD.Part) && !(drawingObject is TSD.Bolt)) return null;
        var geometry = new DrawingModelGeometry();
        try
        {
            var toView = GlobalToViewMatrix(view);
            var mo = model.SelectModelObject(drawingObject.ModelIdentifier);
            switch (mo)
            {
                case TSM.Part part:
                    geometry.Name = part.Name ?? "";
                    geometry.Profile = part.Profile?.ProfileString ?? "";
                    if (!ViewGeometry.MatchesText(geometry, query)) break;
                    var assemblyPos = "";
                    if (part.GetReportProperty("ASSEMBLY_POS", ref assemblyPos) && assemblyPos.Length > 0)
                        geometry.AssemblyPos = assemblyPos;
                    var vertices = new List<Point3D>();
                    var faces = part.GetSolid().GetFaceEnumerator();
                    while (faces.MoveNext())
                        if (faces.Current != null)
                            foreach (var loop in FaceLoops(faces.Current))
                                foreach (var vertex in loop)
                                    vertices.Add(ToPoint3D(toView.Transform(ToPoint(vertex))));
                    geometry.Source = "solid vertices";
                    if (ViewGeometry.TryBox(vertices, out var min, out var max))
                    {
                        geometry.Min = min;
                        geometry.Max = max;
                    }
                    break;
                case TSM.BoltGroup bolts:
                    geometry.Name = bolts.BoltStandard ?? "";
                    geometry.BoltSize = bolts.BoltSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!ViewGeometry.MatchesText(geometry, query)) break;
                    geometry.BoltPositions = new List<Point3D>();
                    foreach (var position in bolts.BoltPositions)
                        if (position is TSG.Point point)
                            geometry.BoltPositions.Add(ToPoint3D(toView.Transform(point)));
                    geometry.Source = "bolt positions";
                    if (ViewGeometry.TryBox(geometry.BoltPositions, out var boltMin, out var boltMax))
                    {
                        geometry.Min = boltMin;
                        geometry.Max = boltMax;
                    }
                    break;
                default:
                    geometry.Message = "The model object was not found or is not a part or bolt group.";
                    break;
            }
        }
        catch (Exception ex) when (!ConnectionErrors.IsTeklaConnectionFailure(ex))
        {
            geometry.Message = ErrorText.Flatten(ex);
        }
        return geometry;
    }

    /// <summary>True when the view holds a hidden mark (best-effort; false when it cannot tell).</summary>
    private static bool HasHiddenMarks(TSD.View view)
    {
        try
        {
            foreach (var current in ReadOwnedObjects(view, new[] { typeof(TSD.Mark) }))
                if (current is TSD.Mark mark && mark.Hideable != null && mark.Hideable.IsHidden)
                    return true;
        }
        catch
        {
            // Only a hint for a warning.
        }
        return false;
    }

    private static int? CountViewModelObjects(TSD.View view)
    {
        try { return view.GetModelObjects().GetSize(); }
        catch { return null; }
    }

    private static TSD.View? CreateOneDrawingView(
        TSD.Drawing drawing, DrawingViewSpec spec, out TSD.DrawingObject? createdMark)
    {
        createdMark = null;
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
                if (!TSD.View.CreateSectionView(
                        source, points[0], points[1], insertion,
                        spec.DepthUp ?? 0, spec.DepthDown ?? 0,
                        attributes, markAttributes, out created, out sectionMark))
                    return null;
                createdMark = sectionMark;
                return created;
            case "curved_section":
                if (points.Count < 3) throw new ArgumentException("curved_section requires three points.");
                TSD.CurvedSectionMark curvedMark;
                if (!TSD.View.CreateCurvedSectionView(
                        source, points[0], points[1], points[2], insertion,
                        spec.DepthUp ?? 0, spec.DepthDown ?? 0,
                        attributes, markAttributes, out created, out curvedMark))
                    return null;
                createdMark = curvedMark;
                return created;
            case "detail":
                if (points.Count < 3)
                    throw new ArgumentException("detail requires center, boundary and label points.");
                var detailAttributes = string.IsNullOrWhiteSpace(spec.MarkAttributeFile)
                    ? new TSD.DetailMark.DetailMarkAttributes()
                    : new TSD.DetailMark.DetailMarkAttributes(spec.MarkAttributeFile);
                TSD.DetailMark detailMark;
                if (!TSD.View.CreateDetailView(
                        source, points[0], points[1], points[2], insertion,
                        attributes, detailAttributes, out created, out detailMark))
                    return null;
                createdMark = detailMark;
                return created;
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
