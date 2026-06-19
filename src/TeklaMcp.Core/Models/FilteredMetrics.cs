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
}
