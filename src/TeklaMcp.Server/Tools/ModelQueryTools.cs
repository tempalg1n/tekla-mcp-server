using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>Tools for listing, filtering and looking up individual model objects.</summary>
[McpServerToolType]
public static class ModelQueryTools
{
    [McpServerTool(Name = "tekla_list_objects")]
    [Description("List model objects with their core properties (guid, id, type, name, class, " +
                 "profile, material, length, weight, coordinates). Use 'limit' to cap the result — " +
                 "models can hold tens of thousands of objects.")]
    public static IReadOnlyList<ModelObjectInfo> ListObjects(
        ITeklaModelService model,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Maximum number of objects to return. Default 100.")] int limit = 100)
        => useSelection
            ? model.FindObjects(new ObjectQuery { UseSelection = true }, limit)
            : model.GetAllObjects(limit);

    [McpServerTool(Name = "tekla_find_objects")]
    [Description("Find objects matching optional filters. Any argument left empty is ignored. " +
                 "'type' and 'class' match exactly (case-insensitive); 'profile', 'material' and " +
                 "'nameContains' match as case-insensitive substrings. Supports UDA and generic " +
                 "attribute filters to avoid guessing where a value is stored.")]
    public static IReadOnlyList<ModelObjectInfo> FindObjects(
        ITeklaModelService model,
        [Description("Object type, exact match, e.g. 'Beam', 'ContourPlate', 'Bolt'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '2'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'HEA300'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355'.")] string? material = null,
        [Description("Substring of the object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'RU_FN1_MRK'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS' or 'RU_FN1_MRK'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")] bool useSelection = false,
        [Description("Maximum number of objects to return. Default 100.")] int limit = 100)
        => model.FindObjects(
            new ObjectQuery
            {
                Type = type,
                Class = @class,
                Profile = profile,
                Material = material,
                NameContains = nameContains,
                UdaName = udaName,
                UdaEquals = udaEquals,
                AttributeName = attributeName,
                AttributeEquals = attributeEquals,
                AttributeContains = attributeContains,
                UseSelection = useSelection,
            },
            limit);

    [McpServerTool(Name = "tekla_get_object_by_guid")]
    [Description("Look up a single object by its Tekla GUID. Returns null if not found.")]
    public static ModelObjectInfo? GetObjectByGuid(
        ITeklaModelService model,
        [Description("The Tekla GUID, e.g. '0a1b2c3d-1234-5678-9abc-def012345678'.")] string guid)
        => model.GetObjectByGuid(guid);

    [McpServerTool(Name = "tekla_get_selected_objects")]
    [Description("Return the objects the user currently has selected in the Tekla Structures UI. " +
                 "Useful for 'analyze what I have selected' workflows. (With the Mock backend this " +
                 "returns a few sample objects.)")]
    public static IReadOnlyList<ModelObjectInfo> GetSelectedObjects(ITeklaModelService model)
        => model.GetSelectedObjects();

    [McpServerTool(Name = "tekla_get_solid_bbox")]
    [Description("Return geometry snapshots with solid world AABB (min/max) for explicit part " +
                 "GUIDs or the current UI selection. GetSolid is expensive, so the result is capped.")]
    public static IReadOnlyList<ModelObjectInfo> GetSolidBoundingBoxes(
        ITeklaModelService model,
        [Description("Part GUIDs, comma/semicolon/newline separated. Ignored with useSelection=true.")]
        string? guids = null,
        [Description("Scope to the current UI selection. Default false.")] bool useSelection = false,
        [Description("Safety cap. Default 100, max 500.")] int limit = 100)
    {
        var parsed = ToolHelpers.ParseList(guids);
        if (!useSelection && parsed.Count == 0) return new List<ModelObjectInfo>();
        return model.FindObjects(
            new ObjectQuery
            {
                GuidIn = useSelection ? new List<string>() : parsed,
                UseSelection = useSelection,
            },
            limit < 1 ? 1 : (limit > 500 ? 500 : limit));
    }

    [McpServerTool(Name = "tekla_list_control_lines")]
    [Description("List ControlLine objects with global start/end coordinates. Supports current " +
                 "selection scope and a result cap.")]
    public static IReadOnlyList<ModelObjectInfo> ListControlLines(
        ITeklaModelService model,
        [Description("Scope to current UI selection. Default false.")] bool useSelection = false,
        [Description("Maximum lines. Default 200.")] int limit = 200)
        => model.FindObjects(
            new ObjectQuery { Type = "ControlLine", UseSelection = useSelection },
            limit);
}
