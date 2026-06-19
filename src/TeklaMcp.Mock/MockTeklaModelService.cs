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
    private static readonly string[] DefaultAttributeCandidates =
    {
        "MCP_TAG",
        "MCP_TYPE",
        "RU_FN1_MRK",
        "ASSEMBLY_POS",
        "PROFILE",
        "MATERIAL",
        "NAME",
        "CLASS",
    };

    private readonly List<ModelObjectInfo> _objects = BuildSampleModel();
    private readonly Dictionary<string, Dictionary<string, string>> _udasByGuid =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    private List<ModelObjectInfo> _selectedObjects;

    public MockTeklaModelService()
    {
        _selectedObjects = _objects.Take(3).ToList();
        SeedUdas();
    }

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
        if (!string.IsNullOrWhiteSpace(query.UdaName) && !string.IsNullOrWhiteSpace(query.UdaEquals))
        {
            q = q.Where(o =>
            {
                if (!_udasByGuid.TryGetValue(o.Guid, out var udas)) return false;
                return udas.TryGetValue(query.UdaName!, out var value) &&
                       string.Equals(value, query.UdaEquals, StringComparison.OrdinalIgnoreCase);
            });
        }
        if (!string.IsNullOrWhiteSpace(query.AttributeName))
        {
            q = q.Where(o =>
            {
                if (!TryGetAttributeValue(o, query.AttributeName!, out var value)) return false;
                if (!string.IsNullOrWhiteSpace(query.AttributeEquals) &&
                    !Eq(value, query.AttributeEquals)) return false;
                if (!string.IsNullOrWhiteSpace(query.AttributeContains) &&
                    !Contains(value, query.AttributeContains)) return false;
                return true;
            });
        }
        return Limit(q.ToList(), limit);
    }

    public ModelObjectInfo? GetObjectByGuid(string guid) =>
        _objects.FirstOrDefault(o => string.Equals(o.Guid, guid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pretend the user has selected objects in the UI.</summary>
    public IReadOnlyList<ModelObjectInfo> GetSelectedObjects() => _selectedObjects;

    public SelectionResult SelectObjects(ObjectQuery query, int? limit = null)
    {
        _selectedObjects = FindObjects(query, limit).ToList();
        return new SelectionResult
        {
            SelectedCount = _selectedObjects.Count,
            Preview = _selectedObjects.Take(20).ToList(),
            Backend = BackendName,
        };
    }

    public IReadOnlyList<AttributeValueMatch> FindAttributesByValue(
        string value,
        IReadOnlyList<string>? candidateAttributeNames = null,
        bool exactMatch = false,
        int? objectLimit = 2000,
        int? resultLimit = 50)
    {
        var candidates = BuildAttributeCandidateList(candidateAttributeNames);
        var objects = Limit(_objects, objectLimit);
        var matches = new Dictionary<string, AttributeValueMatch>(StringComparer.OrdinalIgnoreCase);

        foreach (var obj in objects)
        {
            foreach (var attributeName in candidates)
            {
                if (!TryGetAttributeValue(obj, attributeName, out var attrValue)) continue;
                if (!ValueMatches(attrValue, value, exactMatch)) continue;

                if (!matches.TryGetValue(attributeName, out var row))
                {
                    row = new AttributeValueMatch { AttributeName = attributeName };
                    matches[attributeName] = row;
                }

                row.MatchCount++;
                if (!row.MatchedValues.Any(v => Eq(v, attrValue)) && row.MatchedValues.Count < 5)
                    row.MatchedValues.Add(attrValue);
                if (row.SampleGuids.Count < 5)
                    row.SampleGuids.Add(obj.Guid);
            }
        }

        var ordered = matches.Values
            .OrderByDescending(x => x.MatchCount)
            .ThenBy(x => x.AttributeName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (resultLimit is int n && n > 0 && ordered.Count > n)
            ordered = ordered.Take(n).ToList();

        return ordered;
    }

    public ProfileConnectionSummary AnalyzeConnectionsForProfile(string profile, double toleranceMm = 50, int? limit = 1000)
    {
        var source = FindObjects(new ObjectQuery { Profile = profile, Type = "Beam" }, limit)
            .Where(o => o.StartX.HasValue && o.StartY.HasValue && o.StartZ.HasValue &&
                        o.EndX.HasValue && o.EndY.HasValue && o.EndZ.HasValue)
            .ToList();
        var all = _objects;
        var signatures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var beam in source)
        {
            AddConnectionSignature(beam, all, toleranceMm, true, signatures);
            AddConnectionSignature(beam, all, toleranceMm, false, signatures);
        }

        var rows = signatures
            .Select(kv => new ProfileConnectionType { Signature = kv.Key, Occurrences = kv.Value })
            .OrderByDescending(r => r.Occurrences)
            .ThenBy(r => r.Signature, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProfileConnectionSummary
        {
            SourceProfile = profile ?? "",
            SourceObjects = source.Count,
            UniqueConnectionTypes = rows.Count,
            ToleranceMm = toleranceMm,
            ConnectionTypes = rows,
        };
    }

    public ObjectUdaResult GetObjectUdas(string guid, IReadOnlyList<string> udaNames)
    {
        var obj = GetObjectByGuid(guid);
        if (obj is null)
            return new ObjectUdaResult
            {
                Guid = guid,
                Backend = BackendName,
                Message = "Object not found.",
            };

        _udasByGuid.TryGetValue(obj.Guid, out var objectUdas);
        objectUdas ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new ObjectUdaResult
        {
            Guid = obj.Guid,
            Id = obj.Id,
            Type = obj.Type,
            Backend = BackendName,
        };

        foreach (var name in udaNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (objectUdas.TryGetValue(name, out var value))
                result.Udas[name] = value;
        }

        return result;
    }

    public UdaOperationResult SetObjectUdas(string guid, IReadOnlyDictionary<string, string> updates, bool apply)
    {
        var obj = GetObjectByGuid(guid);
        if (obj is null)
            return new UdaOperationResult
            {
                Applied = apply,
                Backend = BackendName,
                Message = "Object not found.",
            };

        var result = new UdaOperationResult
        {
            Applied = apply,
            MatchedObjects = 1,
            Backend = BackendName,
            Preview = new List<ModelObjectInfo> { obj },
        };

        if (!apply)
            return result;

        if (!_udasByGuid.TryGetValue(obj.Guid, out var objectUdas))
        {
            objectUdas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _udasByGuid[obj.Guid] = objectUdas;
        }

        foreach (var kv in updates)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            objectUdas[kv.Key] = kv.Value ?? "";
            result.UpdatedFields++;
        }

        result.UpdatedObjects = result.UpdatedFields > 0 ? 1 : 0;
        return result;
    }

    public UdaOperationResult SetUdas(
        ObjectQuery query,
        IReadOnlyDictionary<string, string> updates,
        bool apply,
        int? limit = null)
    {
        var matched = FindObjects(query, limit).ToList();
        var result = new UdaOperationResult
        {
            Applied = apply,
            MatchedObjects = matched.Count,
            Backend = BackendName,
            Preview = matched.Take(20).ToList(),
        };

        if (!apply) return result;

        foreach (var obj in matched)
        {
            if (!_udasByGuid.TryGetValue(obj.Guid, out var objectUdas))
            {
                objectUdas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _udasByGuid[obj.Guid] = objectUdas;
            }

            var changed = 0;
            foreach (var kv in updates)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                objectUdas[kv.Key] = kv.Value ?? "";
                changed++;
            }

            if (changed <= 0) continue;
            result.UpdatedObjects++;
            result.UpdatedFields += changed;
        }

        return result;
    }

    // ---------------------------------------------------------------- helpers ----

    private static void Bump(IDictionary<string, int> d, string key)
    {
        if (string.IsNullOrEmpty(key)) key = "(none)";
        d[key] = d.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    private static bool Eq(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string a, string? b) =>
        a.IndexOf(b ?? "", StringComparison.OrdinalIgnoreCase) >= 0;

    private bool TryGetAttributeValue(ModelObjectInfo obj, string attributeName, out string value)
    {
        value = "";
        if (obj == null || string.IsNullOrWhiteSpace(attributeName)) return false;

        switch (attributeName.Trim().ToUpperInvariant())
        {
            case "GUID": value = obj.Guid; return true;
            case "ID": value = obj.Id.ToString(); return true;
            case "TYPE": value = obj.Type; return true;
            case "NAME": value = obj.Name; return true;
            case "CLASS": value = obj.Class; return true;
            case "PROFILE": value = obj.Profile; return true;
            case "MATERIAL": value = obj.Material; return true;
            case "ASSEMBLY_POS": value = obj.AssemblyPos ?? ""; return !string.IsNullOrEmpty(value);
            case "FINISH": value = obj.Finish ?? ""; return !string.IsNullOrEmpty(value);
            default:
                if (!_udasByGuid.TryGetValue(obj.Guid, out var udas)) return false;
                if (!udas.TryGetValue(attributeName, out value)) return false;
                value = value ?? "";
                return true;
        }
    }

    private IReadOnlyList<string> BuildAttributeCandidateList(IReadOnlyList<string>? requested)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var builtIn in DefaultAttributeCandidates)
            set.Add(builtIn);

        if (requested != null)
        {
            foreach (var candidate in requested)
                if (!string.IsNullOrWhiteSpace(candidate))
                    set.Add(candidate.Trim());
        }

        foreach (var udas in _udasByGuid.Values)
            foreach (var udaName in udas.Keys)
                set.Add(udaName);

        return set.ToList();
    }

    private static bool ValueMatches(string candidate, string value, bool exactMatch)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return exactMatch ? Eq(candidate, value) : Contains(candidate, value);
    }

    private static void AddConnectionSignature(
        ModelObjectInfo beam,
        IReadOnlyList<ModelObjectInfo> all,
        double toleranceMm,
        bool startPoint,
        IDictionary<string, int> signatures)
    {
        var x = startPoint ? beam.StartX : beam.EndX;
        var y = startPoint ? beam.StartY : beam.EndY;
        var z = startPoint ? beam.StartZ : beam.EndZ;
        if (!x.HasValue || !y.HasValue || !z.HasValue) return;

        var neighbors = new List<string>();
        foreach (var obj in all)
        {
            if (string.Equals(obj.Guid, beam.Guid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsNearPoint(obj, x.Value, y.Value, z.Value, toleranceMm)) continue;
            neighbors.Add(BuildNeighborLabel(obj));
        }

        if (neighbors.Count == 0) return;

        neighbors.Sort(StringComparer.OrdinalIgnoreCase);
        var signature = string.Join(" + ", neighbors.Distinct(StringComparer.OrdinalIgnoreCase));
        signatures[signature] = signatures.TryGetValue(signature, out var count) ? count + 1 : 1;
    }

    private static bool IsNearPoint(ModelObjectInfo obj, double x, double y, double z, double toleranceMm)
    {
        var expandedMinX = (obj.MinX ?? obj.CenterX ?? x) - toleranceMm;
        var expandedMinY = (obj.MinY ?? obj.CenterY ?? y) - toleranceMm;
        var expandedMinZ = (obj.MinZ ?? obj.CenterZ ?? z) - toleranceMm;
        var expandedMaxX = (obj.MaxX ?? obj.CenterX ?? x) + toleranceMm;
        var expandedMaxY = (obj.MaxY ?? obj.CenterY ?? y) + toleranceMm;
        var expandedMaxZ = (obj.MaxZ ?? obj.CenterZ ?? z) + toleranceMm;

        return x >= expandedMinX && x <= expandedMaxX &&
               y >= expandedMinY && y <= expandedMaxY &&
               z >= expandedMinZ && z <= expandedMaxZ;
    }

    private static string BuildNeighborLabel(ModelObjectInfo obj)
    {
        var profile = string.IsNullOrWhiteSpace(obj.Profile) ? "(none)" : obj.Profile;
        return (obj.Type ?? "") + ":" + profile;
    }

    private static IReadOnlyList<ModelObjectInfo> Limit(List<ModelObjectInfo> src, int? limit) =>
        limit is int n && n > 0 && n < src.Count ? src.GetRange(0, n) : src;

    private void SeedUdas()
    {
        foreach (var obj in _objects)
        {
            var baseMark = obj.Name == "COLUMN" ? "BK1" : (obj.Name == "BEAM" ? "BK2" : "");
            _udasByGuid[obj.Guid] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MCP_TAG"] = "mock",
                ["MCP_TYPE"] = obj.Type,
                ["RU_FN1_MRK"] = baseMark,
                ["RU_OBJ_TYPE"] = obj.Name,
            };
        }
    }

    /// <summary>Build a small, deterministic synthetic steel frame (~42 objects).</summary>
    private static List<ModelObjectInfo> BuildSampleModel()
    {
        var list = new List<ModelObjectInfo>();
        var id = 1000;

        void Add(string type, string name, string cls, string profile, string material,
                 double lengthMm, double weightKg, string pos,
                 double centerX, double centerY, double centerZ,
                 double? startX = null, double? startY = null, double? startZ = null,
                 double? endX = null, double? endY = null, double? endZ = null)
        {
            var minX = centerX - 100;
            var minY = centerY - 100;
            var minZ = centerZ - 100;
            var maxX = centerX + 100;
            var maxY = centerY + 100;
            var maxZ = centerZ + 100;
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
                CenterX = centerX,
                CenterY = centerY,
                CenterZ = centerZ,
                StartX = startX,
                StartY = startY,
                StartZ = startZ,
                EndX = endX,
                EndY = endY,
                EndZ = endZ,
                MinX = minX,
                MinY = minY,
                MinZ = minZ,
                MaxX = maxX,
                MaxY = maxY,
                MaxZ = maxZ,
            });
            id++;
        }

        // Columns: HEA300 ~ 88.3 kg/m
        var columnPoints = new[]
        {
            new { X = 0.0, Y = 0.0 },
            new { X = 6000.0, Y = 0.0 },
            new { X = 0.0, Y = 6000.0 },
            new { X = 6000.0, Y = 6000.0 },
        };
        for (var i = 0; i < columnPoints.Length; i++)
        {
            var p = columnPoints[i];
            Add("Beam", "COLUMN", "2", "HEA300", "S355J2", 4000, 4.0 * 88.3, $"C{i + 1}",
                p.X, p.Y, 2000,
                p.X, p.Y, 0,
                p.X, p.Y, 4000);
        }

        // Main beams: IPE400 ~ 66.3 kg/m (at elevation 4000)
        Add("Beam", "BEAM", "3", "IPE400", "S355J2", 6000, 6.0 * 66.3, "B1", 3000, 0, 4000, 0, 0, 4000, 6000, 0, 4000);
        Add("Beam", "BEAM", "3", "IPE400", "S355J2", 6000, 6.0 * 66.3, "B2", 3000, 6000, 4000, 0, 6000, 4000, 6000, 6000, 4000);
        Add("Beam", "BEAM", "3", "IPE400", "S355J2", 6000, 6.0 * 66.3, "B3", 0, 3000, 4000, 0, 0, 4000, 0, 6000, 4000);
        Add("Beam", "BEAM", "3", "IPE400", "S355J2", 6000, 6.0 * 66.3, "B4", 6000, 3000, 4000, 6000, 0, 4000, 6000, 6000, 4000);
        Add("Beam", "BEAM", "3", "IPE400", "S355J2", 6000, 6.0 * 66.3, "B5", 3000, 3000, 4000, 0, 0, 4000, 6000, 6000, 4000);
        Add("Beam", "BEAM", "3", "IPE400", "S355J2", 6000, 6.0 * 66.3, "B6", 3000, 3000, 4000, 6000, 0, 4000, 0, 6000, 4000);

        // Secondary beams: IPE200 ~ 22.4 kg/m
        for (var i = 0; i < 8; i++)
        {
            var x = (i % 4) * 2000.0;
            var y = (i / 4) * 6000.0 + 1500.0;
            Add("Beam", "BEAM", "4", "IPE200", "S235JR", 3000, 3.0 * 22.4, $"B{i + 10}",
                x + 1500, y, 3000,
                x, y, 3000,
                x + 3000, y, 3000);
        }

        // Braces: L100*100*10 ~ 15.0 kg/m
        Add("Beam", "BRACE", "5", "L100*100*10", "S235JR", 4200, 4.2 * 15.0, "BR1", 3000, 0, 2000, 0, 0, 0, 6000, 0, 4000);
        Add("Beam", "BRACE", "5", "L100*100*10", "S235JR", 4200, 4.2 * 15.0, "BR2", 3000, 6000, 2000, 0, 6000, 0, 6000, 6000, 4000);
        Add("Beam", "BRACE", "5", "L100*100*10", "S235JR", 4200, 4.2 * 15.0, "BR3", 0, 3000, 2000, 0, 0, 0, 0, 6000, 4000);
        Add("Beam", "BRACE", "5", "L100*100*10", "S235JR", 4200, 4.2 * 15.0, "BR4", 6000, 3000, 2000, 6000, 0, 0, 6000, 6000, 4000);

        // Base plates: PL20*400, ~25 kg each
        for (var i = 0; i < columnPoints.Length; i++)
        {
            var p = columnPoints[i];
            Add("ContourPlate", "BASE PLATE", "7", "PL20*400", "S355J2", 400, 25.1, $"P{i + 1}",
                p.X, p.Y, 10);
        }

        // Bolts: M20 8.8, ~0.25 kg each
        for (var i = 0; i < 16; i++)
        {
            var p = columnPoints[i % columnPoints.Length];
            Add("Bolt", "M20", "0", "M20", "8.8", 0, 0.25, "BOLT",
                p.X + (i % 2 == 0 ? 60 : -60),
                p.Y + ((i / 2) % 2 == 0 ? 60 : -60),
                50);
        }

        return list;
    }
}
