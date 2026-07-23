using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>Semantic and geometric inspection of IFC/reference-model objects.</summary>
[McpServerToolType]
public static class ModelReferenceTools
{
    [McpServerTool(Name = "tekla_get_reference_geometry")]
    [Description(
        "Get semantic metadata and WORLD geometry for IFC/reference-model objects: external IFC " +
        "GUID, entity (e.g. IFCWINDOW), OverallWidth/Height, world AABB, placement (origin + " +
        "X/Y/Z axes in GLOBAL mm) and capped face polygons. When the Tekla API cannot deliver " +
        "geometry, the reference IFC file is parsed directly (placementSource='ifc-file'; " +
        "aabbSource says whether the AABB is exact or a placement-based estimate). Address " +
        "objects by integer id, by IFC GlobalId via externalGuids, or set useSelection=true. " +
        "Best-effort across IFC exporters.")]
    public static IReadOnlyList<ReferenceGeometryInfo> GetReferenceGeometry(
        ITeklaModelService model,
        [Description("Comma/semicolon/newline-separated Tekla integer IDs. Ignored with useSelection=true.")]
        string? ids = null,
        [Description("Comma/semicolon/newline-separated IFC GlobalIds (22-char, e.g. '0VZkpIecn7$9mG$7iL8u45'). " +
                     "Takes precedence over ids/useSelection.")]
        string? externalGuids = null,
        [Description("Read currently selected ReferenceModelObject instances. Default true.")]
        bool useSelection = true,
        [Description("Maximum reference objects (default 20, max 100).")]
        int maxObjects = 20,
        [Description("Maximum face polygons returned per object (default 100, max 1000; 0 = metadata only).")]
        int maxFacesPerObject = 100,
        [Description("Maximum face polygons across the whole response (default 1000, max 5000).")]
        int maxTotalFaces = 1000,
        [Description("Maximum face vertices across the whole response (default 20000, max 100000).")]
        int maxTotalPoints = 20000)
    {
        var parsedIds = ToolHelpers.ParseList(ids)
            .Select(s => int.TryParse(s, out var id) ? (int?)id : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        return model.GetReferenceGeometry(
            parsedIds,
            useSelection,
            maxObjects < 0 ? 0 : (maxObjects > 100 ? 100 : maxObjects),
            maxFacesPerObject < 0 ? 0 : (maxFacesPerObject > 1000 ? 1000 : maxFacesPerObject),
            maxTotalFaces < 0 ? 0 : (maxTotalFaces > 5000 ? 5000 : maxTotalFaces),
            maxTotalPoints < 0 ? 0 : (maxTotalPoints > 100000 ? 100000 : maxTotalPoints),
            ToolHelpers.ParseList(externalGuids));
    }
}
