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
                 "'nameContains' match as case-insensitive substrings. 'type' accepts friendly " +
                 "aliases: 'Bolt' matches Tekla's 'BoltArray', 'Plate' matches 'ContourPlate'. " +
                 "For bolts, strength grade/standard live in the BOLT_GRADE/BOLT_STANDARD " +
                 "attributes (use attributeName), NOT in material/class. Passing attributeName " +
                 "alone (no value) selects objects that HAVE that attribute set.")]
    public static IReadOnlyList<ModelObjectInfo> FindObjects(
        ITeklaModelService model,
        [Description("Object type, exact match with aliases, e.g. 'Beam', 'ContourPlate'/'Plate', 'Bolt'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '2'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'HEA300'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355'.")] string? material = null,
        [Description("Substring of the object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'RU_FN1_MRK'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS', 'BOLT_GRADE'. Alone (no value) = must be set.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Value the attribute must NOT equal, e.g. BOLT_GRADE != '88'. Requires attributeName set.")] string? attributeNotEquals = null,
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
                AttributeNotEquals = attributeNotEquals,
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
}
