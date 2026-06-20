using System;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Generic property reader. Lets an agent pull ANY named property from an object without a
/// dedicated tool per field — report properties (VOLUME, AREA, HEIGHT, PROFILE_TYPE, ...),
/// UDAs, or built-ins (GUID, ID, NAME, CLASS, PROFILE, MATERIAL, WEIGHT, LENGTH, ASSEMBLY_POS).
/// </summary>
[McpServerToolType]
public static class ModelPropertyTools
{
    [McpServerTool(Name = "tekla_get_properties")]
    [Description("Read arbitrary properties for one object by GUID. Names can be report properties " +
                 "(e.g. VOLUME, AREA, HEIGHT, WIDTH, PROFILE_TYPE), UDAs, or built-ins (GUID, ID, " +
                 "NAME, CLASS, PROFILE, MATERIAL, WEIGHT, LENGTH, ASSEMBLY_POS). Pass names " +
                 "comma/semicolon/newline separated, e.g. 'VOLUME;AREA;PHASE'. Unknown names are skipped.")]
    public static ObjectUdaResult GetProperties(
        ITeklaModelService model,
        [Description("Object GUID.")] string guid,
        [Description("Property names separated by comma/semicolon/newline.")] string names)
        => model.GetProperties(guid, ParseList(names));

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
}
