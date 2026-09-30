using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using TeklaMcp.Core.Rendering;
using TSG = Tekla.Structures.Geometry3d;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla;

/// <summary>
/// Live half of <c>tekla_render_schematic</c>: reduce Tekla objects to drawable geometry. Only
/// cheap members are read — <c>Beam.StartPoint/EndPoint</c>, <c>Contour.ContourPoints</c>,
/// <c>BoltGroup.BoltPositions</c>, <c>PolyBeam.GetCenterLine(false)</c> (one call per curved
/// member) — and <c>GetSolid()</c> only as a bounded fallback for other part types. GUIDs are
/// looked up directly; context comes from Tekla's spatial query
/// (<c>ModelObjectSelector.GetObjectsByBoundingBox</c>), not from a whole-model walk.
/// </summary>
public sealed partial class TeklaModelService
{
    /// <summary>GetSolid() calls allowed per scene (fallback geometry for bent/lofted/brep/custom parts).</summary>
    private const int SchematicSolidBudget = 300;

    public SchematicScene GetSchematicScene(SchematicSceneRequest request)
    {
        request = request ?? new SchematicSceneRequest();
        var query = request.Query ?? new ObjectQuery();
        var scene = new SchematicScene { Backend = BackendName };
        try
        {
            var model = GetConnectedModel();
            InGlobalWorkPlane(model, () =>
            {
                var solidBudget = SchematicSolidBudget;
                var focusGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var hasScope = SchematicSceneTools.HasScope(query);

                if (hasScope) CollectFocus(model, query, request, scene, focusGuids, ref solidBudget);

                var focusBox = SchematicSceneTools.BoundsOf(scene.Items);
                var region = request.Region ??
                             (focusBox != null ? SchematicSceneTools.Expand(focusBox, Math.Max(0, request.ContextMarginMm)) : null);
                if (request.IncludeContext && (region != null || !hasScope))
                {
                    CollectContext(model, region, request, scene, focusGuids, ref solidBudget);
                    scene.Region = region;
                }

                if (request.IncludeGrids) scene.Grids = ReadGridDefinitions(model, scene.Warnings);
                if (solidBudget <= 0)
                    scene.Warnings.Add("The solid budget (" + SchematicSolidBudget + " GetSolid calls) ran out: further " +
                                       "bent/lofted/brep/custom parts were skipped. Narrow the scope to see them.");
                return true;
            });
        }
        catch (Exception ex)
        {
            scene.Message = ErrorText.Flatten(ex);
        }
        return scene;
    }

    private static void CollectFocus(
        TSM.Model model, ObjectQuery query, SchematicSceneRequest request, SchematicScene scene,
        HashSet<string> focusGuids, ref int solidBudget)
    {
        var maxFocus = Math.Max(1, request.MaxFocusObjects);
        var guids = query.GuidIn.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byGuid = guids.Count > 0 && !query.UseSelection;

        IEnumerable<TSM.ModelObject> source;
        if (byGuid)
        {
            // Direct lookups: a GUID list must never cost a whole-model scan.
            var found = new List<TSM.ModelObject>();
            foreach (var guid in guids)
            {
                var mo = TrySelectObjectByGuid(model, guid);
                if (mo == null) scene.MissingGuids.Add(guid);
                else found.Add(mo);
            }
            source = found;
        }
        else
        {
            source = EnumerateSource(model, query, partsFallback: true);
        }

        foreach (var mo in source)
        {
            if (!byGuid)
            {
                if (request.MaxScanObjects is int cap && cap > 0 && scene.ScannedObjects >= cap)
                {
                    scene.ScanTruncated = true;
                    break;
                }
                scene.ScannedObjects++;
            }

            var info = MapBasic(mo);
            if (info == null || !Matches(info, query) || !MatchesUda(mo, query)) continue;

            var item = ReadSchematicItem(mo, info, ref solidBudget);
            if (item != null && request.Region != null)
            {
                // An explicit region limits the focus too ("columns in this bay").
                var bounds = SchematicSceneTools.BoundsOf(item);
                if (bounds == null || !SchematicSceneTools.Intersects(bounds, request.Region)) continue;
            }
            scene.FocusMatched++;
            if (focusGuids.Count >= maxFocus)
            {
                // Stop at cap + 1: FocusMatched then means "more than the cap", not an exact count.
                scene.FocusTruncated = true;
                break;
            }
            if (item == null)
            {
                scene.SkippedNoGeometry++;
                continue;
            }
            if (mo is TSM.Part part)
            {
                var pos = "";
                if (part.GetReportProperty("ASSEMBLY_POS", ref pos) && !string.IsNullOrEmpty(pos)) item.AssemblyPos = pos;
            }
            item.Focus = true;
            scene.Items.Add(item);
            focusGuids.Add(item.Guid);
        }
    }

