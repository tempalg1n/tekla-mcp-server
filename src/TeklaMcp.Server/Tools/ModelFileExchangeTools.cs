using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Bulk exchange with files on disk, for work whose volume cannot pass through an LLM context:
/// exporting tens of thousands of parts WITH geometry, and writing back tens of thousands of
/// GUID→UDA pairs. The data never appears in the tool response — only counters and a path.
///
/// Paths are restricted to an allow-list of roots (TEKLA_MCP_FILE_ROOT, defaulting to
/// %LOCALAPPDATA%\TeklaMcp\exchange plus the open model's folder) with a data-file extension
/// whitelist — see TeklaMcp.Core.FileExchange.FilePathPolicy.
/// </summary>
[McpServerToolType]
public static class ModelFileExchangeTools
{
    [McpServerTool(Name = "tekla_export_parts_file")]
    [Description(
        "Export model objects TO A FILE ON DISK — the response carries counters only, never the " +
        "rows. Use this instead of tekla_export_objects / tekla_find_objects whenever the result " +
        "would be more than a few hundred objects or needs geometry.\n" +
        "fields (comma-separated, default 'guid,id,type,name,class,profile,material,assemblyPos," +
        "lengthMm,weightKg'): add 'solidAabb' for the world bounding box of Part.GetSolid(), " +
        "'startPoint'/'endPoint', 'contourPoints' (ContourPlate/PolyBeam), 'coordSystem' " +
        "(origin + X/Y/Z axes), 'cog', 'finish', and 'uda:NAME' for any user-defined attribute. " +
        "Ask only for what you need: solidAabb costs a GetSolid() per object (~1.5 ms live).\n" +
        "PAGING: a whole-model export with geometry takes longer than the ~60 s an MCP client " +
        "waits, and a lost response loses the whole call. Pass maxObjects (~30000 is a safe page " +
        "with solidAabb) and, while truncated=true, repeat with cursor=nextCursor AND append=true " +
        "and every other argument unchanged. The server also mirrors the counters into " +
        "'<path>.status.json' so a timed-out call can still be verified.\n" +
        "Set udaIsEmpty=true (with udaName) to select objects whose UDA is still blank.")]
    public static ExportResult ExportPartsFile(
        ITeklaModelService model,
        [Description("Absolute path of the output file, e.g. 'C:\\Users\\me\\AppData\\Local\\TeklaMcp\\exchange\\parts.jsonl'.")]
        string path,
        [Description("Output format: 'jsonl' (default) or 'csv'.")] string format = "jsonl",
        [Description("Comma-separated field list. Empty = the default identity + bill-of-materials set.")]
        string? fields = null,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("UDA field name for exact match, e.g. 'USER_FIELD_1'.")] string? udaName = null,
        [Description("Exact UDA value to match (case-insensitive).")] string? udaEquals = null,
        [Description("With udaName: match objects whose UDA is UNSET or blank. Overrides udaEquals.")]
        bool udaIsEmpty = false,
        [Description("Generic attribute/report/UDA name, e.g. 'ASSEMBLY_POS'.")] string? attributeName = null,
        [Description("Exact value for generic attribute match (case-insensitive).")] string? attributeEquals = null,
        [Description("Substring value for generic attribute match (case-insensitive).")] string? attributeContains = null,
        [Description("Scope to current Tekla UI selection instead of the whole model. Default false.")]
        bool useSelection = false,
        [Description("Scan physical parts only (default). Set false to include bolts/welds/assemblies/etc.")]
        bool partsOnly = true,
        [Description("Cap the number of source objects scanned in this call. Use it — see PAGING above.")]
        int? maxObjects = null,
        [Description("Wall-clock budget for this call in seconds; the scan stops between objects and returns a " +
                     "cursor. Belt-and-braces for maxObjects when the per-object cost is unknown.")]
        double? maxSeconds = null,
        [Description("Continuation cursor from the previous page's nextCursor. Requires append=true.")]
        string? cursor = null,
        [Description("Append to an existing file instead of replacing it. Required for every page after the first.")]
        bool append = false)
    {
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
            UseSelection = useSelection,
        };

        return model.ExportObjectsToFile(query, new ExportRequest
        {
            Path = path,
            Format = format,
            Fields = ToolHelpers.ParseList(fields),
            MaxObjects = maxObjects,
            MaxSeconds = maxSeconds,
            Cursor = cursor,
            Append = append,
            PartsOnly = partsOnly,
        });
    }

    [McpServerTool(Name = "tekla_set_udas_from_file")]
    [Description(
        "Write UDA values to many objects at once from a jsonl/csv file keyed by Tekla GUID — " +
        "the way to apply a result computed outside the model (e.g. a geometric reconciliation) " +
        "without listing tens of thousands of GUIDs in a tool call.\n" +
        "File format: one record per object, e.g. " +
        "{\"guid\":\"c7e3b4dc-...\",\"USER_FIELD_1\":\"Согласованно\",\"USER_FIELD_2\":\"KXM_AI\"}. " +
        "Every column except guidColumn is written as a UDA unless udaColumns narrows it.\n" +
        "SAFETY: apply=false by default — run it once to read the counters, show them to the user, " +
        "and only then re-run with apply=true. overwriteNonEmpty=false (default) leaves any UDA " +
        "that already holds a value untouched, so decisions a human made are never overwritten; " +
        "the skippedNonEmpty counter tells you how many were left alone.\n" +
        "PAGING: on large files pass maxObjects and, while truncated=true, repeat with " +
        "cursor=nextCursor and the same arguments.")]
    public static UdaFileWriteResult SetUdasFromFile(
        ITeklaModelService model,
        [Description("Absolute path of the input file (jsonl or csv).")] string path,
        [Description("Safety switch: set true to write. Default false = preview only, nothing is changed.")]
        bool apply = false,
        [Description("File format: 'jsonl' or 'csv'. Empty = inferred from the extension.")]
        string? format = null,
        [Description("Name of the column/property holding the Tekla GUID. Default 'guid'.")]
        string guidColumn = "guid",
        [Description("Comma-separated columns to write as UDAs. Empty = every column except the GUID column.")]
        string? udaColumns = null,
        [Description("What to do with a GUID that is not in the model: 'skip' (default) or 'fail'.")]
        string onMissing = "skip",
        [Description("Overwrite UDAs that already hold a value. Default false — a human's decision wins.")]
        bool overwriteNonEmpty = false,
        [Description("Cap the number of file rows processed in this call.")] int? maxObjects = null,
        [Description("Continuation cursor from the previous page's nextCursor.")] string? cursor = null)
        => model.SetUdasFromFile(new UdaFileWriteRequest
        {
            Path = path,
            Format = format,
            GuidColumn = guidColumn,
            UdaColumns = ToolHelpers.ParseList(udaColumns),
            Apply = apply,
            OverwriteNonEmpty = overwriteNonEmpty,
            OnMissing = onMissing,
            MaxObjects = maxObjects,
            Cursor = cursor,
        });

    [McpServerTool(Name = "tekla_export_reference_objects_file")]
    [Description(
        "Export reference-model (IFC) objects TO A FILE ON DISK: external IFC GUID, entity, name, " +
        "description, objectType, reference model title, world AABB and placement (origin + X/Y/Z " +
        "axes, global mm). The file-based counterpart of tekla_get_reference_geometry, which only " +
        "returns a handful of objects per call. The response carries counters only.\n" +
        "entityFilter (e.g. 'IFCBEAM,IFCCOLUMN,IFCMEMBER,IFCPLATE') keeps bolts and welds out of " +
        "the file. referenceModelId/referenceModelGuid restrict the scan to one reference model.\n" +
        "GEOMETRY HONESTY: aabbSource says where each box came from — 'ifc-placement-estimate' " +
        "(position and in-plane extent parsed from the IFC file, zero thickness), 'tekla-faces' " +
        "(exact) or 'tekla-faces-truncated' (face budget hit, the box is too SMALL — never treat " +
        "it as exact). placementSource='ifc-file' means the placement was parsed off disk.\n" +
        "DANGER — includeFaceAabb: exact AABBs come from an internal, version-sensitive Tekla " +
        "call, and it is OFF by default for a reason. On Tekla 2021 / model 3155 (2026-08-13) " +
        "running it across an IFC overlay pegged a core INSIDE Tekla and left the UI " +
        "unresponsive; killing the MCP client did not release it, because the work was already " +
        "handed to Tekla. maxObjects and maxSeconds cannot bound a call that never returns. " +
        "Before enabling it on a reference model you have not tried it on, run ONE probe with " +
        "maxObjects=5 and tell the user what it cost.\n" +
        "PAGING: same contract as tekla_export_parts_file — maxObjects (plus maxSeconds), then " +
        "cursor=nextCursor with append=true while truncated=true.")]
    public static ExportResult ExportReferenceObjectsFile(
        ITeklaModelService model,
        [Description("Absolute path of the output file.")] string path,
        [Description("Output format: 'jsonl' (default) or 'csv'.")] string format = "jsonl",
        [Description("Restrict to one reference model by its Tekla integer id.")] int? referenceModelId = null,
        [Description("Restrict to one reference model by its Tekla GUID.")] string? referenceModelGuid = null,
        [Description("Comma-separated IFC entity types to keep, e.g. 'IFCBEAM,IFCCOLUMN'. Empty = all.")]
        string? entityFilter = null,
        [Description("Read face polygons for an EXACT AABB. Default false — see the DANGER note above. " +
                     "Probe with maxObjects=5 before enabling it on an untried reference model.")]
        bool includeFaceAabb = false,
        [Description("Face budget per object when includeFaceAabb is on. Default 2000.")]
        int maxFacesPerObject = 2000,
        [Description("Cap the number of source objects scanned in this call.")] int? maxObjects = null,
        [Description("Wall-clock budget for this call in seconds; the scan stops between objects and returns a " +
                     "cursor. Cannot interrupt a single Tekla call that never returns.")]
        double? maxSeconds = null,
        [Description("Continuation cursor from the previous page's nextCursor. Requires append=true.")]
        string? cursor = null,
        [Description("Append to an existing file instead of replacing it. Required for every page after the first.")]
        bool append = false)
        => model.ExportReferenceObjectsToFile(new ReferenceExportRequest
        {
            Path = path,
            Format = format,
            ReferenceModelId = referenceModelId,
            ReferenceModelGuid = referenceModelGuid,
            EntityFilter = ToolHelpers.ParseList(entityFilter),
            IncludeFaceAabb = includeFaceAabb,
            MaxFacesPerObject = maxFacesPerObject,
            MaxObjects = maxObjects,
            MaxSeconds = maxSeconds,
            Cursor = cursor,
            Append = append,
        });
}
