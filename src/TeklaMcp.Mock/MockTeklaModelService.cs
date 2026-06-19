using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Mock;

/// <summary>
/// Deterministic, in-memory fake of <see cref="ITeklaModelService"/>.
///
/// Purpose: let the MCP server run and be exercised end-to-end on machines without
/// Tekla (e.g. macOS during development). The data is synthetic but structurally
/// realistic — a small steel frame of columns, beams, braces, base plates and bolts —
/// so the read/analyze tools return believable results.
///
/// This implementation contains NO Tekla references and is safe everywhere.
/// </summary>
public sealed class MockTeklaModelService : ITeklaModelService
{
    private const string BackendName = "Mock";
    private readonly List<ModelObjectInfo> _objects = BuildSampleModel();

    public ConnectionInfo GetConnectionInfo() => new()
    {
        Connected = true,
        ModelName = "MockModel",
        ModelPath = "/virtual/mock/MockModel",
        TeklaVersion = "2026 (mock)",
        Backend = BackendName,
        Message = "Synthetic data — no Tekla involved.",
    };

    public ModelSummary GetModelSummary()
    {
        var s = new ModelSummary { Backend = BackendName, TotalObjects = _objects.Count };
        foreach (var o in _objects)
        {
            s.TotalWeightKg += o.WeightKg ?? 0;
            Bump(s.CountByType, o.Type);
            Bump(s.CountByClass, o.Class);
            Bump(s.CountByProfile, o.Profile);
            Bump(s.CountByMaterial, o.Material);
            if (o.WeightKg is double w)
                s.WeightByMaterialKg[o.Material] =
                    s.WeightByMaterialKg.TryGetValue(o.Material, out var cur) ? cur + w : w;
        }
        s.TotalWeightKg = Math.Round(s.TotalWeightKg, 1);
        return s;
    }

    public IReadOnlyList<ModelObjectInfo> GetAllObjects(int? limit = null) => Limit(_objects, limit);

    public IReadOnlyList<ModelObjectInfo> FindObjects(ObjectQuery query, int? limit = null)
    {
        IEnumerable<ModelObjectInfo> q = _objects;
        if (!string.IsNullOrWhiteSpace(query.Type)) q = q.Where(o => Eq(o.Type, query.Type));
        if (!string.IsNullOrWhiteSpace(query.Class)) q = q.Where(o => Eq(o.Class, query.Class));
        if (!string.IsNullOrWhiteSpace(query.Profile)) q = q.Where(o => Contains(o.Profile, query.Profile));
        if (!string.IsNullOrWhiteSpace(query.Material)) q = q.Where(o => Contains(o.Material, query.Material));
        if (!string.IsNullOrWhiteSpace(query.NameContains)) q = q.Where(o => Contains(o.Name, query.NameContains));
        return Limit(q.ToList(), limit);
    }

    public ModelObjectInfo? GetObjectByGuid(string guid) =>
        _objects.FirstOrDefault(o => string.Equals(o.Guid, guid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pretend the user has selected the first few objects in the UI.</summary>
    public IReadOnlyList<ModelObjectInfo> GetSelectedObjects() => _objects.Take(3).ToList();

    // ---------------------------------------------------------------- helpers ----

    private static void Bump(IDictionary<string, int> d, string key)
    {
        if (string.IsNullOrEmpty(key)) key = "(none)";
        d[key] = d.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    private static bool Eq(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string a, string? b) =>
        a.IndexOf(b ?? "", StringComparison.OrdinalIgnoreCase) >= 0;

    private static IReadOnlyList<ModelObjectInfo> Limit(List<ModelObjectInfo> src, int? limit) =>
        limit is int n && n > 0 && n < src.Count ? src.GetRange(0, n) : src;

    /// <summary>Build a small, deterministic synthetic steel frame (~42 objects).</summary>
    private static List<ModelObjectInfo> BuildSampleModel()
    {
        var list = new List<ModelObjectInfo>();
        var id = 1000;

        void Add(string type, string name, string cls, string profile, string material,
                 double lengthMm, double weightKg, string pos)
        {
            list.Add(new ModelObjectInfo
            {
                Guid = $"00000000-0000-0000-0000-{id:D12}",
                Id = id,
                Type = type,
                Name = name,
                Class = cls,
                Profile = profile,
                Material = material,
                LengthMm = lengthMm > 0 ? lengthMm : null,
                WeightKg = Math.Round(weightKg, 1),
                Finish = "PAINT",
                AssemblyPos = pos,
            });
            id++;
        }

        // Columns: HEA300 ~ 88.3 kg/m
        for (var i = 0; i < 4; i++)
            Add("Beam", "COLUMN", "2", "HEA300", "S355J2", 4000, 4.0 * 88.3, $"C{i + 1}");

        // Main beams: IPE400 ~ 66.3 kg/m
        for (var i = 0; i < 6; i++)
            Add("Beam", "BEAM", "3", "IPE400", "S355J2", 6000, 6.0 * 66.3, $"B{i + 1}");

        // Secondary beams: IPE200 ~ 22.4 kg/m
        for (var i = 0; i < 8; i++)
            Add("Beam", "BEAM", "4", "IPE200", "S235JR", 3000, 3.0 * 22.4, $"B{i + 10}");

        // Braces: L100*100*10 ~ 15.0 kg/m
        for (var i = 0; i < 4; i++)
            Add("Beam", "BRACE", "5", "L100*100*10", "S235JR", 4200, 4.2 * 15.0, $"BR{i + 1}");

        // Base plates: PL20*400, ~25 kg each
        for (var i = 0; i < 4; i++)
            Add("ContourPlate", "BASE PLATE", "7", "PL20*400", "S355J2", 400, 25.1, $"P{i + 1}");

        // Bolts: M20 8.8, ~0.25 kg each
        for (var i = 0; i < 16; i++)
            Add("Bolt", "M20", "0", "M20", "8.8", 0, 0.25, "BOLT");

        return list;
    }
}
