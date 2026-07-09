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
    private int _nextId = 100000;

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

    public ModelSummary GetModelSummary(bool includeWeights = true, int? maxObjects = null)
    {
        var s = new ModelSummary { Backend = BackendName };
        foreach (var o in _objects)
        {
            if (maxObjects is int cap && cap > 0 && s.TotalObjects >= cap)
            {
                s.Truncated = true;
                s.Message = $"Scan stopped after {cap} of {_objects.Count} objects (maxObjects); all counts are partial.";
                break;
            }

            s.TotalObjects++;
            Bump(s.CountByType, o.Type);
            Bump(s.CountByClass, o.Class);
            Bump(s.CountByProfile, o.Profile);
            Bump(s.CountByMaterial, o.Material);
            if (includeWeights && o.WeightKg is double w)
            {
                s.TotalWeightKg += w;
                s.WeightByMaterialKg[o.Material] =
                    s.WeightByMaterialKg.TryGetValue(o.Material, out var cur) ? cur + w : w;
            }
        }
        if (!includeWeights)
            s.Message = (s.Message + " Weights skipped (includeWeights=false).").TrimStart();
        s.TotalWeightKg = Math.Round(s.TotalWeightKg, 1);
        return s;
    }

    public int CountObjects(ObjectQuery query) => FindObjects(query).Count;

    public IReadOnlyList<ModelObjectInfo> GetAllObjects(int? limit = null) => Limit(_objects, limit);

    public IReadOnlyList<ModelObjectInfo> FindObjects(ObjectQuery query, int? limit = null)
    {
        // Honor "scope = current UI selection" just like the real backend.
        IEnumerable<ModelObjectInfo> q = query.UseSelection ? _selectedObjects : _objects;
        if (query.GuidIn != null && query.GuidIn.Count > 0)
        {
            var guidSet = new HashSet<string>(query.GuidIn.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            q = q.Where(o => guidSet.Contains(o.Guid));
        }
        if (!string.IsNullOrWhiteSpace(query.Type)) q = q.Where(o => TeklaTypeAliases.TypeMatches(o.Type, query.Type));
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
                if (!string.IsNullOrWhiteSpace(query.AttributeNotEquals) &&
                    Eq(value, query.AttributeNotEquals)) return false;
                return true;
            });
        }
        return Limit(q.ToList(), limit);
    }

    public ModelObjectInfo? GetObjectByGuid(string guid) =>
        _objects.FirstOrDefault(o => string.Equals(o.Guid, guid, StringComparison.OrdinalIgnoreCase));

    public ObjectUdaResult GetProperties(string guid, IReadOnlyList<string> names)
    {
        var obj = GetObjectByGuid(guid);
        if (obj is null)
            return new ObjectUdaResult { Guid = guid ?? "", Backend = BackendName, Message = "Object not found." };

        var result = new ObjectUdaResult
        {
            Guid = obj.Guid,
            Id = obj.Id,
            Type = obj.Type,
            Backend = BackendName,
        };

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (TryGetAttributeValue(obj, name, out var value)) result.Udas[name] = value;
        }

        return result;
    }

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

    // -- Geometry / grids ---------------------------------------------------------------

    private static readonly List<GridLineInfo> Grids = new List<GridLineInfo>
    {
        new GridLineInfo { Axis = "X", Label = "1", Coordinate = 0 },
        new GridLineInfo { Axis = "X", Label = "2", Coordinate = 6000 },
        new GridLineInfo { Axis = "X", Label = "3", Coordinate = 12000 },
        new GridLineInfo { Axis = "Y", Label = "А", Coordinate = 0 },
        new GridLineInfo { Axis = "Y", Label = "Б", Coordinate = 6000 },
        new GridLineInfo { Axis = "Y", Label = "Д", Coordinate = 12000 },
    };

    public IReadOnlyList<GridLineInfo> GetGrids() => Grids;

    public PointResult ResolvePoint(string axisXLabel, string axisYLabel, double z)
    {
        var result = new PointResult { AxisX = axisXLabel, AxisY = axisYLabel, Z = z };
        var gx = Grids.FirstOrDefault(g => g.Axis == "X" && Eq(g.Label, axisXLabel));
        var gy = Grids.FirstOrDefault(g => g.Axis == "Y" && Eq(g.Label, axisYLabel));
        if (gx is null || gy is null)
        {
            result.Message = $"Grid label not found (X='{axisXLabel}': {(gx != null)}, Y='{axisYLabel}': {(gy != null)}).";
            return result;
        }
        result.Resolved = true;
        result.X = gx.Coordinate;
        result.Y = gy.Coordinate;
        return result;
    }

    // -- Mutations ----------------------------------------------------------------------

    public WriteResult CreateParts(IReadOnlyList<PartSpec> specs, bool apply)
    {
        var result = new WriteResult { Operation = "create", Applied = apply, Backend = BackendName };
        if (specs == null || specs.Count == 0) { result.Message = "No specs provided."; return result; }

        foreach (var spec in specs)
        {
            var guid = Guid.NewGuid().ToString();
            var info = MakeInfoFromSpec(spec, guid, _nextId++);
            result.PlannedCount++;
            if (result.Preview.Count < 20) result.Preview.Add(info);

            if (!apply) continue;
            _objects.Add(info);
            _udasByGuid[guid] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MCP_ORIGIN"] = "mcp:create",
            };
            result.CreatedCount++;
            result.CreatedGuids.Add(guid);
        }
        return result;
    }

    public WriteResult ModifyParts(IReadOnlyList<PartModification> modifications, bool apply)
    {
        var result = new WriteResult { Operation = "modify", Applied = apply, Backend = BackendName };
        if (modifications == null || modifications.Count == 0) { result.Message = "No modifications provided."; return result; }

        foreach (var mod in modifications)
        {
            var obj = GetObjectByGuid(mod.Guid);
            if (obj is null) { result.Errors.Add($"Not found: {mod.Guid}"); continue; }

            result.PlannedCount++;
            if (result.Preview.Count < 20) result.Preview.Add(obj);
            if (!apply) continue;

            if (mod.Profile != null) obj.Profile = mod.Profile;
            if (mod.Material != null) obj.Material = mod.Material;
            if (mod.Class != null) obj.Class = mod.Class;
            if (mod.Name != null) obj.Name = mod.Name;
            if (mod.SwapHandles) SwapEnds(obj);
            if (mod.NewStart != null) { obj.StartX = mod.NewStart.X; obj.StartY = mod.NewStart.Y; obj.StartZ = mod.NewStart.Z; }
            if (mod.NewEnd != null) { obj.EndX = mod.NewEnd.X; obj.EndY = mod.NewEnd.Y; obj.EndZ = mod.NewEnd.Z; }
            Recenter(obj);
            StampOrigin(obj.Guid, "mcp:modify");
            result.ModifiedCount++;
        }
        return result;
    }

    public WriteResult DeleteObjects(ObjectQuery query, bool apply, int? limit = null)
    {
        var matched = FindObjects(query, limit).ToList();
        var result = new WriteResult
        {
            Operation = "delete",
            Applied = apply,
            Backend = BackendName,
            PlannedCount = matched.Count,
            Preview = matched.Take(20).ToList(),
        };
        if (!apply) return result;

        foreach (var obj in matched)
        {
            _objects.RemoveAll(o => string.Equals(o.Guid, obj.Guid, StringComparison.OrdinalIgnoreCase));
            _udasByGuid.Remove(obj.Guid);
            result.DeletedCount++;
        }
        return result;
    }

    // -- Script escape hatch --------------------------------------------------------------

    public ScriptResult ExecuteScript(string code, bool allowMutations = false, int timeoutSeconds = 60)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ScriptResult { Backend = BackendName, Stage = "policy" };

        var violations = Scripting.ScriptPolicy.Validate(code, allowMutations);
        if (violations.Count > 0)
        {
            result.PolicyViolations.AddRange(violations);
            result.Guidance = "Fix the policy violations and retry.";
            result.DurationMs = watch.ElapsedMilliseconds;
            return result;
        }

        // Compile-only validation: possible on any OS when the Tekla DLLs are available
        // (e.g. extracted from the NuGet packages — see tools/TeklaApiDoc/README.md).
        result.Stage = "compile";
        var dllDir = Environment.GetEnvironmentVariable("TEKLA_MCP_SCRIPT_REF_DIR");
        if (!string.IsNullOrWhiteSpace(dllDir) && System.IO.Directory.Exists(dllDir))
        {
            var dlls = System.IO.Directory.GetFiles(dllDir, "Tekla.*.dll");
            var script = Scripting.ScriptEngine.Create(
                code, Scripting.ScriptEngine.BuildReferences(teklaDllPaths: dlls));
            result.CompileErrors.AddRange(Scripting.ScriptEngine.Compile(script));
            if (result.CompileErrors.Count > 0)
            {
                result.Guidance = "Fix the compile errors and retry. Verify signatures with tekla_search_api.";
                result.DurationMs = watch.ElapsedMilliseconds;
                return result;
            }
        }
        else
        {
            result.Guidance = "Compilation was SKIPPED (no Tekla DLLs on this machine — set " +
                              "TEKLA_MCP_SCRIPT_REF_DIR to a folder with Tekla.Structures*.dll to enable it). ";
        }

        result.Success = true;
        result.Guidance = (result.Guidance ?? "") +
                          "Mock backend: the script was validated but NOT executed — execution requires " +
                          "the real Tekla backend on Windows. Do not fabricate results from this run.";
        result.DurationMs = watch.ElapsedMilliseconds;
        return result;
    }

    private void StampOrigin(string guid, string value)
    {
        if (!_udasByGuid.TryGetValue(guid, out var udas))
        {
            udas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _udasByGuid[guid] = udas;
        }
        udas["MCP_ORIGIN"] = value;
    }

    private static void SwapEnds(ModelObjectInfo o)
    {
        (o.StartX, o.EndX) = (o.EndX, o.StartX);
        (o.StartY, o.EndY) = (o.EndY, o.StartY);
        (o.StartZ, o.EndZ) = (o.EndZ, o.StartZ);
    }

    private static void Recenter(ModelObjectInfo o)
    {
        if (o.StartX.HasValue && o.EndX.HasValue) o.CenterX = (o.StartX + o.EndX) / 2.0;
        if (o.StartY.HasValue && o.EndY.HasValue) o.CenterY = (o.StartY + o.EndY) / 2.0;
        if (o.StartZ.HasValue && o.EndZ.HasValue) o.CenterZ = (o.StartZ + o.EndZ) / 2.0;
    }

    private static ModelObjectInfo MakeInfoFromSpec(PartSpec spec, string guid, int id)
    {
        var kind = (spec.Kind ?? "beam").Trim().ToLowerInvariant();
        var info = new ModelObjectInfo
        {
            Guid = guid,
            Id = id,
            Type = kind == "plate" ? "ContourPlate" : (kind == "column" ? "Column" : "Beam"),
            Name = spec.Name ?? "",
            Class = spec.Class ?? "",
            Profile = spec.Profile ?? "",
            Material = spec.Material ?? "",
            Finish = "PAINT",
        };

        if (spec.Start != null && spec.End != null)
        {
            info.StartX = spec.Start.X; info.StartY = spec.Start.Y; info.StartZ = spec.Start.Z;
            info.EndX = spec.End.X; info.EndY = spec.End.Y; info.EndZ = spec.End.Z;
            var dx = spec.End.X - spec.Start.X;
            var dy = spec.End.Y - spec.Start.Y;
            var dz = spec.End.Z - spec.Start.Z;
            var len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            info.LengthMm = Math.Round(len, 1);
            info.WeightKg = Math.Round(len / 1000.0 * 50.0, 1); // nominal synthetic density
            Recenter(info);
        }
        else if (spec.Contour != null && spec.Contour.Count > 0)
        {
            info.CenterX = spec.Contour.Average(p => p.X);
            info.CenterY = spec.Contour.Average(p => p.Y);
            info.CenterZ = spec.Contour.Average(p => p.Z);
            info.WeightKg = 25.0;
        }

        return info;
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
            case "WEIGHT": value = obj.WeightKg?.ToString() ?? ""; return obj.WeightKg.HasValue;
            case "LENGTH": value = obj.LengthMm?.ToString() ?? ""; return obj.LengthMm.HasValue;
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
        var boltIndex = 0;
        foreach (var obj in _objects)
        {
            var baseMark = obj.Name == "COLUMN" ? "BK1" : (obj.Name == "BEAM" ? "BK2" : "");
            var udas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MCP_TAG"] = "mock",
                ["MCP_TYPE"] = obj.Type,
                ["RU_FN1_MRK"] = baseMark,
                ["RU_OBJ_TYPE"] = obj.Name,
            };

            // Bolts carry grade/standard as report attributes (mirrors the live backend), so
            // attribute filters like BOLT_GRADE != 88 are exercised. Alternate 8.8 / 10.9.
            if (string.Equals(obj.Type, "BoltArray", StringComparison.OrdinalIgnoreCase))
            {
                var highGrade = boltIndex % 2 == 1;
                udas["BOLT_GRADE"] = highGrade ? "109" : "88";
                udas["BOLT_STANDARD"] = highGrade ? "7805-10.9" : "7990-8.8";
                boltIndex++;
            }

            _udasByGuid[obj.Guid] = udas;
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

        // Bolts: M20, ~0.25 kg each. Type is "BoltArray" (what the live Tekla API reports),
        // and — like the real backend — the strength grade lives in the BOLT_GRADE attribute
        // (seeded in SeedUdas), NOT in Material. See TeklaTypeAliases for the "Bolt" alias.
        for (var i = 0; i < 16; i++)
        {
            var p = columnPoints[i % columnPoints.Length];
            Add("BoltArray", "M20", "0", "M20", "", 0, 0.25, "BOLT",
                p.X + (i % 2 == 0 ? 60 : -60),
                p.Y + ((i / 2) % 2 == 0 ? 60 : -60),
                50);
        }

        return list;
    }
}
