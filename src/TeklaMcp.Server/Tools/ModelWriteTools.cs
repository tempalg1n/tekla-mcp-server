using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Low-level write primitives: create beams/columns/plates, modify one part, swap handles,
/// delete by filter. ALL default to preview (apply=false): nothing is written until apply=true.
/// Created/modified objects are tagged with the MCP_ORIGIN UDA for traceability.
/// </summary>
[McpServerToolType]
public static class ModelWriteTools
{
    [McpServerTool(Name = "tekla_create_beam")]
    [Description("Create a beam between two GLOBAL points (mm). Preview unless apply=true.")]
    public static WriteResult CreateBeam(
        ITeklaModelService model,
        [Description("Start X (mm).")] double startX,
        [Description("Start Y (mm).")] double startY,
        [Description("Start Z (mm).")] double startZ,
        [Description("End X (mm).")] double endX,
        [Description("End Y (mm).")] double endY,
        [Description("End Z (mm).")] double endZ,
        [Description("Profile, e.g. 'IPE300'.")] string profile,
        [Description("Material grade, e.g. 'S355J2'. Empty = model default.")] string material = "",
        [Description("Tekla class. Empty = default.")] string @class = "",
        [Description("Object name. Empty = default.")] string name = "",
        [Description("Plane position: MIDDLE, LEFT, RIGHT. Empty = default/matched.")] string? plane = null,
        [Description("Plane offset (mm).")] double? planeOffset = null,
        [Description("Rotation: FRONT, TOP, BACK, BELOW. Empty = default/matched.")] string? rotation = null,
        [Description("Additional rotation offset (degrees).")] double? rotationOffset = null,
        [Description("Depth: MIDDLE, FRONT, BEHIND. Empty = default/matched.")] string? depth = null,
        [Description("Depth offset (mm).")] double? depthOffset = null,
        [Description("Copy the complete Position from this existing part GUID, then apply explicit overrides.")] string? matchPositionGuid = null,
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
        => model.CreateParts(new[]
        {
            new PartSpec
            {
                Kind = "beam",
                Start = new Point3D(startX, startY, startZ),
                End = new Point3D(endX, endY, endZ),
                Profile = profile, Material = material, Class = @class, Name = name,
                Position = ToolHelpers.BuildPosition(
                    plane, planeOffset, rotation, rotationOffset, depth, depthOffset),
                MatchPositionGuid = matchPositionGuid,
            }
        }, apply);

    [McpServerTool(Name = "tekla_create_beams")]
    [Description("Create up to 200 beams in ONE batch. Each item uses PartSpec Start/End/Profile " +
                 "and optional material/class/name/position/matchPositionGuid. Kind is forced to " +
                 "'beam'. Preview unless apply=true.")]
    public static WriteResult CreateBeams(
        ITeklaModelService model,
        [Description("Beam specifications (maximum 200).")] IReadOnlyList<PartSpec> beams,
        [Description("Set true to commit the whole batch. Default false = preview.")] bool apply = false)
    {
        var specs = (beams ?? new List<PartSpec>()).Take(200).ToList();
        foreach (var spec in specs) spec.Kind = "beam";
        var result = model.CreateParts(specs, apply);
        if (beams != null && beams.Count > 200)
            result.Message = "Batch capped at 200 of " + beams.Count + " beam specs.";
        return result;
    }

    [McpServerTool(Name = "tekla_create_column")]
    [Description("Create a vertical column at (x,y) from bottomZ to topZ (mm). Preview unless apply=true.")]
    public static WriteResult CreateColumn(
        ITeklaModelService model,
        [Description("X (mm).")] double x,
        [Description("Y (mm).")] double y,
        [Description("Bottom elevation Z (mm).")] double bottomZ,
        [Description("Top elevation Z (mm).")] double topZ,
        [Description("Profile, e.g. 'HEA300'.")] string profile,
        [Description("Material grade. Empty = default.")] string material = "",
        [Description("Tekla class. Empty = default.")] string @class = "",
        [Description("Object name. Empty = 'COLUMN'.")] string name = "COLUMN",
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
        => model.CreateParts(new[]
        {
            new PartSpec
            {
                Kind = "column",
                Start = new Point3D(x, y, bottomZ),
                End = new Point3D(x, y, topZ),
                Profile = profile, Material = material, Class = @class, Name = name,
            }
        }, apply);

    [McpServerTool(Name = "tekla_create_plate")]
    [Description("Create a contour plate from 3+ GLOBAL points 'x,y,z; x,y,z; ...' and a profile " +
                 "like 'PL20'. Preview unless apply=true.")]
    public static WriteResult CreatePlate(
        ITeklaModelService model,
        [Description("Contour points 'x,y,z; x,y,z; ...' (>= 3).")] string contour,
        [Description("Plate profile, e.g. 'PL20'.")] string profile,
        [Description("Material grade. Empty = default.")] string material = "",
        [Description("Tekla class. Empty = default.")] string @class = "",
        [Description("Object name. Empty = default.")] string name = "",
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
        => model.CreateParts(new[]
        {
            new PartSpec
            {
                Kind = "plate",
                Contour = ToolHelpers.ParsePoints(contour),
                Profile = profile, Material = material, Class = @class, Name = name,
            }
        }, apply);

    [McpServerTool(Name = "tekla_modify_part")]
    [Description("Modify one part by GUID: set any of profile/material/class/name, and/or move its " +
                 "endpoints. Provide newStart/newEnd as 'x,y,z'. Preview unless apply=true.")]
    public static WriteResult ModifyPart(
        ITeklaModelService model,
        [Description("Object GUID.")] string guid,
        [Description("New profile, or empty to keep.")] string? profile = null,
        [Description("New material, or empty to keep.")] string? material = null,
        [Description("New class, or empty to keep.")] string? @class = null,
        [Description("New name, or empty to keep.")] string? name = null,
        [Description("New start point 'x,y,z' (optional).")] string? newStart = null,
        [Description("New end point 'x,y,z' (optional).")] string? newEnd = null,
        [Description("Plane position: MIDDLE, LEFT, RIGHT. Empty = keep/matched.")] string? plane = null,
        [Description("Plane offset (mm), or omitted to keep/match.")] double? planeOffset = null,
        [Description("Rotation: FRONT, TOP, BACK, BELOW. Empty = keep/matched.")] string? rotation = null,
        [Description("Additional rotation offset (degrees).")] double? rotationOffset = null,
        [Description("Depth: MIDDLE, FRONT, BEHIND. Empty = keep/matched.")] string? depth = null,
        [Description("Depth offset (mm), or omitted to keep/match.")] double? depthOffset = null,
        [Description("Copy the complete Position from this existing part GUID, then apply explicit overrides.")] string? matchPositionGuid = null,
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
        => model.ModifyParts(new[]
        {
            new PartModification
            {
                Guid = guid,
                Profile = string.IsNullOrWhiteSpace(profile) ? null : profile,
                Material = string.IsNullOrWhiteSpace(material) ? null : material,
                Class = string.IsNullOrWhiteSpace(@class) ? null : @class,
                Name = string.IsNullOrWhiteSpace(name) ? null : name,
                NewStart = ToolHelpers.ParsePoint(newStart),
                NewEnd = ToolHelpers.ParsePoint(newEnd),
                Position = ToolHelpers.BuildPosition(
                    plane, planeOffset, rotation, rotationOffset, depth, depthOffset),
                MatchPositionGuid = matchPositionGuid,
            }
        }, apply);

    [McpServerTool(Name = "tekla_swap_handles")]
    [Description("Swap the start/end handles of parts matching filters (re-orient wrongly-modeled " +
                 "members). Preview unless apply=true. Capped by limit.")]
    public static WriteResult SwapHandles(
        ITeklaModelService model,
        [Description("Object type, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class.")] string? @class = null,
        [Description("Profile substring.")] string? profile = null,
        [Description("Material substring.")] string? material = null,
        [Description("Name substring.")] string? nameContains = null,
        [Description("Explicit GUID list (comma/semicolon/newline).")] string? guidIn = null,
        [Description("Scope to current UI selection. Default false.")] bool useSelection = false,
        [Description("Safety cap. Default 200.")] int limit = 200,
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
    {
        var targets = model.FindObjects(
            ToolHelpers.BuildQuery(type, @class, profile, material, nameContains, guidIn: guidIn, useSelection: useSelection),
            limit);
        var mods = targets.Select(o => new PartModification { Guid = o.Guid, SwapHandles = true }).ToList();
        return model.ModifyParts(mods, apply);
    }

    [McpServerTool(Name = "tekla_delete_objects")]
    [Description("Delete objects matching filters (or an explicit GUID list). Preview unless " +
                 "apply=true. Capped by limit (default 200).")]
    public static WriteResult DeleteObjects(
        ITeklaModelService model,
        [Description("Object type, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class.")] string? @class = null,
        [Description("Profile substring.")] string? profile = null,
        [Description("Material substring.")] string? material = null,
        [Description("Name substring.")] string? nameContains = null,
        [Description("UDA name for exact match.")] string? udaName = null,
        [Description("Exact UDA value.")] string? udaEquals = null,
        [Description("Explicit GUID list (comma/semicolon/newline).")] string? guidIn = null,
        [Description("Scope to current UI selection. Default false.")] bool useSelection = false,
        [Description("Safety cap. Default 200.")] int limit = 200,
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
        => model.DeleteObjects(
            ToolHelpers.BuildQuery(type, @class, profile, material, nameContains, udaName, udaEquals, guidIn, useSelection),
            apply,
            limit);
}
