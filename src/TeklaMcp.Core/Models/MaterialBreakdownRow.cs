namespace TeklaMcp.Core.Models;

/// <summary>One row of a bill-of-material style breakdown grouped by material grade.</summary>
public sealed class MaterialBreakdownRow
{
    /// <summary>Material grade, e.g. "S355J2".</summary>
    public string Material { get; set; } = "";

    /// <summary>Number of objects of this material.</summary>
    public int Count { get; set; }

    /// <summary>Total weight of those objects, in kilograms.</summary>
    public double TotalWeightKg { get; set; }
}