    private static void CollectContext(
        TSM.Model model, Box3D? region, SchematicSceneRequest request, SchematicScene scene,
        HashSet<string> focusGuids, ref int solidBudget)
    {
        var max = Math.Max(0, request.MaxContextObjects);
        IEnumerable<TSM.ModelObject> source;
        if (region != null)
        {
            // Tekla's spatial index; the boxes it uses are approximate, so every hit is re-checked below.
            source = Drain(model.GetModelObjectSelector()
                .GetObjectsByBoundingBox(ToPoint(region.Min), ToPoint(region.Max)));
        }
        else
        {
            source = EnumerateParts(model);
            if (request.IncludeBolts) source = source.Concat(EnumerateBolts(model));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var mo in source)
        {
            if (!(mo is TSM.Part) && !(request.IncludeBolts && mo is TSM.BoltGroup)) continue;
            var guid = mo.Identifier.GUID.ToString();
            if (focusGuids.Contains(guid) || !seen.Add(guid)) continue;

            var info = MapBasic(mo);
            if (info == null) continue;
            var item = ReadSchematicItem(mo, info, ref solidBudget);
            if (item == null) continue;
            if (region != null)
            {
                var bounds = SchematicSceneTools.BoundsOf(item);
                if (bounds == null || !SchematicSceneTools.Intersects(bounds, region)) continue;
            }
            if (added >= max)
            {
                scene.ContextTruncated = true;
                break;
            }
            scene.Items.Add(item);
            added++;
        }
    }

    private static IEnumerable<TSM.ModelObject> EnumerateBolts(TSM.Model model)
    {
        foreach (var type in new[]
                 {
                     TSM.ModelObject.ModelObjectEnum.BOLT_ARRAY,
                     TSM.ModelObject.ModelObjectEnum.BOLT_CIRCLE,
                     TSM.ModelObject.ModelObjectEnum.BOLT_XYLIST,
                 })
            foreach (var mo in Drain(model.GetModelObjectSelector().GetAllObjectsWithType(type)))
                yield return mo;
    }

