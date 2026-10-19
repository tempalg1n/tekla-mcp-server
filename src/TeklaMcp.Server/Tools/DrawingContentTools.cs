using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>Active-drawing view, annotation, graphic, dimension and mark editing.</summary>
[McpServerToolType]
[Toolset(ToolExposure.Drawings)]
public static class DrawingContentTools
{
    [McpServerTool(Name = "tekla_create_drawing_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a front/top/back/bottom/3D view on an ACTIVE assembly/single-part/" +
                 "cast-unit drawing (not GA). insertionPoint is on the sheet in paper mm. " +
                 "Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingView(
        ITeklaModelService model,
        [Description("front, top, back, bottom or 3d.")] string type,
        [Description("Sheet insertion point 'x,y,z' in paper mm.")] string insertionPoint,
        [Description("Saved view attributes file.")] string attributeFile = "",
        [Description("Optional view name.")] string name = "",
        [Description("Optional view scale.")] double? scale = null,
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null)
    {
        var normalizedType = (type ?? "").Trim().ToLowerInvariant();
        if (normalizedType != "front" && normalizedType != "top" &&
            normalizedType != "back" && normalizedType != "bottom" &&
            normalizedType != "3d" && normalizedType != "_3d")
            return ToolValidationFailure(
                "create_drawing_views", apply,
                "Unknown view type. Use front, top, back, bottom, or 3d.");
        var insertion = ToolHelpers.ParsePoint(insertionPoint);
        if (insertion == null)
            return ToolValidationFailure(
                "create_drawing_views", apply,
                "insertionPoint must use the 'x,y,z' format.");

        return ToolHelpers.Write(model, expectedModelPath, () => model.CreateDrawingViews(new[]
        {
            new DrawingViewSpec
            {
                Type = normalizedType,
                InsertionPoint = insertion,
                AttributeFile = attributeFile ?? "",
                Name = name ?? "",
                Scale = scale,
            },
        }, apply));
    }

    [McpServerTool(Name = "tekla_create_ga_drawing_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a model view on the ACTIVE general-arrangement drawing from explicit " +
                 "global model coordinate systems and a restriction box. insertionPoint is on " +
                 "the sheet in paper mm; restriction bounds are in view coordinates. By default " +
                 "(placeBy=origin) insertionPoint is where the MODEL ORIGIN lands, so a restricted " +
                 "plan can end up far off the sheet; placeBy=frame_lower_left / frame_center puts the " +
                 "view FRAME there instead (moved after the insert, by the real frame with its " +
                 "margins). The result's frameMin/frameMax give the frame on the sheet (in a preview: " +
                 "an estimate without margins). Tekla counts HIDDEN marks in the frame, so a hidden mark " +
                 "far from its part shifts frame and label (a warning says so). Preview unless apply=true.")]
    public static DrawingWriteResult CreateGaDrawingView(
        ITeklaModelService model,
        [Description("Sheet point 'x,y,z' in paper mm; its meaning is set by placeBy.")] string insertionPoint,
        [Description("View-coordinate-system origin in global model coordinates.")] string viewOrigin,
        [Description("View-coordinate-system X axis vector.")] string viewAxisX,
        [Description("View-coordinate-system Y axis vector.")] string viewAxisY,
        [Description("Display-coordinate-system origin in global model coordinates.")] string displayOrigin,
        [Description("Display-coordinate-system X axis vector.")] string displayAxisX,
        [Description("Display-coordinate-system Y axis vector.")] string displayAxisY,
        [Description("Restriction-box minimum 'x,y,z' in view coordinates.")] string restrictionMin,
        [Description("Restriction-box maximum 'x,y,z' in view coordinates.")] string restrictionMax,
        [Description("Saved GA view attributes file.")] string attributeFile = "",
        [Description("Optional view name.")] string name = "",
        [Description("Optional view scale.")] double? scale = null,
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null,
        [Description("What insertionPoint places: origin (model origin, default), frame_lower_left or frame_center.")]
        string placeBy = "origin")
    {
        if (ViewPlacement.Normalize(placeBy) == null)
            return ToolValidationFailure(
                "create_drawing_views", apply,
                "placeBy must be origin, frame_lower_left or frame_center.");
        var insertion = ToolHelpers.ParsePoint(insertionPoint);
        var viewO = ToolHelpers.ParsePoint(viewOrigin);
        var viewX = ToolHelpers.ParsePoint(viewAxisX);
        var viewY = ToolHelpers.ParsePoint(viewAxisY);
        var displayO = ToolHelpers.ParsePoint(displayOrigin);
        var displayX = ToolHelpers.ParsePoint(displayAxisX);
        var displayY = ToolHelpers.ParsePoint(displayAxisY);
        var min = ToolHelpers.ParsePoint(restrictionMin);
        var max = ToolHelpers.ParsePoint(restrictionMax);
        if (insertion == null || viewO == null || viewX == null || viewY == null ||
            displayO == null || displayX == null || displayY == null ||
            min == null || max == null)
        {
            return new DrawingWriteResult
            {
                Operation = "create_drawing_views",
                Applied = apply,
                Backend = "Tool validation",
                Message = "All GA view points and axes must use the 'x,y,z' format.",
            };
        }
        if (IsZeroVector(viewX) || IsZeroVector(viewY) ||
            IsZeroVector(displayX) || IsZeroVector(displayY))
            return new DrawingWriteResult
            {
                Operation = "create_drawing_views",
                Applied = apply,
                Backend = model.GetDrawingStatus().Backend,
                Message = "GA view coordinate-system axis vectors must be non-zero.",
            };
        if (AreParallel(viewX, viewY) || AreParallel(displayX, displayY))
            return ToolValidationFailure(
                "create_drawing_views", apply,
                "Each GA coordinate system requires non-parallel X and Y axes.");
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            return new DrawingWriteResult
            {
                Operation = "create_drawing_views",
                Applied = apply,
                Backend = model.GetDrawingStatus().Backend,
                Message = "restrictionMin must be component-wise less than or equal to restrictionMax.",
            };

        return ToolHelpers.Write(model, expectedModelPath, () => model.CreateDrawingViews(new[]
        {
            new DrawingViewSpec
            {
                Type = "ga_model",
                InsertionPoint = insertion,
                ViewCoordinateSystem = new CoordinateSystemInfo
                {
                    Origin = viewO,
                    AxisX = viewX,
                    AxisY = viewY,
                },
                DisplayCoordinateSystem = new CoordinateSystemInfo
                {
                    Origin = displayO,
                    AxisX = displayX,
                    AxisY = displayY,
                },
                RestrictionMin = min,
                RestrictionMax = max,
                AttributeFile = attributeFile ?? "",
                Name = name ?? "",
                PlaceBy = ViewPlacement.Normalize(placeBy)!,
                Scale = scale,
            },
        }, apply));
    }

    private static bool IsZeroVector(Point3D value) =>
        value.X == 0 && value.Y == 0 && value.Z == 0;

    private static bool AreParallel(Point3D first, Point3D second)
    {
        var crossX = first.Y * second.Z - first.Z * second.Y;
        var crossY = first.Z * second.X - first.X * second.Z;
        var crossZ = first.X * second.Y - first.Y * second.X;
        return crossX * crossX + crossY * crossY + crossZ * crossZ < 1e-18;
    }

    [McpServerTool(Name = "tekla_create_section_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a straight or curved section view from an existing view on the ACTIVE " +
                 "drawing. Cut points are view-local; insertionPoint is sheet paper mm. The section " +
                 "looks along the cut direction rotated +90° in the source view (a cut drawn upwards " +
                 "looks toward −x; the preview states it). depthUp = how far it sees BEHIND the cut " +
                 "plane, depthDown = the slab between the cut and the viewer; both must be > 0. " +
                 "attributeFile is loaded after creation (Tekla ignores it in CreateSectionView), " +
                 "keeping name, scale, label and restriction box. The created section mark is " +
                 "returned in objectPreview (rename it with the view). Preview unless apply=true.")]
    public static DrawingWriteResult CreateSectionView(
        ITeklaModelService model,
        [Description("Source View ID from tekla_list_drawing_views.")] int sourceViewId,
        [Description("Source View ID2.")] int sourceViewId2,
        [Description("Cut points 'x,y,z; ...': 2 for straight, 3 for curved.")] string points,
        [Description("New view insertion point on sheet 'x,y,z' (paper mm).")] string insertionPoint,
        [Description("Ephemeral source-view index fallback when source IDs are zero.")] int sourceViewIndex = -1,
        [Description("true = curved section, false = straight.")] bool curved = false,
        [Description("Depth BEHIND the cut plane, away from the viewer (model mm, > 0).")] double? depthUp = null,
        [Description("Depth between the cut plane and the viewer (model mm, > 0; typically 5-10).")] double? depthDown = null,
        [Description("Saved view attributes file.")] string attributeFile = "",
        [Description("Saved section-mark attributes file.")] string markAttributeFile = "",
        [Description("Optional created-view name.")] string name = "",
        [Description("Optional view scale.")] double? scale = null,
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null)
    {
        var cutPoints = ToolHelpers.ParsePoints(points);
        var expected = curved ? 3 : 2;
        var insertion = ToolHelpers.ParsePoint(insertionPoint);
        if (cutPoints.Count != expected)
            return ToolValidationFailure(
                "create_drawing_views", apply,
                (curved ? "Curved section" : "Straight section") +
                " requires exactly " + expected + " valid cut points.");
        if (insertion == null)
            return ToolValidationFailure(
                "create_drawing_views", apply,
                "insertionPoint must use the 'x,y,z' format.");
        var depthProblem = SectionViews.CheckDepths(depthUp, depthDown);
        if (depthProblem != null)
            return ToolValidationFailure("create_drawing_views", apply, depthProblem);

        return ToolHelpers.Write(model, expectedModelPath, () => model.CreateDrawingViews(new[]
        {
            new DrawingViewSpec
            {
                Type = curved ? "curved_section" : "section",
                SourceViewId = sourceViewId,
                SourceViewId2 = sourceViewId2,
                SourceViewIndex = sourceViewIndex,
                Points = cutPoints,
                InsertionPoint = insertion,
                DepthUp = depthUp,
                DepthDown = depthDown,
                AttributeFile = attributeFile ?? "",
                MarkAttributeFile = markAttributeFile ?? "",
                Name = name ?? "",
                Scale = scale,
            },
        }, apply));
    }

    [McpServerTool(Name = "tekla_create_detail_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a detail view from an existing view on the ACTIVE drawing. center/boundary/" +
                 "label are view-local; insertionPoint is sheet paper mm. attributeFile is loaded after " +
                 "creation, keeping name, scale, label and restriction box; the created detail mark is " +
                 "returned in objectPreview. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDetailView(
        ITeklaModelService model,
        [Description("Source View ID.")] int sourceViewId,
        [Description("Source View ID2.")] int sourceViewId2,
        [Description("Detail center 'x,y,z' in source-view coordinates.")] string centerPoint,
        [Description("Boundary point 'x,y,z' in source-view coordinates.")] string boundaryPoint,
        [Description("Detail-label point 'x,y,z' in source-view coordinates.")] string labelPoint,
        [Description("New view insertion point 'x,y,z' on sheet (paper mm).")] string insertionPoint,
        [Description("Ephemeral source-view index fallback when source IDs are zero.")] int sourceViewIndex = -1,
        [Description("Saved view attributes file.")] string attributeFile = "",
        [Description("Saved detail-mark attributes file.")] string markAttributeFile = "",
        [Description("Optional created-view name.")] string name = "",
        [Description("Optional view scale.")] double? scale = null,
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null)
    {
        var center = ToolHelpers.ParsePoint(centerPoint);
        var boundary = ToolHelpers.ParsePoint(boundaryPoint);
        var label = ToolHelpers.ParsePoint(labelPoint);
        var insertion = ToolHelpers.ParsePoint(insertionPoint);
        if (center == null || boundary == null || label == null || insertion == null)
            return ToolValidationFailure(
                "create_drawing_views", apply,
                "centerPoint, boundaryPoint, labelPoint and insertionPoint must use 'x,y,z'.");

        return ToolHelpers.Write(model, expectedModelPath, () => model.CreateDrawingViews(new[]
        {
            new DrawingViewSpec
            {
                Type = "detail",
                SourceViewId = sourceViewId,
                SourceViewId2 = sourceViewId2,
                SourceViewIndex = sourceViewIndex,
                Points = new List<Point3D> { center, boundary, label },
                InsertionPoint = insertion,
                AttributeFile = attributeFile ?? "",
                MarkAttributeFile = markAttributeFile ?? "",
                Name = name ?? "",
                Scale = scale,
            },
        }, apply));
    }

    [McpServerTool(Name = "tekla_modify_drawing_view", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Modify one ACTIVE-drawing view by non-zero session ID/ID2 or ephemeral index: " +
                 "name, sheet origin/frame size, scale and rotations, or load a saved view attributes " +
                 "file (attributeFile, e.g. one carrying a part filter and neighbour-part filter) while " +
                 "keeping the view's scale, restriction box, origin, label and name (keep* default true; " +
                 "explicit origin/scale/name override them). The name is used verbatim (a leading space " +
                 "is part of it). After loading, the message gives the view's model-object " +
                 "count before and after, so you can see whether a filter took effect. Tekla caches view " +
                 "filters by attribute-file NAME within a session: after editing <name>.vi.npfilter, " +
                 "save it under a NEW name, or the view keeps the old filter. A new scale keeps the " +
                 "paper offset of leader-line marks (keepMarkOffsets, default true); dimension lines keep " +
                 "theirs by themselves, so never rescale their distances. labelText sets the view label " +
                 "(API-created views often have an empty one; tekla_list_drawing_views shows labelText); " +
                 "renameMark=true with a new name renames the section/detail mark of the same old name. " +
                 "The label sits above the frame, and hidden marks extend the frame. Angles are degrees. " +
                 "Preview unless apply=true; re-list after structural edits.")]
    public static DrawingWriteResult ModifyDrawingView(
        ITeklaModelService model,
        [Description("Non-zero session View ID; omit to use viewIndex.")] int? viewId = null,
        [Description("Session View ID2.")] int? viewId2 = null,
        [Description("Ephemeral view index fallback; re-list before use.")] int viewIndex = -1,
        [Description("New name; omit to keep.")] string? name = null,
        [Description("New sheet origin 'x,y,z' in paper mm.")] string? origin = null,
        [Description("New frame width in paper mm.")] double? width = null,
        [Description("New frame height in paper mm.")] double? height = null,
        [Description("New scale.")] double? scale = null,
        [Description("Rotate around view X in degrees.")] double? rotateXDegrees = null,
        [Description("Rotate around view Y in degrees.")] double? rotateYDegrees = null,
        [Description("Rotate around view Z in degrees.")] double? rotateZDegrees = null,
        [Description("Rotate on drawing plane in degrees.")] double? rotateOnDrawingPlaneDegrees = null,
        [Description("Set true to commit; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null,
        [Description("Saved view attributes file to load (name without extension); omit to keep.")] string? attributeFile = null,
        [Description("Keep the current scale when loading attributeFile (default true).")] bool keepScale = true,
        [Description("Keep the current restriction box when loading attributeFile (default true).")] bool keepRestrictionBox = true,
        [Description("Keep the current sheet origin when loading attributeFile (default true).")] bool keepOrigin = true,
        [Description("Keep the current view label when loading attributeFile (default true).")] bool keepLabel = true,
        [Description("Keep the current view name when loading attributeFile (default true).")] bool keepName = true,
        [Description("With a new scale: move leader-line marks so their paper offset stays (default true).")] bool keepMarkOffsets = true,
        [Description("New view label text (replaces tag A1); omit to keep.")] string? labelText = null,
        [Description("Label font name; default = the current label's font, else Arial.")] string? labelFont = null,
        [Description("Label text height in paper mm; default = the current label's, else 3.5.")] double? labelHeight = null,
        [Description("Label vertical position: top or bottom; omit to keep.")] string? labelVertical = null,
        [Description("Label horizontal position: centered_by_frame, centered_by_restriction_box, left_by_frame, right_by_frame, left_by_restriction_box, right_by_restriction_box; omit to keep.")] string? labelHorizontal = null,
        [Description("With labelText: empty the other label tags A2-A5 (default true).")] bool clearOtherTags = true,
        [Description("With a new name: also rename the section/detail mark named like the view's old name.")] bool renameMark = false)
    {
        if (name == null && origin == null && !width.HasValue && !height.HasValue &&
            !scale.HasValue && !rotateXDegrees.HasValue && !rotateYDegrees.HasValue &&
            !rotateZDegrees.HasValue && !rotateOnDrawingPlaneDegrees.HasValue &&
            string.IsNullOrWhiteSpace(attributeFile) && labelText == null &&
            string.IsNullOrWhiteSpace(labelVertical) && string.IsNullOrWhiteSpace(labelHorizontal))
            return ToolValidationFailure(
                "modify_drawing_views", apply,
                "No view changes were supplied.");
        var parsedOrigin = ToolHelpers.ParsePoint(origin);
        if (!string.IsNullOrWhiteSpace(origin) && parsedOrigin == null)
            return ToolValidationFailure(
                "modify_drawing_views", apply,
                "origin must use the 'x,y,z' format.");
        if ((width.HasValue && width.Value <= 0) ||
            (height.HasValue && height.Value <= 0) ||
            (scale.HasValue && scale.Value <= 0))
            return ToolValidationFailure(
                "modify_drawing_views", apply,
                "width, height and scale must be greater than zero when supplied.");
        return ToolHelpers.Write(model, expectedModelPath, () => model.ModifyDrawingViews(new[]
        {
            new DrawingViewModification
            {
                ViewIndex = viewIndex,
                ViewId = viewId,
                ViewId2 = viewId2,
                Name = name,
                Origin = parsedOrigin,
                Width = width,
                Height = height,
                Scale = scale,
                RotateXDegrees = rotateXDegrees,
                RotateYDegrees = rotateYDegrees,
                RotateZDegrees = rotateZDegrees,
                RotateOnDrawingPlaneDegrees = rotateOnDrawingPlaneDegrees,
                // Verbatim: Tekla 2021 loads " Truss node" (leading space) only with the space.
                AttributeFile = string.IsNullOrWhiteSpace(attributeFile) ? null : attributeFile,
                KeepScale = keepScale,
                KeepRestrictionBox = keepRestrictionBox,
                KeepOrigin = keepOrigin,
                KeepLabel = keepLabel,
                KeepName = keepName,
                KeepMarkOffsets = keepMarkOffsets,
                LabelText = labelText,
                LabelFont = labelFont,
                LabelHeight = labelHeight,
                LabelVertical = string.IsNullOrWhiteSpace(labelVertical) ? null : labelVertical,
                LabelHorizontal = string.IsNullOrWhiteSpace(labelHorizontal) ? null : labelHorizontal,
                ClearOtherTags = clearOtherTags,
                RenameMark = renameMark,
            },
        }, apply));
    }

    [McpServerTool(Name = "tekla_delete_drawing_view", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Delete one view and its children from the ACTIVE drawing. Preview unless apply=true.")]
    public static DrawingWriteResult DeleteDrawingView(
        ITeklaModelService model,
        [Description("Non-zero session View ID; omit to use viewIndex.")] int? viewId = null,
        [Description("Session View ID2.")] int? viewId2 = null,
        [Description("Ephemeral view index fallback; re-list before use.")] int viewIndex = -1,
        [Description("Set true to delete; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        ToolHelpers.Write(model, expectedModelPath, () => model.ModifyDrawingViews(new[]
        {
            new DrawingViewModification
            {
                ViewIndex = viewIndex,
                ViewId = viewId,
                ViewId2 = viewId2,
                Delete = true,
            },
        }, apply));

    [McpServerTool(Name = "tekla_create_drawing_objects", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Batch-create up to 200 annotations/graphics/dimensions/marks in the ACTIVE " +
                 "drawing. Structured DrawingObjectSpec items. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingObjects(
        ITeklaModelService model,
        [Description("Structured drawing-object specs (maximum 200).")] IReadOnlyList<DrawingObjectSpec> objects,
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        ToolHelpers.Write(model, expectedModelPath, () => model.CreateDrawingObjects(
            (objects ?? new List<DrawingObjectSpec>()).Take(200).ToList(), apply));

    [McpServerTool(Name = "tekla_create_drawing_text", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create text in a view (view-local/model coordinates) or on the sheet (paper mm). " +
                 "Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingText(
        ITeklaModelService model,
        [Description("Text contents.")] string text,
        [Description("Insertion point 'x,y,z'.")] string point,
        [Description("View ID; omit and set viewIndex=-1 for sheet text.")] int? viewId = null,
        [Description("View ID2.")] int? viewId2 = null,
        [Description("view (default), model, or sheet.")] string coordinateSpace = "view",
        [Description("Target view index (default 0); use -1 with coordinateSpace=sheet. Ignored when viewId supplied.")] int viewIndex = 0,
        [Description("Saved text attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "text", point, viewId, viewId2, viewIndex, coordinateSpace,
            attributeFile, text: text, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_drawing_line", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a line in the ACTIVE drawing. Points use view-local/model/sheet coordinates. " +
                 "Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingLine(
        ITeklaModelService model,
        [Description("Two points 'x,y,z; x,y,z'.")] string points,
        [Description("Target View ID; omit for sheet with viewIndex=-1.")] int? viewId = null,
        [Description("Target View ID2.")] int? viewId2 = null,
        [Description("view, model, or sheet.")] string coordinateSpace = "view",
        [Description("Target view index (default 0); use -1 with coordinateSpace=sheet.")] int viewIndex = 0,
        [Description("Optional line bulge.")] double? bulge = null,
        [Description("Saved line attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "line", points, viewId, viewId2, viewIndex, coordinateSpace,
            attributeFile, bulge: bulge, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_drawing_rectangle", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a rectangle from two opposite points in the ACTIVE drawing. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingRectangle(
        ITeklaModelService model,
        [Description("Two points 'x,y,z; x,y,z'.")] string points,
        [Description("Target View ID.")] int? viewId = null,
        [Description("Target View ID2.")] int? viewId2 = null,
        [Description("view, model, or sheet.")] string coordinateSpace = "view",
        [Description("Target view index (default 0); use -1 with coordinateSpace=sheet.")] int viewIndex = 0,
        [Description("Rotation angle in degrees.")] double? angleDegrees = null,
        [Description("Saved rectangle attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "rectangle", points, viewId, viewId2, viewIndex, coordinateSpace,
            attributeFile, angleDegrees: angleDegrees, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_drawing_circle", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a circle in the ACTIVE drawing. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingCircle(
        ITeklaModelService model,
        [Description("Center 'x,y,z'.")] string center,
        [Description("Radius in the target coordinate system.")] double radius,
        [Description("Target View ID.")] int? viewId = null,
        [Description("Target View ID2.")] int? viewId2 = null,
        [Description("view, model, or sheet.")] string coordinateSpace = "view",
        [Description("Target view index (default 0); use -1 with coordinateSpace=sheet.")] int viewIndex = 0,
        [Description("Saved circle attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "circle", center, viewId, viewId2, viewIndex, coordinateSpace,
            attributeFile, radius: radius, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_drawing_arc", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create an arc from three points, or two points plus radius. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingArc(
        ITeklaModelService model,
        [Description("Two or three points 'x,y,z; ...'.")] string points,
        [Description("Radius when only two points are supplied.")] double? radius = null,
        [Description("Target View ID.")] int? viewId = null,
        [Description("Target View ID2.")] int? viewId2 = null,
        [Description("view, model, or sheet.")] string coordinateSpace = "view",
        [Description("Target view index (default 0); use -1 with coordinateSpace=sheet.")] int viewIndex = 0,
        [Description("Saved arc attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "arc", points, viewId, viewId2, viewIndex, coordinateSpace,
            attributeFile, radius: radius, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_drawing_polyline", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a polyline in the ACTIVE drawing. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingPolyline(
        ITeklaModelService model,
        [Description("Two or more points 'x,y,z; ...'.")] string points,
        [Description("Target View ID.")] int? viewId = null,
        [Description("Target View ID2.")] int? viewId2 = null,
        [Description("view, model, or sheet.")] string coordinateSpace = "view",
        [Description("Target view index (default 0); use -1 with coordinateSpace=sheet.")] int viewIndex = 0,
        [Description("Optional common bulge.")] double? bulge = null,
        [Description("Saved polyline attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "polyline", points, viewId, viewId2, viewIndex, coordinateSpace,
            attributeFile, bulge: bulge, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_drawing_polygon", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a closed polygon in the ACTIVE drawing. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingPolygon(
        ITeklaModelService model,
        [Description("Three or more points 'x,y,z; ...'.")] string points,
        [Description("Target View ID.")] int? viewId = null,
        [Description("Target View ID2.")] int? viewId2 = null,
        [Description("view, model, or sheet.")] string coordinateSpace = "view",
        [Description("Target view index (default 0); use -1 with coordinateSpace=sheet.")] int viewIndex = 0,
        [Description("Optional common bulge.")] double? bulge = null,
        [Description("Saved polygon attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "polygon", points, viewId, viewId2, viewIndex, coordinateSpace,
            attributeFile, bulge: bulge, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_revision_cloud", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a revision cloud in the ACTIVE drawing. Preview unless apply=true.")]
    public static DrawingWriteResult CreateRevisionCloud(
        ITeklaModelService model,
        [Description("Three or more corner points 'x,y,z; ...'.")] string points,
        [Description("Target View ID.")] int? viewId = null,
        [Description("Target View ID2.")] int? viewId2 = null,
        [Description("view, model, or sheet.")] string coordinateSpace = "view",
        [Description("Target view index (default 0); use -1 with coordinateSpace=sheet.")] int viewIndex = 0,
        [Description("Cloud bulge/size.")] double? bulge = null,
        [Description("Saved cloud attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "cloud", points, viewId, viewId2, viewIndex, coordinateSpace,
            attributeFile, bulge: bulge, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_straight_dimension", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a straight dimension set in a real view of the ACTIVE drawing. Distance " +
                 "is paper mm (the server multiplies it by the view scale, which is what Tekla " +
                 "expects); Tekla offsets the line from the OUTERMOST point of the set along " +
                 "upDirection. Points may be view-local or global model coordinates. Use placing=fixed " +
                 "when the line must stay at that offset (free placing moves it away from marks). When " +
                 "reading dimension sets back (tekla_list_drawing_objects), dimensionDistance is in view " +
                 "units at the CURRENT scale and doubles after 1:5 → 1:10 although nothing moved: use " +
                 "dimensionDistancePaperMm. Preview unless apply=true.")]
    public static DrawingWriteResult CreateStraightDimension(
        ITeklaModelService model,
        [Description("Two or more dimension points 'x,y,z; ...'.")] string points,
        [Description("Target View ID.")] int viewId,
        [Description("Target View ID2.")] int viewId2 = 0,
        [Description("Up direction 'x,y,z' in view coordinates.")] string upDirection = "0,1,0",
        [Description("Dimension line distance in paper mm, from the outermost point along upDirection.")] double distance = 10,
        [Description("view or model.")] string coordinateSpace = "view",
        [Description("Saved dimension attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null,
        [Description("fixed = keep the line exactly at distance; free = Tekla may move it away from marks " +
                     "(the offset is then not kept); empty = the attribute file's setting.")] string placing = "")
    {
        var normalizedPlacing = (placing ?? "").Trim().ToLowerInvariant();
        if (normalizedPlacing.Length > 0 && normalizedPlacing != "fixed" && normalizedPlacing != "free")
            return ToolValidationFailure(
                "create_drawing_objects", apply, "placing must be fixed, free or empty.");
        var up = ToolHelpers.ParsePoint(upDirection);
        if (up == null || IsZeroVector(up))
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "upDirection must be a non-zero 'x,y,z' vector.");
        return CreateOne(
            model, "straight_dimension", points,
            viewId, viewId2, -1, coordinateSpace,
            attributeFile, upDirection: up, distance: distance, apply: apply,
            expectedModelPath: expectedModelPath, placing: normalizedPlacing);
    }

    [McpServerTool(Name = "tekla_create_angle_dimension", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create an angle dimension from origin, point1, point2. Distance is paper mm. " +
                 "Preview unless apply=true.")]
    public static DrawingWriteResult CreateAngleDimension(
        ITeklaModelService model,
        [Description("Origin; point1; point2 as 'x,y,z; ...'.")] string points,
        [Description("Target View ID.")] int viewId,
        [Description("Target View ID2.")] int viewId2 = 0,
        [Description("Dimension distance in paper mm.")] double distance = 10,
        [Description("view or model.")] string coordinateSpace = "view",
        [Description("Saved angle-dimension attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "angle_dimension", points, viewId, viewId2, -1, coordinateSpace,
            attributeFile, distance: distance, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_radius_dimension", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a radius dimension from three arc points: start, a point on the arc " +
                 "between, end — in either direction. Tekla draws the arc clockwise from point 1 " +
                 "to point 3, so the server swaps points 1 and 3 when they run counter-clockwise " +
                 "in the view (a warning says so); a wrong middle point dimensions the " +
                 "complementary arc. For exact values take the points from " +
                 "tekla_get_part_curve_geometry's view block rather than rounding them. " +
                 "Distance is paper mm. Preview unless apply=true.")]
    public static DrawingWriteResult CreateRadiusDimension(
        ITeklaModelService model,
        [Description("Arc start; a point on the arc between; arc end, as 'x,y,z; x,y,z; x,y,z'.")] string points,
        [Description("Target View ID.")] int viewId,
        [Description("Target View ID2.")] int viewId2 = 0,
        [Description("Dimension distance in paper mm.")] double distance = 10,
        [Description("view or model.")] string coordinateSpace = "view",
        [Description("Saved radius-dimension attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "radius_dimension", points, viewId, viewId2, -1, coordinateSpace,
            attributeFile, distance: distance, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_curved_dimension", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a radial or orthogonal curved dimension. First 3 points define the arc; " +
                 "remaining points are dimension points. Preview unless apply=true.")]
    public static DrawingWriteResult CreateCurvedDimension(
        ITeklaModelService model,
        [Description("Arc p1,p2,p3 then dimension points: 'x,y,z; ...'.")] string points,
        [Description("Target View ID.")] int viewId,
        [Description("Target View ID2.")] int viewId2 = 0,
        [Description("radial or orthogonal.")] string mode = "radial",
        [Description("Dimension distance in paper mm.")] double distance = 10,
        [Description("view or model.")] string coordinateSpace = "view",
        [Description("Saved curved-dimension attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null)
    {
        var normalized = (mode ?? "").Trim().ToLowerInvariant();
        if (normalized != "radial" && normalized != "orthogonal")
            return new DrawingWriteResult
            {
                Operation = "create_drawing_objects",
                Applied = apply,
                Backend = model.GetDrawingStatus().Backend,
                Message = "Unknown curved-dimension mode. Use radial or orthogonal.",
            };
        return CreateOne(
            model,
            normalized == "orthogonal" ? "curved_orthogonal" : "curved_radial",
            points, viewId, viewId2, -1, coordinateSpace,
            attributeFile, distance: distance, apply: apply,
            expectedModelPath: expectedModelPath);
    }

    [McpServerTool(Name = "tekla_create_drawing_mark", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a mark for a model object already represented in a target view. Optional: " +
                 "leaderStart (a point on the VISIBLE part — a member mark with default placing sits at " +
                 "the part's midpoint, often outside a detail view, and still counts in the view frame), " +
                 "placing (leader_line / base_line / along_line with lineEnd / point), contentText to " +
                 "replace the content, or copyContentFromMarkId to clone another mark's content (e.g. a " +
                 "bolt mark). Points are view coordinates. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingMark(
        ITeklaModelService model,
        [Description("Represented model-object GUID.")] string modelGuid,
        [Description("Target View ID.")] int viewId,
        [Description("Target View ID2.")] int viewId2 = 0,
        [Description("Optional mark insertion point 'x,y,z' in view coordinates.")] string? insertionPoint = null,
        [Description("Saved mark attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null,
        [Description("Leader-line start 'x,y,z' (view coordinates), on the visible part.")] string? leaderStart = null,
        [Description("leader_line (default with leaderStart), base_line, along_line or point.")] string placing = "",
        [Description("base_line / along_line: the line's end 'x,y,z'.")] string? lineEnd = null,
        [Description("Replace the mark content with this text.")] string? contentText = null,
        [Description("Copy the content of this existing mark ('id:id2' from tekla_list_drawing_objects).")] string? copyContentFromMarkId = null)
    {
        var start = ToolHelpers.ParsePoint(leaderStart);
        var end = ToolHelpers.ParsePoint(lineEnd);
        if ((!string.IsNullOrWhiteSpace(leaderStart) && start == null) || (!string.IsNullOrWhiteSpace(lineEnd) && end == null))
            return ToolValidationFailure("create_drawing_objects", apply, "leaderStart and lineEnd must use 'x,y,z'.");
        var mode = (placing ?? "").Trim().Replace("-", "_").ToLowerInvariant();
        if (mode.Length > 0 && mode != "leader_line" && mode != "base_line" && mode != "along_line" && mode != "point")
            return ToolValidationFailure("create_drawing_objects", apply,
                "placing must be leader_line, base_line, along_line or point.");
        return CreateOne(model, "mark", insertionPoint ?? "", viewId, viewId2, -1, "view",
            attributeFile, modelGuid: modelGuid, apply: apply,
            expectedModelPath: expectedModelPath, placing: mode,
            leaderStart: start, lineEnd: end, contentText: contentText,
            copyContentFrom: copyContentFromMarkId);
    }

    [McpServerTool(Name = "tekla_create_detail_mark", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false)]
    [Description("Create a standalone DETAIL MARK (callout circle + label) in a view of the ACTIVE drawing, " +
                 "WITHOUT creating a detail view — e.g. to reference a node drawn on another sheet (issue #36). " +
                 "Give center + boundary + label points, or center + label + radius (model mm). Points are " +
                 "view-local, or global model coordinates with coordinateSpace=model. The created mark's " +
                 "ID is returned in objectPreview (detail marks cannot be listed back reliably). Preview " +
                 "unless apply=true.")]
    public static DrawingWriteResult CreateDetailMark(
        ITeklaModelService model,
        [Description("Target View ID.")] int viewId,
        [Description("Detail centre 'x,y,z'.")] string centerPoint,
        [Description("Label point 'x,y,z'.")] string labelPoint,
        [Description("Target View ID2.")] int viewId2 = 0,
        [Description("Boundary point 'x,y,z' on the circle; omit when giving radius.")] string? boundaryPoint = null,
        [Description("Circle radius (model mm) when no boundaryPoint is given.")] double? radius = null,
        [Description("Mark name, e.g. the node number.")] string markName = "",
        [Description("Saved detail-mark attributes file.")] string markAttributeFile = "",
        [Description("view or model.")] string coordinateSpace = "view",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null)
    {
        var points = string.IsNullOrWhiteSpace(boundaryPoint)
            ? centerPoint + "; " + labelPoint
            : centerPoint + "; " + boundaryPoint + "; " + labelPoint;
        if (string.IsNullOrWhiteSpace(boundaryPoint) && (!radius.HasValue || radius.Value <= 0))
            return ToolValidationFailure(
                "create_drawing_objects", apply, "Give boundaryPoint, or radius > 0.");
        return CreateOne(model, "detail_mark", points, viewId, viewId2, -1, coordinateSpace,
            markAttributeFile, text: markName, radius: radius, apply: apply,
            expectedModelPath: expectedModelPath);
    }

    [McpServerTool(Name = "tekla_create_section_mark", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false)]
    [Description("Create a standalone SECTION MARK (cut symbols) in a view of the ACTIVE drawing, WITHOUT " +
                 "creating a section view — e.g. to reference a section drawn on another sheet (issue #36). " +
                 "Two points: left and right symbol; the look direction follows the same rule as " +
                 "tekla_create_section_view (the cut direction rotated +90°). Points are view-local, or " +
                 "global model coordinates with coordinateSpace=model. Preview unless apply=true.")]
    public static DrawingWriteResult CreateSectionMark(
        ITeklaModelService model,
        [Description("Target View ID.")] int viewId,
        [Description("Left point; right point as 'x,y,z; x,y,z'.")] string points,
        [Description("Target View ID2.")] int viewId2 = 0,
        [Description("Mark name, e.g. '6'.")] string markName = "",
        [Description("Saved section-mark attributes file.")] string markAttributeFile = "",
        [Description("view or model.")] string coordinateSpace = "view",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "section_mark", points, viewId, viewId2, -1, coordinateSpace,
            markAttributeFile, text: markName, apply: apply, expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_create_level_mark", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a level mark from insertion and base points. Preview unless apply=true.")]
    public static DrawingWriteResult CreateLevelMark(
        ITeklaModelService model,
        [Description("Insertion point; base point as 'x,y,z; x,y,z'.")] string points,
        [Description("Target View ID; omit for sheet with viewIndex=-1.")] int? viewId = null,
        [Description("Target View ID2.")] int? viewId2 = null,
        [Description("view, model, or sheet.")] string coordinateSpace = "view",
        [Description("Target view index (default 0); use -1 with coordinateSpace=sheet.")] int viewIndex = 0,
        [Description("NoArrowNoLeaderLine, ArrowWithoutLeaderLine, InclinedLeaderLine, OrthogonalLeaderLine.")] string subType = "",
        [Description("Saved level-mark attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null)
    {
        var normalized = (subType ?? "").Trim();
        if (normalized.Length > 0 &&
            normalized != "NoArrowNoLeaderLine" &&
            normalized != "ArrowWithoutLeaderLine" &&
            normalized != "InclinedLeaderLine" &&
            normalized != "OrthogonalLeaderLine")
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "Unknown level-mark subtype. Use NoArrowNoLeaderLine, ArrowWithoutLeaderLine, " +
                "InclinedLeaderLine, or OrthogonalLeaderLine.");
        return CreateOne(
            model, "level_mark", points, viewId, viewId2,
            viewIndex, coordinateSpace, attributeFile,
            subType: normalized, apply: apply,
            expectedModelPath: expectedModelPath);
    }

    [McpServerTool(Name = "tekla_create_drawing_symbol", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a symbol from a Tekla .sym library in the ACTIVE drawing. Preview unless apply=true.")]
    public static DrawingWriteResult CreateDrawingSymbol(
        ITeklaModelService model,
        [Description("Insertion point 'x,y,z'.")] string point,
        [Description("Symbol library, e.g. xsteel.")] string symbolFile,
        [Description("Symbol index 0..255.")] int symbolIndex,
        [Description("Target View ID; omit for sheet with viewIndex=-1.")] int? viewId = null,
        [Description("Target View ID2.")] int? viewId2 = null,
        [Description("view, model, or sheet.")] string coordinateSpace = "view",
        [Description("Target view index (default 0); use -1 with coordinateSpace=sheet.")] int viewIndex = 0,
        [Description("Saved symbol attributes file.")] string attributeFile = "",
        [Description("Set true to create; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null) =>
        CreateOne(model, "symbol", point, viewId, viewId2, viewIndex, coordinateSpace,
            attributeFile, symbolFile: symbolFile, symbolIndex: symbolIndex, apply: apply,
            expectedModelPath: expectedModelPath);

    [McpServerTool(Name = "tekla_modify_drawing_objects", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Batch edit active-drawing objects: change Text contents, move, set visibility, " +
                 "or load an attributes file; on marks also set insertionPoint, leaderStart (puts the mark " +
                 "on a leader line) and contentText. Address by non-zero session Object ID:ID2 or current " +
                 "selection. Preview unless apply=true.")]
    public static DrawingWriteResult ModifyDrawingObjects(
        ITeklaModelService model,
        [Description("Object IDs 'id:id2; id:id2'.")] string? objectIds = null,
        [Description("Exact API object types.")] string? types = null,
        [Description("Operate on current drawing selection.")] bool useSelection = false,
        [Description("New text (Text objects only); omit to keep.")] string? text = null,
        [Description("Relative move vector 'x,y,z' in object/view coordinates.")] string? moveBy = null,
        [Description("drawing, view, show_drawing, show_view; omit to keep.")] string? visibility = null,
        [Description("Saved attributes file to load; omit to keep.")] string? attributeFile = null,
        [Description("Safety cap (default 200).")] int limit = 200,
        [Description("Set true to commit; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null,
        [Description("Marks: new insertion point 'x,y,z' (view coordinates).")] string? insertionPoint = null,
        [Description("Marks: leader-line start 'x,y,z' (view coordinates).")] string? leaderStart = null,
        [Description("Marks: replace the content with this text.")] string? contentText = null)
    {
        var rejected = RejectEmptyMutationScope(
            model, "modify_drawing_objects", objectIds, types, useSelection, apply);
        var parsedInsertion = ToolHelpers.ParsePoint(insertionPoint);
        var parsedLeader = ToolHelpers.ParsePoint(leaderStart);
        if ((!string.IsNullOrWhiteSpace(insertionPoint) && parsedInsertion == null) ||
            (!string.IsNullOrWhiteSpace(leaderStart) && parsedLeader == null))
            return ToolValidationFailure(
                "modify_drawing_objects", apply, "insertionPoint and leaderStart must use 'x,y,z'.");
        if (rejected != null) return rejected;
        var parsedMove = ToolHelpers.ParsePoint(moveBy);
        if (!string.IsNullOrWhiteSpace(moveBy) && parsedMove == null)
            return ToolValidationFailure(
                "modify_drawing_objects", apply,
                "moveBy must use the 'x,y,z' format.");
        var normalizedVisibility = (visibility ?? "").Trim().Replace("-", "_").ToLowerInvariant();
        if (normalizedVisibility.Length > 0 &&
            normalizedVisibility != "drawing" &&
            normalizedVisibility != "view" &&
            normalizedVisibility != "show_drawing" &&
            normalizedVisibility != "show_view")
            return ToolValidationFailure(
                "modify_drawing_objects", apply,
                "visibility must be drawing, view, show_drawing, or show_view.");
        return ToolHelpers.Write(model, expectedModelPath, () => model.ModifyDrawingObjects(
            DrawingToolHelpers.BuildObjectQuery(
                objectIds: objectIds, types: types, useSelection: useSelection),
            new DrawingObjectModification
            {
                Text = text,
                MoveBy = parsedMove,
                Visibility = normalizedVisibility.Length == 0 ? null : normalizedVisibility,
                AttributeFile = attributeFile,
                InsertionPoint = parsedInsertion,
                LeaderStart = parsedLeader,
                ContentText = contentText,
            },
            apply,
            limit));
    }

    [McpServerTool(Name = "tekla_delete_drawing_objects", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Delete matched objects from the ACTIVE drawing by non-zero session IDs/type/selection. " +
                 "Preview unless apply=true.")]
    public static DrawingWriteResult DeleteDrawingObjects(
        ITeklaModelService model,
        [Description("Object IDs 'id:id2; id:id2'.")] string? objectIds = null,
        [Description("Exact API object types.")] string? types = null,
        [Description("Delete current drawing selection.")] bool useSelection = false,
        [Description("Safety cap (default 200).")] int limit = 200,
        [Description("Set true to delete; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null)
    {
        var rejected = RejectEmptyMutationScope(
            model, "delete_drawing_objects", objectIds, types, useSelection, apply);
        if (rejected != null) return rejected;
        return ToolHelpers.Write(model, expectedModelPath, () => model.ModifyDrawingObjects(
            DrawingToolHelpers.BuildObjectQuery(
                objectIds: objectIds, types: types, useSelection: useSelection),
            new DrawingObjectModification { Delete = true },
            apply,
            limit));
    }

    [McpServerTool(Name = "tekla_merge_drawing_marks", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Merge compatible Mark objects in the ACTIVE drawing. Address by IDs or use " +
                 "the current selection. Preview unless apply=true.")]
    public static DrawingWriteResult MergeDrawingMarks(
        ITeklaModelService model,
        [Description("Mark Object IDs 'id:id2; id:id2'.")] string? objectIds = null,
        [Description("Use current drawing selection.")] bool useSelection = false,
        [Description("Safety cap (default 200).")] int limit = 200,
        [Description("Set true to merge; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null)
    {
        var rejected = RejectEmptyMutationScope(
            model, "merge_drawing_marks", objectIds, null, useSelection, apply);
        if (rejected != null) return rejected;
        return ToolHelpers.Write(model, expectedModelPath, () => model.OperateDrawingMarks(
            DrawingToolHelpers.BuildObjectQuery(
                objectIds: objectIds, types: "Mark,MarkSet", useSelection: useSelection),
            "merge", apply, limit));
    }

    [McpServerTool(Name = "tekla_split_drawing_marks", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Split merged Mark/MarkSet objects in the ACTIVE drawing. Address by IDs or use " +
                 "the current selection. Preview unless apply=true.")]
    public static DrawingWriteResult SplitDrawingMarks(
        ITeklaModelService model,
        [Description("Mark Object IDs 'id:id2; id:id2'.")] string? objectIds = null,
        [Description("Use current drawing selection.")] bool useSelection = false,
        [Description("Safety cap (default 200).")] int limit = 200,
        [Description("Set true to split; false returns preview.")] bool apply = false,
        [Description(ToolHelpers.ExpectedModelPathDescription)] string? expectedModelPath = null)
    {
        var rejected = RejectEmptyMutationScope(
            model, "split_drawing_marks", objectIds, null, useSelection, apply);
        if (rejected != null) return rejected;
        return ToolHelpers.Write(model, expectedModelPath, () => model.OperateDrawingMarks(
            DrawingToolHelpers.BuildObjectQuery(
                objectIds: objectIds, types: "Mark,MarkSet", useSelection: useSelection),
            "split", apply, limit));
    }

    private static DrawingWriteResult CreateOne(
        ITeklaModelService model,
        string kind,
        string points,
        int? viewId,
        int? viewId2,
        int viewIndex,
        string coordinateSpace,
        string attributeFile,
        string text = "",
        string modelGuid = "",
        string symbolFile = "",
        int? symbolIndex = null,
        string subType = "",
        double? radius = null,
        double? bulge = null,
        double? angleDegrees = null,
        Point3D? upDirection = null,
        double? distance = null,
        bool apply = false,
        string? expectedModelPath = null,
        string placing = "",
        Point3D? leaderStart = null,
        Point3D? lineEnd = null,
        string? contentText = null,
        string? copyContentFrom = null)
    {
        var normalizedKind = (kind ?? "").Trim().Replace("-", "_").ToLowerInvariant();
        var normalizedSpace = (coordinateSpace ?? "view").Trim().ToLowerInvariant();
        if (normalizedSpace != "view" &&
            normalizedSpace != "model" &&
            normalizedSpace != "sheet")
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "coordinateSpace must be view, model, or sheet.");

        var sheetTarget = viewIndex == -1 && !viewId.HasValue;
        if (sheetTarget && normalizedSpace != "sheet")
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "The sheet target requires coordinateSpace=sheet.");
        if (!sheetTarget && normalizedSpace == "sheet")
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "sheet coordinates require viewIndex=-1 and no viewId.");
        if (viewId.HasValue && viewId.Value == 0 && (viewId2 ?? 0) == 0)
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "A supplied viewId/viewId2 pair must not be 0:0.");

        var parsedPoints = ToolHelpers.ParsePoints(points);
        var minimumPoints = MinimumPointCount(normalizedKind);
        if (minimumPoints < 0)
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "Unknown drawing-object kind: " + normalizedKind + ".");
        if (parsedPoints.Count < minimumPoints)
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                normalizedKind + " requires at least " + minimumPoints + " valid point(s).");
        if (normalizedKind == "circle" && (!radius.HasValue || radius.Value <= 0))
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "circle requires radius > 0.");
        if (normalizedKind == "arc" && parsedPoints.Count < 3 &&
            (!radius.HasValue || radius.Value <= 0))
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "arc requires three points or two points plus radius > 0.");
        if (normalizedKind == "mark" && !System.Guid.TryParse(modelGuid, out _))
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "mark requires a valid represented modelGuid.");
        if (normalizedKind == "symbol" &&
            (!symbolIndex.HasValue || symbolIndex.Value < 0 || symbolIndex.Value > 255))
            return ToolValidationFailure(
                "create_drawing_objects", apply,
                "symbolIndex must be between 0 and 255.");

        return ToolHelpers.Write(model, expectedModelPath, () => model.CreateDrawingObjects(new[]
        {
            new DrawingObjectSpec
            {
                Kind = normalizedKind,
                ViewIndex = viewIndex,
                ViewId = viewId,
                ViewId2 = viewId2,
                CoordinateSpace = normalizedSpace,
                Points = parsedPoints,
                AttributeFile = attributeFile ?? "",
                Text = text ?? "",
                ModelGuid = modelGuid ?? "",
                SymbolFile = symbolFile ?? "",
                SymbolIndex = symbolIndex,
                SubType = subType ?? "",
                Radius = radius,
                Bulge = bulge,
                AngleDegrees = angleDegrees,
                UpDirection = upDirection,
                Distance = distance,
                Placing = placing.Length == 0 ? null : placing,
                LeaderStart = leaderStart,
                LineEnd = lineEnd,
                ContentText = string.IsNullOrWhiteSpace(contentText) ? null : contentText,
                CopyContentFromObjectId = string.IsNullOrWhiteSpace(copyContentFrom) ? null : copyContentFrom,
            },
        }, apply));
    }

    private static int MinimumPointCount(string kind)
    {
        switch (kind)
        {
            case "mark": return 0;
            case "text":
            case "circle":
            case "symbol": return 1;
            case "line":
            case "rectangle":
            case "arc":
            case "polyline":
            case "straight_dimension":
            case "dimension":
            case "level_mark": return 2;
            case "detail_mark":
            case "section_mark": return 2;
            case "polygon":
            case "cloud":
            case "angle_dimension":
            case "radius_dimension": return 3;
            case "curved_radial":
            case "curved_orthogonal": return 4;
            default: return -1;
        }
    }

    private static DrawingWriteResult ToolValidationFailure(
        string operation,
        bool apply,
        string message) =>
        new DrawingWriteResult
        {
            Operation = operation,
            Applied = apply,
            Outcome = apply ? WriteOutcome.NotWritten : WriteOutcome.Planned, // refused before the backend
            Backend = "Tool validation",
            Message = message,
        };

    private static DrawingWriteResult? RejectEmptyMutationScope(
        ITeklaModelService model,
        string operation,
        string? objectIds,
        string? types,
        bool useSelection,
        bool apply)
    {
        if (!string.IsNullOrWhiteSpace(objectIds) ||
            !string.IsNullOrWhiteSpace(types) ||
            useSelection)
            return null;
        return new DrawingWriteResult
        {
            Operation = operation,
            Applied = apply,
            Outcome = apply ? WriteOutcome.NotWritten : WriteOutcome.Planned, // refused before the backend
            Backend = model.GetDrawingStatus().Backend,
            Message =
                "Refusing an unscoped drawing-object mutation. Supply objectIds, types, " +
                "or useSelection=true, preview it, then apply.",
        };
    }
}
