using System;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Tools for attribute-centric discovery and filtering.
/// </summary>
[McpServerToolType]
public static class ModelAttributeTools
{
    [McpServerTool(Name = "tekla_find_attributes_by_value")]
    [Description("Find likely attribute names by a known value (useful when you know 'BK1' but " +
                 "do not know whether it is stored as a UDA or report property).")]
    public static IReadOnlyList<AttributeValueMatch> FindAttributesByValue(
        ITeklaModelService model,
        [Description("Known attribute value to search, e.g. 'BK1'.")] string value,
        [Description("Optional candidate attribute names separated by comma/semicolon/newline. " +
                     "If empty, server uses a broad built-in candidate list.")] string? candidateAttributes = null,
        [Description("Exact match when true, substring match when false. Default false.")] bool exactMatch = false,
        [Description("Maximum number of objects to inspect. Default 2000.")] int objectLimit = 2000,
        [Description("Maximum number of matching attribute names to return. Default 50.")] int resultLimit = 50)
        => model.FindAttributesByValue(value, ParseList(candidateAttributes), exactMatch, objectLimit, resultLimit);

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
