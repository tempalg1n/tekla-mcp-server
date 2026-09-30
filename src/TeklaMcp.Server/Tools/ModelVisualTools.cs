using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Core.Rendering;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Pictures for agents. Both tools answer with an MCP image block (the agent actually sees it)
/// plus a JSON text block that maps what is on the picture back to GUIDs and model millimetres.
/// </summary>
[McpServerToolType]
public static class ModelVisualTools
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Cyrillic view names and grid labels stay readable in the legend.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new CompactDoubleConverter() },
    };

    /// <summary>
    /// On net48 System.Text.Json writes doubles with 17 significant digits ("98.813999999999993"
    /// for 98.814) — noise in every coordinate the agent reads. The values are already rounded to
    /// what they mean, so write them short.
    /// </summary>
    private sealed class CompactDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) writer.WriteNullValue();
            else if (System.Math.Abs(value) < 1e15) writer.WriteNumberValue(System.Math.Round((decimal)value, 4));
            else writer.WriteNumberValue(value);
        }
    }

    [McpServerTool(Name = "tekla_render_schematic")]
    [Description(
        "BETA — the drawing is an early version: on dense models it can look cluttered, and it is a simplified " +
        "sketch, not a rendering. If the picture does not answer your question, narrow the scope (guids, filters, " +
        "region) or use tekla_capture_view, and tell the user what you could not see. " +
        "Draw a SCHEMATIC PICTURE of (part of) the model and return it as an image you can look at, plus a JSON " +
        "legend. Read-only, no side effects in Tekla. Use it to SEE what you are working with: where objects are, " +
        "what connects to what, what a filter matched or a write created — instead of guessing from coordinates. " +
        "Scope = focus objects (coloured, numbered): guids, useSelection and/or the usual filters (type, class, " +
        "profile, material, nameContains, uda*/attribute*; a filter scans physical parts unless 'type' names " +
        "another type). Parts around them (focus box + contextMarginMm) are drawn thin grey as context. No scope = " +
        "overview of the whole model (or of 'region'), coloured by class — on big models give a region or a filter. " +
        "view: iso (default, from +X-Y+Z), top (plan), front (looking +Y), back, left, right, bottom, " +
        "iso_sw/iso_ne/iso_nw, or 'dx,dy,dz' = the direction the camera looks along (the same convention as " +
        "camera.direction from tekla_capture_view). The picture shows grid axes with their real labels, levels in " +
        "elevations, mm coordinate ticks in top/front/back/left/right views and a scale bar. JSON: legend[] maps " +
        "each number to guid/type/profile; pixelToModel converts a pixel to model mm; colors maps colours to classes. " +
        "SIMPLIFIED geometry: beams/columns are reference lines (a column in plan is a square), plates contours, " +
        "curved beams centerlines, other parts bounding boxes, bolts crosses (includeBolts); no profiles, cuts or " +
        "welds — use tekla_capture_view for the real look. Check focusTruncated/contextTruncated/missingGuids/" +
        "warnings before concluding that something is absent. One picture costs about 1-1.6k tokens.")]
    public static IEnumerable<ContentBlock> RenderSchematic(
        ITeklaModelService model,
        [Description("iso (default) | top | front | back | left | right | bottom | iso_sw | iso_ne | iso_nw | 'dx,dy,dz' viewing direction.")]
        string? view = "iso",
        [Description("Focus objects by GUID, comma/semicolon/newline separated (looked up directly, no model scan).")]
        string? guids = null,
        [Description("Focus = the current Tekla UI selection.")] bool useSelection = false,
        [Description("Focus filter: object type, exact, e.g. 'Beam', 'ContourPlate', 'BoltArray'.")] string? type = null,
        [Description("Focus filter: Tekla class, exact.")] string? @class = null,
        [Description("Focus filter: profile substring.")] string? profile = null,
        [Description("Focus filter: material substring.")] string? material = null,
        [Description("Focus filter: name substring.")] string? nameContains = null,
        [Description("Focus filter: UDA name (with udaEquals or udaIsEmpty).")] string? udaName = null,
        [Description("Focus filter: exact UDA value.")] string? udaEquals = null,
        [Description("Focus filter: UDA unset or blank.")] bool udaIsEmpty = false,
        [Description("Focus filter: generic attribute/report/UDA name.")] string? attributeName = null,
        [Description("Focus filter: exact value for attributeName.")] string? attributeEquals = null,
        [Description("Focus filter: substring value for attributeName.")] string? attributeContains = null,
        [Description("Optional region 'minX,minY,minZ,maxX,maxY,maxZ' (global mm). Limits the focus to objects inside it " +
                     "and frames the picture; without a scope everything in it is drawn.")]
        string? region = null,
        [Description("Draw surrounding parts as grey context. Default true.")] bool includeContext = true,
        [Description("How far around the focus objects context is collected, mm. Default 3000.")] double contextMarginMm = 3000,
        [Description("Text next to focus objects: number (default, see legend) | id | mark | profile | name | none.")]
        string labels = "number",
        [Description("Colour by: class (Tekla-like class colours, default) | type | profile | material | none.")]
        string colorBy = "class",
        [Description("Draw grid axes and levels. Default true.")] bool showGrids = true,
        [Description("Mark start (yellow) and end (magenta) of focus members, like Tekla handles. Default false.")]
        bool showHandles = false,
        [Description("Also draw bolt positions as context. Default false.")] bool includeBolts = false,
        [Description("Picture width in px (320-2400). Default 1280.")] int width = 1280,
        [Description("Picture height in px (240-2400). Default 960.")] int height = 960,
        [Description("Cap on focus objects. Default 300, max 2000.")] int maxObjects = 300,
        [Description("Cap on context objects (an overview is all context). Default 20000 (~6 s live), max 50000.")]
        int maxContextObjects = 20000,
        [Description("Optional cap on objects examined by a filter scan (huge models).")] int? maxScanObjects = null)
    {
        Box3D? regionBox = null;
        if (!string.IsNullOrWhiteSpace(region))
        {
            if (!SchematicSceneTools.TryParseBox(region, out var parsed, out var regionError))
                throw new McpException(regionError);
            regionBox = parsed;
        }

        var query = new ObjectQuery
        {
            Type = type,
            Class = @class,
            Profile = profile,
            Material = material,
            NameContains = nameContains,
            UdaName = udaName,
            UdaEquals = udaEquals,
            UdaIsEmpty = udaIsEmpty,
            AttributeName = attributeName,
            AttributeEquals = attributeEquals,
            AttributeContains = attributeContains,
            GuidIn = ToolHelpers.ParseList(guids),
            UseSelection = useSelection,
        };

        var scene = model.GetSchematicScene(new SchematicSceneRequest
        {
            Query = query,
            MaxFocusObjects = Clamp(maxObjects, 1, 2000),
            IncludeContext = includeContext,
            ContextMarginMm = contextMarginMm < 0 ? 0 : contextMarginMm,
            MaxContextObjects = Clamp(maxContextObjects, 0, 50000),
            Region = regionBox,
            IncludeBolts = includeBolts,
            IncludeGrids = showGrids,
            MaxScanObjects = maxScanObjects is int cap && cap > 0 ? cap : (int?)null,
        });

        if (SchematicSceneTools.HasScope(query) && scene.FocusMatched == 0 && regionBox == null)
        {
            var why = "No objects matched the scope";
            if (scene.MissingGuids.Count > 0) why += "; GUIDs not in the model: " + string.Join(", ", scene.MissingGuids.Take(20));
            if (scene.ScannedObjects > 0) why += "; scanned " + scene.ScannedObjects + " objects" + (scene.ScanTruncated ? " (maxScanObjects reached)" : "");
            if (!string.IsNullOrWhiteSpace(scene.Message)) why += ". " + scene.Message;
            throw new McpException(why + ".");
        }

        var render = SchematicRenderer.Render(scene, new SchematicRenderOptions
        {
            View = string.IsNullOrWhiteSpace(view) ? "iso" : view!,
            Width = width,
            Height = height,
            Labels = labels,
            ColorBy = colorBy,
            ShowGrids = showGrids,
            ShowHandles = showHandles,
        });
        if (!render.Rendered || render.Image == null)
            throw new McpException(render.Message ?? "Nothing to draw.");
        if (SchematicSceneTools.HasScope(query) && scene.FocusMatched == 0)
            render.Warnings.Insert(0, "No object matched the scope inside the region; the picture shows the region only.");

        var image = render.Image;
        render.Image = null;
        return Answer(image, render);
    }

    [McpServerTool(Name = "tekla_capture_view")]
    [Description(
        "SCREENSHOT of a LIVE Tekla model view — exactly what Tekla renders (real profiles, bolts, welds, cuts, " +
        "reference models, class colours) — returned as an image you can look at, plus JSON (view, camera, legend). " +
        "Default (no guids, no direction): captures the ACTIVE view as it is, without touching it. With guids or " +
        "useSelection it zooms to those objects, colours them red and makes everything else semi-transparent " +
        "(zoom/highlight/ghostOthers); labels=true also draws their numbers in the view (active view only; legend " +
        "maps number -> guid). direction rotates a 3D view, same values as tekla_render_schematic's view. These " +
        "change the USER'S screen for about a second: restore=true (default) puts camera and colours back right " +
        "after the capture; restore=false leaves the view zoomed/highlighted so you can show the user what you mean " +
        "— tell them when you do. Only a view visible on screen can be captured (the active view, or tiled views): " +
        "a view hidden behind another view, a minimized Tekla or no open view fails with the list of open views — " +
        "ask the user to bring the view forward. Only Tekla's own view window is captured, never other windows. The " +
        "picture carries no GUIDs: highlight what you need to identify, or use tekla_render_schematic for a numbered " +
        "map. Windows + live Tekla only; the mock backend returns a schematic stand-in labelled as such " +
        "(source='mock-schematic'). One picture costs about 1-1.6k tokens; jpeg (default) is small, png is crisp.")]
    public static IEnumerable<ContentBlock> CaptureView(
        ITeklaModelService model,
        [Description("Tekla view name, e.g. '3D'. Default: the active view.")] string? viewName = null,
        [Description("Target GUIDs, comma/semicolon/newline separated.")] string? guids = null,
        [Description("Targets = the current Tekla UI selection.")] bool useSelection = false,
        [Description("Zoom to the targets. Default true.")] bool zoom = true,
        [Description("Colour the targets red. Default true.")] bool highlight = true,
        [Description("With highlight: make everything else semi-transparent. Default true.")] bool ghostOthers = true,
        [Description("Rotate a 3D view first: iso | top | front | back | left | right | bottom | iso_sw | iso_ne | iso_nw | 'dx,dy,dz'.")]
        string? direction = null,
        [Description("Draw target numbers in the view (active view only; experimental). Default false.")] bool labels = false,
        [Description("Restore camera and colours after the capture. Default true.")] bool restore = true,
        [Description("Maximum picture width in px. Default 1280.")] int maxWidth = 1280,
        [Description("Maximum picture height in px. Default 1024.")] int maxHeight = 1024,
        [Description("jpeg (default) | png.")] string format = "jpeg")
    {
        var result = model.CaptureView(new ViewCaptureRequest
        {
            ViewName = viewName,
            Guids = ToolHelpers.ParseList(guids),
            UseSelection = useSelection,
            Zoom = zoom,
            Highlight = highlight,
            GhostOthers = ghostOthers,
            Direction = direction,
            Labels = labels,
            Restore = restore,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Format = format,
        });
        if (!result.Captured || result.Image == null)
            throw new McpException(result.Message ?? "The view could not be captured.");

        var image = result.Image;
        result.Image = null;
        return Answer(image, result);
    }

    private static IEnumerable<ContentBlock> Answer(RenderedImage image, object description) =>
        new List<ContentBlock>
        {
            ImageContentBlock.FromBytes(image.Bytes, image.MimeType),
            new TextContentBlock { Text = JsonSerializer.Serialize(description, description.GetType(), Json) },
        };

    private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
}
