using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// One page of a streaming count + weight aggregation (<c>ITeklaModelService.AggregateBy</c>).
///
/// Aggregation never materializes DTOs and never touches solids — it reads exactly one group
/// key and the WEIGHT report property per matched object, so it is the sanctioned path for
/// whole-model analytics on 400k+ models (see "Performance rules" in AGENTS.md).
///
/// Paging: large scans can exceed the MCP client's request deadline, so callers may cap one
/// call with <c>maxObjects</c>; a capped call sets <see cref="Truncated"/> and returns
/// <see cref="NextCursor"/>. Pass that cursor to the next call to resume the scan where it
/// stopped, then merge rows by <see cref="GroupedMetricRow.Key"/> client-side — every page
/// covers a disjoint slice of the source, so counts and weights add up.
/// </summary>
public sealed class AggregationResult
{
    /// <summary>Normalized group key this aggregation used, e.g. "material" or "uda:USER_FIELD_1".</summary>
    public string GroupBy { get; set; } = "";

    /// <summary>
    /// Per-group rows for THIS page, ordered by weight then count descending. When more groups
    /// exist than the row limit, the remainder is rolled into a single "(other)" row so page
    /// totals stay consistent.
    /// </summary>
    public List<GroupedMetricRow> Rows { get; set; } = new List<GroupedMetricRow>();

    /// <summary>Source objects consumed in this call (after the cursor skip, matched or not).</summary>
    public int ScannedObjects { get; set; }

    /// <summary>Objects that passed the query filters and were aggregated into <see cref="Rows"/>.</summary>
    public int MatchedObjects { get; set; }

    /// <summary>Matched objects that reported a WEIGHT value.</summary>
    public int ObjectsWithWeight { get; set; }

    /// <summary>Total weight (kg) across all matched objects in this page.</summary>
    public double TotalWeightKg { get; set; }

    /// <summary>True when the scan stopped at <c>maxObjects</c> — this page is partial.</summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// Opaque continuation cursor. Non-null only when <see cref="Truncated"/>; pass it back
    /// unchanged (with the SAME query/groupBy/scope) to aggregate the next slice.
    /// </summary>
    public string? NextCursor { get; set; }

    /// <summary>Diagnostics: scan scope, truncation hints, or an error explanation.</summary>
    public string Message { get; set; } = "";

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";
}
