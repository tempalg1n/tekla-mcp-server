using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>Aggregated statistics over all objects in the model.</summary>
public sealed class ModelSummary
{
    /// <summary>Total number of objects counted.</summary>
    public int TotalObjects { get; set; }

    /// <summary>Sum of all object weights, in kilograms.</summary>
    public double TotalWeightKg { get; set; }

    /// <summary>Object count grouped by type ("Beam", "Bolt", ...).</summary>
    public Dictionary<string, int> CountByType { get; set; } = new();

    /// <summary>Object count grouped by Tekla class.</summary>
    public Dictionary<string, int> CountByClass { get; set; } = new();

    /// <summary>Object count grouped by profile.</summary>
    public Dictionary<string, int> CountByProfile { get; set; } = new();

    /// <summary>Object count grouped by material grade.</summary>
    public Dictionary<string, int> CountByMaterial { get; set; } = new();

    /// <summary>Total weight (kg) grouped by material grade.</summary>
    public Dictionary<string, double> WeightByMaterialKg { get; set; } = new();

    /// <summary>True when the scan stopped early (maxObjects cap) — all counts are partial.</summary>
    public bool Truncated { get; set; }

    /// <summary>Notes about how the summary was computed (partial scan, skipped weights).</summary>
    public string Message { get; set; } = "";

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";
}
