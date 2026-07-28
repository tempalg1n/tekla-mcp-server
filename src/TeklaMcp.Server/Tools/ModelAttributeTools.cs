using System;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Tools for attribute-centric discovery and filtering. The intended session flow on an
/// unfamiliar model: <c>tekla_discover_udas</c> to learn which fields exist at all, then
/// <c>tekla_find_attributes_by_value</c> to pin a known value to a field, then the analytics
/// tools (<c>tekla_group_weight_by groupBy='uda:...'</c>) or filters (udaName/udaEquals).
/// </summary>
[McpServerToolType]
public static class ModelAttributeTools
{
    [McpServerTool(Name = "tekla_find_attributes_by_value")]
    [Description("Find which attribute names hold a known value (useful when you know 'BK1' or 'Approved' but not " +
                 "whether it lives in USER_FIELD_1, a report property, ...). Checks each scanned object against a " +
                 "candidate name list (common UDAs incl. USER_FIELD_1..4 + any names you pass). READ THE RESULT " +
                 "METADATA: empty matches with truncated=true means 'not found in the first N objects', NOT absent " +
                 "— raise objectLimit or filter the scope. Scans physical parts by default. If the value could live " +
                 "in an uncommon field, run tekla_discover_udas first to enumerate the fields that actually exist.")]
    public static AttributeSearchResult FindAttributesByValue(
        ITeklaModelService model,
        [Description("Known attribute value to search, e.g. 'BK1'.")] string value,
        [Description("Optional candidate attribute names separated by comma/semicolon/newline. " +
                     "Added on top of the built-in candidate list.")] string? candidateAttributes = null,
        [Description("Exact match when true, substring match when false. Default false.")] bool exactMatch = false,
        [Description("Maximum number of objects to inspect. Default 2000.")] int objectLimit = 2000,
        [Description("Maximum number of matching attribute names to return. Default 50.")] int resultLimit = 50,
        [Description("Scan physical parts only (default) — UDAs live on parts in most workflows. " +
                     "Set false to also scan bolts/welds/assemblies/etc.")] bool partsOnly = true,
        [Description("Scan only the current Tekla UI selection. Default false.")] bool useSelection = false)
        => model.FindAttributesByValue(value, ParseList(candidateAttributes), exactMatch, objectLimit, resultLimit, partsOnly, useSelection);

    [McpServerTool(Name = "tekla_discover_udas")]
    [Description("Discover which UDA fields actually exist on model objects: field names with fill counts, distinct " +
                 "value counts and most frequent values, from a bounded sample. Use this FIRST on an unfamiliar " +
                 "model — it replaces guessing candidate field names. SAMPLE-BASED: reading all UDAs of one object " +
                 "is expensive on live models (~0.1 s each), so keep sampleSize moderate (default 200; the sample " +
                 "is spread across part types). A field absent from the sample may still exist on unsampled " +
                 "objects — verify with tekla_count_objects (udaName + udaEquals) before concluding it is absent.")]
    public static UdaDiscoveryResult DiscoverUdas(
        ITeklaModelService model,
        [Description("Object type, exact match, e.g. 'Beam'. Empty = every part type.")] string? type = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("Profile substring, e.g. 'IPE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355'.")] string? material = null,
        [Description("Sample only the current Tekla UI selection. Default false.")] bool useSelection = false,
        [Description("Objects to sample (default 200; ~0.1 s per object on live models).")] int sampleSize = 200,
        [Description("Most frequent values to report per field. Default 5.")] int topValuesPerField = 5,
        [Description("Sample physical parts only (default). Set false to include bolts/welds/assemblies/etc.")] bool partsOnly = true)
        => model.DiscoverUdas(
            new ObjectQuery
            {
                Type = type,
                NameContains = nameContains,
                Profile = profile,
                Material = material,
                UseSelection = useSelection,
            },
            sampleSize, topValuesPerField, partsOnly);

    private static IReadOnlyList<string>? ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var result = new List<string>();
        var parts = raw!.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) result.Add(trimmed);
        }

        return result;
    }
}
