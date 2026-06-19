namespace TeklaMcp.Core.Models;

/// <summary>
/// Aggregate row grouped by a single key (type/class/profile/material/name).
/// </summary>
public sealed class GroupedMetricRow
{
    /// <summary>Group value; "(none)" when source value is empty.</summary>
    public string Key { get; set; } = "";

    /// <summary>Number of objects in this group.</summary>
    public int Count { get; set; }

    /// <summary>Total weight in kilograms for this group.</summary>
    public double TotalWeightKg { get; set; }
}
