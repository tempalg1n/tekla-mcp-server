namespace TeklaMcp.Core.Models;

/// <summary>
/// Count + weight aggregate for objects matching a filter.
/// </summary>
public sealed class FilteredMetrics
{
    /// <summary>Total matched objects.</summary>
    public int ObjectCount { get; set; }

    /// <summary>Matched objects that have non-null weight.</summary>
    public int ObjectsWithWeight { get; set; }

    /// <summary>Total weight in kilograms.</summary>
    public double TotalWeightKg { get; set; }

    /// <summary>Source objects consumed by this call (matched or not).</summary>
    public int ScannedObjects { get; set; }

    /// <summary>True when the scan stopped at maxObjects — the totals are partial.</summary>
    public bool Truncated { get; set; }

    /// <summary>Continuation cursor when <see cref="Truncated"/>; add page totals together.</summary>
    public string? NextCursor { get; set; }

    /// <summary>Scan scope notes, truncation hints, or an error explanation.</summary>
    public string Message { get; set; } = "";

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";
}