    /// <summary>
    /// Drawable geometry of one object, or null when it has none (or the solid budget is spent).
    /// Must run in the global work plane: every point member is relative to the current plane.
    /// </summary>
    private static SchematicItem? ReadSchematicItem(TSM.ModelObject mo, ModelObjectInfo info, ref int solidBudget)
    {
        var item = new SchematicItem
        {
            Guid = info.Guid,
            Id = info.Id,
            Type = info.Type,
            Name = info.Name,
            Profile = info.Profile,
            Material = info.Material,
            Class = info.Class,
        };

        try
        {
            switch (mo)
            {
                case TSM.Beam beam:
                    item.Shape = "line";
                    item.GeometrySource = "reference-line";
                    item.Points.Add(ToPoint3D(beam.StartPoint));
                    item.Points.Add(ToPoint3D(beam.EndPoint));
                    return item;

                case TSM.PolyBeam polyBeam:
                {
                    item.Shape = "line";
                    // GetCenterLine is a segmented polyline — useless for dimensions, right for drawing.
                    var centerLine = polyBeam.GetCenterLine(false);
                    if (centerLine != null && centerLine.Count >= 2)
                    {
                        item.GeometrySource = "centerline";
                        AddModelPoints(item.Points, centerLine);
                        return item;
                    }
                    item.GeometrySource = "contour-points";
                    AddModelPoints(item.Points, polyBeam.Contour?.ContourPoints);
                    return item.Points.Count >= 2 ? item : null;
                }

                case TSM.ContourPlate plate:
                    item.Shape = "polygon";
                    item.GeometrySource = "contour";
                    AddModelPoints(item.Points, plate.Contour?.ContourPoints);
                    return item.Points.Count >= 2 ? item : null;

                case TSM.BoltGroup bolts:
                    item.Shape = "points";
                    item.GeometrySource = "bolt-positions";
                    AddModelPoints(item.Points, bolts.BoltPositions);
                    return item.Points.Count > 0 ? item : null;

                case TSM.Part part:
                {
                    // BentPlate, LoftedPlate, SpiralBeam, Brep, CustomPart: no cheap geometry.
                    if (solidBudget <= 0) return null;
                    solidBudget--;
                    var solid = part.GetSolid();
                    if (solid == null) return null;
                    item.Shape = "box";
                    item.GeometrySource = "solid-aabb";
                    item.Box = new Box3D(ToPoint3D(solid.MinimumPoint), ToPoint3D(solid.MaximumPoint));
                    return item;
                }
            }
        }
        catch (Exception ex)
        {
            // TODO(windows): an object that faults during remoting materialization is skipped,
            // like the drawing enumeration does; the scene reports it as "no drawable geometry".
            Console.Error.WriteLine("[tekla] schematic: skipped " + info.Type + " " + info.Guid + ": " + ErrorText.Flatten(ex));
        }
        return null;
    }

    /// <summary>
    /// Every grid with its own coordinate system, real labels and Tekla's coordinate semantics.
    /// Ordered largest first so label lookups prefer the main grid. Run in the global work plane.
    /// </summary>
    private static List<SchematicGrid> ReadGridDefinitions(TSM.Model model, List<string>? warnings)
    {
        var grids = new List<SchematicGrid>();
        var en = model.GetModelObjectSelector().GetAllObjectsWithType(TSM.ModelObject.ModelObjectEnum.GRID);
        while (en.MoveNext())
        {
            if (!(en.Current is TSM.Grid grid)) continue;
            var definition = new SchematicGrid { Id = grid.Identifier.ID };
            try
            {
                var cs = grid.GetCoordinateSystem();
                definition.Origin = ToPoint3D(cs.Origin);
                definition.AxisX = GridMath.Normalize(new Point3D(cs.AxisX.X, cs.AxisX.Y, cs.AxisX.Z), new Point3D(1, 0, 0));
                definition.AxisY = GridMath.Normalize(new Point3D(cs.AxisY.X, cs.AxisY.Y, cs.AxisY.Z), new Point3D(0, 1, 0));
            }
            catch (Exception ex)
            {
                warnings?.Add("Grid " + grid.Identifier.ID + ": coordinate system unavailable, global origin assumed (" +
                              ErrorText.Flatten(ex) + ").");
            }
            definition.LinesX = GridMath.BuildAxis(
                GridMath.ParsePositions(grid.CoordinateX, relative: true), GridMath.SplitLabels(grid.LabelX), "X");
            definition.LinesY = GridMath.BuildAxis(
                GridMath.ParsePositions(grid.CoordinateY, relative: true), GridMath.SplitLabels(grid.LabelY), "Y");
            definition.Levels = GridMath.BuildAxis(
                GridMath.ParsePositions(grid.CoordinateZ, relative: false), GridMath.SplitLabels(grid.LabelZ), "Z");
            grids.Add(definition);
        }
        return grids.OrderByDescending(g => g.LinesX.Count + g.LinesY.Count).ToList();
    }

    private static void AddModelPoints(List<Point3D> sink, IEnumerable? points)
    {
        if (points == null) return;
        foreach (var p in points)
            if (p is TSG.Point point) sink.Add(ToPoint3D(point));
    }

    private static Point3D ToPoint3D(TSG.Point p) =>
        new Point3D(Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1));
}
