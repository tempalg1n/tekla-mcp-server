using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// High-level, declarative operations so agents do NOT need to script loops of primitives:
/// frame/column-grid/beam generators, plus "find &amp; fix" operations for crooked or
/// wrongly-oriented columns. All compose the write primitives, so all default to preview
/// (apply=false) and tag changes with MCP_ORIGIN.
/// </summary>
[McpServerToolType]
public static class ModelGeneratorTools
{
    [McpServerTool(Name = "tekla_create_beam_between_grids")]
    [Description("Create a beam between two grid intersections at an elevation: from " +
                 "(gridXFrom, gridY) to (gridXTo, gridY) at z. Resolves axis labels via the model " +
                 "grids — e.g. 'between axes 1 and 2 along axis Д at +6.000'. Preview unless apply=true.")]
    public static WriteResult CreateBeamBetweenGrids(
        ITeklaModelService model,
        [Description("X-axis label of the start, e.g. '1'.")] string gridXFrom,
        [Description("X-axis label of the end, e.g. '2'.")] string gridXTo,
        [Description("Shared Y-axis label, e.g. 'Д'.")] string gridY,
        [Description("Elevation Z (mm).")] double z,
        [Description("Profile, e.g. 'IPE300'.")] string profile,
        [Description("Material grade. Empty = default.")] string material = "",
        [Description("Tekla class. Empty = default.")] string @class = "",
        [Description("Object name. Empty = default.")] string name = "",
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
    {
        var a = model.ResolvePoint(gridXFrom, gridY, z);
        var b = model.ResolvePoint(gridXTo, gridY, z);
        if (!a.Resolved || !b.Resolved)
            return new WriteResult
            {
                Operation = "create_beam_between_grids",
                Message = $"Could not resolve grids. {a.Message} {b.Message}".Trim(),
            };

        return ToolHelpers.FailIfNothingApplied(model.CreateParts(new[]
        {
            new PartSpec
            {
                Kind = "beam",
                Start = new Point3D(a.X, a.Y, a.Z),
                End = new Point3D(b.X, b.Y, b.Z),
                Profile = profile, Material = material, Class = @class, Name = name,
            }
        }, apply));
    }

    [McpServerTool(Name = "tekla_create_column_grid")]
    [Description("Create vertical columns at every intersection of given X and Y coordinate lists " +
                 "(mm), from bottomZ to topZ. Coordinate lists like '0,6000,12000'. Preview unless apply=true.")]
    public static WriteResult CreateColumnGrid(
        ITeklaModelService model,
        [Description("X coordinates (mm), e.g. '0,6000,12000'.")] string xCoords,
        [Description("Y coordinates (mm), e.g. '0,6000'.")] string yCoords,
        [Description("Bottom elevation Z (mm).")] double bottomZ,
        [Description("Top elevation Z (mm).")] double topZ,
        [Description("Column profile, e.g. 'HEA300'.")] string profile,
        [Description("Material grade. Empty = default.")] string material = "",
        [Description("Tekla class. Empty = default.")] string @class = "",
        [Description("Object name. Empty = 'COLUMN'.")] string name = "COLUMN",
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
    {
        var xs = ToolHelpers.ParseNums(xCoords);
        var ys = ToolHelpers.ParseNums(yCoords);
        var specs = new List<PartSpec>();
        foreach (var x in xs)
            foreach (var y in ys)
                specs.Add(new PartSpec
                {
                    Kind = "column",
                    Start = new Point3D(x, y, bottomZ),
                    End = new Point3D(x, y, topZ),
                    Profile = profile, Material = material, Class = @class, Name = name,
                });
        return ToolHelpers.FailIfNothingApplied(model.CreateParts(specs, apply));
    }

    [McpServerTool(Name = "tekla_generate_frame")]
    [Description("Generate a rectangular steel frame: columns at a grid of bays plus connecting " +
                 "beams at each story level. baysX/baysY are the number of bays (columns = bays+1) " +
                 "along each direction. Preview unless apply=true — preview returns the full plan/count.")]
    public static WriteResult GenerateFrame(
        ITeklaModelService model,
        [Description("Frame origin X (mm).")] double originX,
        [Description("Frame origin Y (mm).")] double originY,
        [Description("Base elevation Z (mm).")] double baseZ,
        [Description("Number of bays along X.")] int baysX,
        [Description("Bay width along X (mm).")] double bayWidthX,
        [Description("Number of bays along Y.")] int baysY,
        [Description("Bay width along Y (mm).")] double bayWidthY,
        [Description("Story height (mm).")] double storyHeight,
        [Description("Number of stories.")] int stories,
        [Description("Column profile, e.g. 'HEA300'.")] string columnProfile,
        [Description("Beam profile, e.g. 'IPE300'.")] string beamProfile,
        [Description("Material grade. Empty = default.")] string material = "",
        [Description("Tekla class. Empty = default.")] string @class = "",
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
    {
        baysX = Math.Max(0, baysX);
        baysY = Math.Max(0, baysY);
        stories = Math.Max(1, stories);

        var xs = Enumerable.Range(0, baysX + 1).Select(i => originX + i * bayWidthX).ToList();
        var ys = Enumerable.Range(0, baysY + 1).Select(j => originY + j * bayWidthY).ToList();
        var topZ = baseZ + storyHeight * stories;
        var specs = new List<PartSpec>();

        // Columns at every grid intersection, full height.
        foreach (var x in xs)
            foreach (var y in ys)
                specs.Add(new PartSpec
                {
                    Kind = "column",
                    Start = new Point3D(x, y, baseZ),
                    End = new Point3D(x, y, topZ),
                    Profile = columnProfile, Material = material, Class = @class, Name = "COLUMN",
                });

        // Beams at every story level, spanning adjacent columns in both directions.
        for (var s = 1; s <= stories; s++)
        {
            var z = baseZ + storyHeight * s;

            foreach (var y in ys)
                for (var i = 0; i < xs.Count - 1; i++)
                    specs.Add(Beam(xs[i], y, z, xs[i + 1], y, z, beamProfile, material, @class));

            foreach (var x in xs)
                for (var j = 0; j < ys.Count - 1; j++)
                    specs.Add(Beam(x, ys[j], z, x, ys[j + 1], z, beamProfile, material, @class));
        }

        var result = model.CreateParts(specs, apply);
        result.Operation = "generate_frame";
        return ToolHelpers.FailIfNothingApplied(result);
    }

    [McpServerTool(Name = "tekla_straighten_columns")]
    [Description("Find near-vertical members whose top is not directly above the bottom (out of " +
                 "plumb beyond toleranceMm) and make them vertical by aligning both ends over the " +
                 "bottom point's X/Y. Use for 'fix crooked columns'. Preview unless apply=true.")]
    public static WriteResult StraightenColumns(
        ITeklaModelService model,
        [Description("Object type filter, e.g. 'Beam' or 'Column'. Empty = any.")] string? type = null,
        [Description("Tekla class filter.")] string? @class = null,
        [Description("Profile substring filter.")] string? profile = null,
        [Description("Name substring filter.")] string? nameContains = null,
        [Description("Out-of-plumb tolerance (mm). Members within this are left alone. Default 5.")] double toleranceMm = 5,
        [Description("Scope to current UI selection. Default false.")] bool useSelection = false,
        [Description("Safety cap. Default 500.")] int limit = 500,
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
    {
        var targets = model.FindObjects(
            ToolHelpers.BuildQuery(type, @class, profile, nameContains: nameContains, useSelection: useSelection),
            limit);

        var mods = new List<PartModification>();
        foreach (var o in targets)
        {
            if (!ToolHelpers.HasEnds(o)) continue;
            var bottomIsStart = o.StartZ!.Value <= o.EndZ!.Value;
            var baseX = bottomIsStart ? o.StartX!.Value : o.EndX!.Value;
            var baseY = bottomIsStart ? o.StartY!.Value : o.EndY!.Value;

            var offset = ToolHelpers.Hypot(o.EndX!.Value - o.StartX!.Value, o.EndY!.Value - o.StartY!.Value);
            if (offset <= toleranceMm) continue; // already plumb

            mods.Add(new PartModification
            {
                Guid = o.Guid,
                NewStart = new Point3D(baseX, baseY, o.StartZ!.Value),
                NewEnd = new Point3D(baseX, baseY, o.EndZ!.Value),
            });
        }

        var result = model.ModifyParts(mods, apply);
        result.Operation = "straighten_columns";
        return ToolHelpers.FailIfNothingApplied(result);
    }

    [McpServerTool(Name = "tekla_fix_column_handles")]
    [Description("Find near-vertical members modeled with inverted handles (start handle above the " +
                 "end handle) and swap their handles so the bottom handle is at the lower point. Use " +
                 "for 'fix flipped columns' (modeled as beams). Preview unless apply=true.")]
    public static WriteResult FixColumnHandles(
        ITeklaModelService model,
        [Description("Object type filter, e.g. 'Beam'. Empty = any.")] string? type = null,
        [Description("Tekla class filter.")] string? @class = null,
        [Description("Profile substring filter.")] string? profile = null,
        [Description("Name substring filter.")] string? nameContains = null,
        [Description("How vertical a member must be to count as a column: |dz| must exceed the " +
                     "horizontal run. Default heuristic; no extra param needed.")] double verticalRatio = 1.0,
        [Description("Scope to current UI selection. Default false.")] bool useSelection = false,
        [Description("Safety cap. Default 500.")] int limit = 500,
        [Description("Set true to commit. Default false = preview.")] bool apply = false)
    {
        var targets = model.FindObjects(
            ToolHelpers.BuildQuery(type, @class, profile, nameContains: nameContains, useSelection: useSelection),
            limit);

        var mods = new List<PartModification>();
        foreach (var o in targets)
        {
            if (!ToolHelpers.HasEnds(o)) continue;
            var dz = Math.Abs(o.EndZ!.Value - o.StartZ!.Value);
            var dxy = ToolHelpers.Hypot(o.EndX!.Value - o.StartX!.Value, o.EndY!.Value - o.StartY!.Value);
            if (dz <= dxy * verticalRatio) continue;   // not vertical enough to be a column
            if (o.StartZ!.Value <= o.EndZ!.Value) continue; // already bottom-up

            mods.Add(new PartModification { Guid = o.Guid, SwapHandles = true });
        }

        var result = model.ModifyParts(mods, apply);
        result.Operation = "fix_column_handles";
        return ToolHelpers.FailIfNothingApplied(result);
    }

    private static PartSpec Beam(double x1, double y1, double z1, double x2, double y2, double z2,
                                 string profile, string material, string @class) =>
        new PartSpec
        {
            Kind = "beam",
            Start = new Point3D(x1, y1, z1),
            End = new Point3D(x2, y2, z2),
            Profile = profile, Material = material, Class = @class, Name = "BEAM",
        };
}
