using System;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Tools for reading and writing user-defined attributes (UDA).
/// </summary>
[McpServerToolType]
public static class ModelUdaTools
{
    [McpServerTool(Name = "tekla_get_object_udas")]
    [Description("Read selected UDA fields from one object by GUID. " +
                 "Pass a comma/semicolon/newline separated list, e.g. 'USER_FIELD_1;USER_PHASE'.")]
    public static ObjectUdaResult GetObjectUdas(
        ITeklaModelService model,
        [Description("Object GUID.")] string guid,
        [Description("UDA names separated by comma/semicolon/newline.")] string udaNames)
        => model.GetObjectUdas(guid, ParseList(udaNames));

    [McpServerTool(Name = "tekla_set_object_udas")]
    [Description("Set UDA fields on one object by GUID. " +
                 "Use key/value pairs like 'USER_FIELD_1=ABC;USER_PHASE=KMD'. " +
                 "By default this runs in preview mode (apply=false).")]
    public static UdaOperationResult SetObjectUdas(
        ITeklaModelService model,
        [Description("Object GUID.")] string guid,
        [Description("UDA updates: 'KEY=VALUE;KEY2=VALUE2' (semicolon/newline separated).")] string updates,
        [Description("Safety switch: set true to apply changes. Default false = preview only.")] bool apply = false)
        => model.SetObjectUdas(guid, ParseKeyValuePairs(updates), apply);

    [McpServerTool(Name = "tekla_set_udas_by_filter")]
    [Description("Set UDA fields on objects matching filters. " +
                 "Use key/value pairs like 'USER_FIELD_1=ABC;USER_PHASE=KMD'. " +
                 "By default this runs in preview mode (apply=false).")]
    public static UdaOperationResult SetUdasByFilter(
        ITeklaModelService model,
        [Description("UDA updates: 'KEY=VALUE;KEY2=VALUE2' (semicolon/newline separated).")] string updates,
        [Description("Object type, exact match, e.g. 'Beam'.")] string? type = null,
        [Description("Tekla class, exact match, e.g. '20'.")] string? @class = null,
        [Description("Profile substring, e.g. 'IPE' or 'TUBE'.")] string? profile = null,
        [Description("Material substring, e.g. 'S355' or 'C245'.")] string? material = null,
        [Description("Substring of object name.")] string? nameContains = null,
        [Description("Safety limit on number of matched objects. Default 200.")] int limit = 200,
        [Description("Safety switch: set true to apply changes. Default false = preview only.")] bool apply = false)
        => model.SetUdas(
            new ObjectQuery
            {
                Type = type,
                Class = @class,
                Profile = profile,
                Material = material,
                NameContains = nameContains,
            },
            ParseKeyValuePairs(updates),
            apply,
            limit);

    private static IReadOnlyList<string> ParseList(string raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var parts = raw.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) result.Add(trimmed);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseKeyValuePairs(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var pairs = raw.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;

            var key = pair.Substring(0, idx).Trim();
            if (key.Length == 0) continue;
            var value = pair.Substring(idx + 1).Trim();
            result[key] = value;
        }

        return result;
    }
}
