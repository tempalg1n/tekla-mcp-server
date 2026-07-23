using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

internal static class DrawingToolHelpers
{
    public static DrawingQuery BuildDrawingQuery(
        string? keyIn = null,
        string? type = null,
        string? markContains = null,
        string? nameContains = null,
        string? titleContains = null,
        string? associatedModelGuid = null,
        string? upToDateStatusContains = null,
        bool? isIssued = null,
        bool? isLocked = null,
        bool? isReadyForIssue = null,
        bool selectedOnly = false) =>
        new DrawingQuery
        {
            KeyIn = ToolHelpers.ParseList(keyIn),
            Type = EmptyToNull(type),
            MarkContains = EmptyToNull(markContains),
            NameContains = EmptyToNull(nameContains),
            TitleContains = EmptyToNull(titleContains),
            AssociatedModelGuid = EmptyToNull(associatedModelGuid),
            UpToDateStatusContains = EmptyToNull(upToDateStatusContains),
            IsIssued = isIssued,
            IsLocked = isLocked,
            IsReadyForIssue = isReadyForIssue,
            SelectedOnly = selectedOnly,
        };

    public static DrawingObjectQuery BuildObjectQuery(
        string? objectIds = null,
        string? indices = null,
        string? types = null,
        int? viewIndex = null,
        int? viewId = null,
        int? viewId2 = null,
        string? modelGuid = null,
        string? textContains = null,
        bool useSelection = false,
        bool recursive = true,
        bool includeGeometry = true,
        bool includeUdas = false) =>
        new DrawingObjectQuery
        {
            ObjectIds = ParseIdentifiers(objectIds),
            IndexIn = ParseInts(indices),
            TypeIn = ToolHelpers.ParseList(types),
            ViewIndex = viewIndex,
            ViewId = viewId,
            ViewId2 = viewId2,
            ModelGuid = EmptyToNull(modelGuid),
            TextContains = EmptyToNull(textContains),
            UseSelection = useSelection,
            Recursive = recursive,
            IncludeGeometry = includeGeometry,
            IncludeUdas = includeUdas,
        };

    public static List<TeklaIdentifierInfo> ParseIdentifiers(string? raw)
    {
        var result = new List<TeklaIdentifierInfo>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var token in raw.Split(
                     new[] { ',', ';', '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Trim().Split(':');
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                continue;
            var id2 = 0;
            if (parts.Length > 1)
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id2);
            result.Add(new TeklaIdentifierInfo { Id = id, Id2 = id2, Key = id + ":" + id2 });
        }
        // Preserve a non-empty but invalid selector as an impossible identifier. Returning
        // an empty list here would mean "no filter" and could widen a write to 200 objects.
        if (result.Count == 0)
            result.Add(new TeklaIdentifierInfo { Id = 0, Id2 = 0, Key = "invalid" });
        return result;
    }

    public static List<int> ParseInts(string? raw) =>
        ToolHelpers.ParseList(raw)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? (int?)n : null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToList();

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
