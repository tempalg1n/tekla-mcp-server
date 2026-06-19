using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TSM = Tekla.Structures.Model;
using TSMUI = Tekla.Structures.Model.UI;

namespace TeklaMcp.Tekla;

/// <summary>
/// REAL implementation of <see cref="ITeklaModelService"/> backed by the Tekla Open API.
///
/// ⚠️  UNTESTED PROTOTYPE. None of this has been compiled or run against a live Tekla
///     instance yet — it was written from the Tekla 2026 Open API documentation on a
///     machine without Tekla. Treat every API call below as "needs verification on the
///     Windows machine". The exact member names (GetReportProperty keys, SelectModelObject,
///     ModelInfo fields) are the most likely places to need small fixes. See
///     docs/tekla-api-notes.md for the reference list and the official docs links.
///
/// How the connection works: this runs as a STANDALONE process and connects to an
/// already-running Tekla Structures with a model open. <c>new TSM.Model()</c> establishes
/// that connection; <c>GetConnectionStatus()</c> reports whether it succeeded.
/// </summary>
public sealed class TeklaModelService : ITeklaModelService
{
    private const string BackendName = "Tekla";
    private static readonly string[] DefaultAttributeCandidates =
    {
        "ASSEMBLY_POS",
        "PART_POS",
        "PROFILE",
        "MATERIAL",
        "NAME",
        "CLASS",
        "PHASE",
        "USER_PHASE",
        "RU_FN1_MRK",
    };

    public ConnectionInfo GetConnectionInfo()
    {
        try
        {
            var model = new TSM.Model();
            if (!model.GetConnectionStatus())
            {
                return new ConnectionInfo
                {
                    Connected = false,
                    Backend = BackendName,
                    Message = "Not connected. Is Tekla Structures running with a model open?",
                };
            }

            var info = model.GetInfo();
            return new ConnectionInfo
            {
                Connected = true,
                Backend = BackendName,
                ModelName = info.ModelName ?? "",
                ModelPath = info.ModelPath ?? "",
            };
        }
        catch (Exception ex)
        {
            return new ConnectionInfo { Connected = false, Backend = BackendName, Message = ex.Message };
        }
    }

    public ModelSummary GetModelSummary()
    {
        var model = GetConnectedModel();
        var summary = new ModelSummary { Backend = BackendName };

        var en = model.GetModelObjectSelector().GetAllObjects();
        while (en.MoveNext())
        {
            var info = Map(en.Current);
            if (info is null) continue;

            summary.TotalObjects++;
            summary.TotalWeightKg += info.WeightKg ?? 0;
            Bump(summary.CountByType, info.Type);
            Bump(summary.CountByClass, info.Class);
            Bump(summary.CountByProfile, info.Profile);
            Bump(summary.CountByMaterial, info.Material);
            if (info.WeightKg is double w)
                summary.WeightByMaterialKg[Key(info.Material)] =
                    summary.WeightByMaterialKg.TryGetValue(Key(info.Material), out var cur) ? cur + w : w;
        }

        summary.TotalWeightKg = Math.Round(summary.TotalWeightKg, 1);
        return summary;
    }

    public IReadOnlyList<ModelObjectInfo> GetAllObjects(int? limit = null)
    {
        var model = GetConnectedModel();
        var result = new List<ModelObjectInfo>();

        var en = model.GetModelObjectSelector().GetAllObjects();
        while (en.MoveNext())
        {
            var info = Map(en.Current);
            if (info is null) continue;
            result.Add(info);
            if (limit is int n && result.Count >= n) break;
        }
        return result;
    }

    public IReadOnlyList<ModelObjectInfo> FindObjects(ObjectQuery query, int? limit = null)
    {
        var model = GetConnectedModel();
        var result = new List<ModelObjectInfo>();

        var en = model.GetModelObjectSelector().GetAllObjects();
        while (en.MoveNext())
        {
            var mo = en.Current;
            var info = Map(mo);
            if (info is null || !Matches(info, query) || !MatchesUda(mo, query)) continue;
            result.Add(info);
            if (limit is int n && result.Count >= n) break;
        }
        return result;
    }

    public ModelObjectInfo? GetObjectByGuid(string guid)
    {
        var model = GetConnectedModel();
        if (!Guid.TryParse(guid, out var g)) return null;

        try
        {
            var identifier = new global::Tekla.Structures.Identifier(g);
            var mo = model.SelectModelObject(identifier);
            return mo is null ? null : Map(mo);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<ModelObjectInfo> GetSelectedObjects()
    {
        var result = new List<ModelObjectInfo>();
        var selector = new TSMUI.ModelObjectSelector();
        var en = selector.GetSelectedObjects();
        while (en.MoveNext())
        {
            var info = Map(en.Current);
            if (info != null) result.Add(info);
        }
        return result;
    }

    public SelectionResult SelectObjects(ObjectQuery query, int? limit = null)
    {
        try
        {
            var model = GetConnectedModel();
            var selector = model.GetModelObjectSelector();
            var toSelect = new ArrayList();
            var preview = new List<ModelObjectInfo>();

            var en = selector.GetAllObjects();
            while (en.MoveNext())
            {
                var mo = en.Current;
                var info = Map(mo);
                if (info is null || !Matches(info, query) || !MatchesUda(mo, query)) continue;

                toSelect.Add(mo);
                if (preview.Count < 20) preview.Add(info);
                if (limit is int n && n > 0 && toSelect.Count >= n) break;
            }

            var uiSelector = new TSMUI.ModelObjectSelector();
            uiSelector.Select(toSelect);

            return new SelectionResult
            {
                SelectedCount = toSelect.Count,
                Preview = preview,
                Backend = BackendName,
            };
        }
        catch
        {
            return new SelectionResult { SelectedCount = 0, Backend = BackendName };
        }
    }

    public IReadOnlyList<AttributeValueMatch> FindAttributesByValue(
        string value,
        IReadOnlyList<string>? candidateAttributeNames = null,
        bool exactMatch = false,
        int? objectLimit = 2000,
        int? resultLimit = 50)
    {
        var result = new Dictionary<string, AttributeValueMatch>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return new List<AttributeValueMatch>();

        try
        {
            var model = GetConnectedModel();
            var candidates = BuildAttributeCandidateList(candidateAttributeNames);
            var en = model.GetModelObjectSelector().GetAllObjects();
            var scanned = 0;

            while (en.MoveNext())
            {
                if (objectLimit is int maxObjects && maxObjects > 0 && scanned >= maxObjects) break;
                scanned++;

                var mo = en.Current;
                if (mo is null) continue;

                foreach (var attrName in candidates)
                {
                    if (!TryGetAttributeValue(mo, attrName, out var attrValue)) continue;
                    if (!ValueMatches(attrValue, value, exactMatch)) continue;

                    if (!result.TryGetValue(attrName, out var row))
                    {
                        row = new AttributeValueMatch { AttributeName = attrName };
                        result[attrName] = row;
                    }

                    row.MatchCount++;
                    if (!row.MatchedValues.Exists(v => string.Equals(v, attrValue, StringComparison.OrdinalIgnoreCase)) &&
                        row.MatchedValues.Count < 5)
                        row.MatchedValues.Add(attrValue);
                    if (row.SampleGuids.Count < 5)
                        row.SampleGuids.Add(mo.Identifier.GUID.ToString());
                }
            }
        }
        catch
        {
            // Best-effort tool: return accumulated results or empty on failure.
        }

        var ordered = new List<AttributeValueMatch>(result.Values);
        ordered.Sort((a, b) =>
        {
            var cmp = b.MatchCount.CompareTo(a.MatchCount);
            return cmp != 0 ? cmp : string.Compare(a.AttributeName, b.AttributeName, StringComparison.OrdinalIgnoreCase);
        });

        if (resultLimit is int maxRows && maxRows > 0 && ordered.Count > maxRows)
            ordered = ordered.GetRange(0, maxRows);

        return ordered;
    }

    public ProfileConnectionSummary AnalyzeConnectionsForProfile(string profile, double toleranceMm = 50, int? limit = 1000)
    {
        var summary = new ProfileConnectionSummary
        {
            SourceProfile = profile ?? "",
            ToleranceMm = toleranceMm,
        };

        try
        {
            var source = FindObjects(
                new ObjectQuery { Type = "Beam", Profile = profile },
                limit);
            var all = GetAllObjects(null);
            var signatures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var beam in source)
            {
                if (!HasLineData(beam)) continue;
                AddConnectionSignature(beam, all, toleranceMm, true, signatures);
                AddConnectionSignature(beam, all, toleranceMm, false, signatures);
            }

            var rows = signatures
                .Select(kv => new ProfileConnectionType
                {
                    Signature = kv.Key,
                    Occurrences = kv.Value,
                })
                .OrderByDescending(r => r.Occurrences)
                .ThenBy(r => r.Signature, StringComparer.OrdinalIgnoreCase)
                .ToList();

            summary.SourceObjects = source.Count;
            summary.UniqueConnectionTypes = rows.Count;
            summary.ConnectionTypes = rows;
        }
        catch
        {
            // Best-effort analysis; return partial/empty data on runtime failures.
        }

        return summary;
    }

    public ObjectUdaResult GetObjectUdas(string guid, IReadOnlyList<string> udaNames)
    {
        guid = guid ?? "";
        var result = new ObjectUdaResult
        {
            Guid = guid,
            Backend = BackendName,
        };

        try
        {
            var model = GetConnectedModel();
            var mo = TrySelectObjectByGuid(model, guid);
            if (mo is null)
            {
                result.Message = "Object not found.";
                return result;
            }

            result.Guid = mo.Identifier.GUID.ToString();
            result.Id = mo.Identifier.ID;
            result.Type = mo.GetType().Name;

            foreach (var udaName in udaNames)
            {
                if (string.IsNullOrWhiteSpace(udaName)) continue;
                if (TryGetUserPropertyAsString(mo, udaName, out var value))
                    result.Udas[udaName] = value;
            }
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }

        return result;
    }

    public UdaOperationResult SetObjectUdas(string guid, IReadOnlyDictionary<string, string> updates, bool apply)
    {
        var result = new UdaOperationResult
        {
            Applied = apply,
            Backend = BackendName,
        };

        try
        {
            var model = GetConnectedModel();
            var mo = TrySelectObjectByGuid(model, guid);
            if (mo is null)
            {
                result.Message = "Object not found.";
                return result;
            }

            var preview = Map(mo);
            if (preview != null) result.Preview.Add(preview);
            result.MatchedObjects = 1;

            if (!apply) return result;

            if (ApplyUdaUpdates(mo, updates, out var changedFields))
            {
                result.UpdatedObjects = 1;
                result.UpdatedFields = changedFields;
            }
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }

        return result;
    }

    public UdaOperationResult SetUdas(
        ObjectQuery query,
        IReadOnlyDictionary<string, string> updates,
        bool apply,
        int? limit = null)
    {
        var result = new UdaOperationResult
        {
            Applied = apply,
            Backend = BackendName,
        };

        try
        {
            var model = GetConnectedModel();
            var en = model.GetModelObjectSelector().GetAllObjects();
            while (en.MoveNext())
            {
                var mo = en.Current;
                var info = Map(mo);
                if (info is null || !Matches(info, query) || !MatchesUda(mo, query)) continue;

                if (limit is int n && n > 0 && result.MatchedObjects >= n) break;
                result.MatchedObjects++;
                if (result.Preview.Count < 20) result.Preview.Add(info);

                if (!apply) continue;
                if (ApplyUdaUpdates(mo, updates, out var changedFields))
                {
                    result.UpdatedObjects++;
                    result.UpdatedFields += changedFields;
                }
            }
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }

        return result;
    }

    // ---------------------------------------------------------------- internals ----

    private static TSM.Model GetConnectedModel()
    {
        var model = new TSM.Model();
        if (!model.GetConnectionStatus())
            throw new InvalidOperationException(
                "No connection to Tekla Structures. Start Tekla and open a model first.");
        return model;
    }

    /// <summary>Convert a Tekla <c>ModelObject</c> into our flat DTO. Returns null to skip.</summary>
    private static ModelObjectInfo? Map(TSM.ModelObject mo)
    {
        if (mo is null) return null;

        var info = new ModelObjectInfo
        {
            Id = mo.Identifier.ID,
            Guid = mo.Identifier.GUID.ToString(),
            Type = mo.GetType().Name,
        };

        // Parts (Beam, Column, ContourPlate, PolyBeam, ...) carry the rich properties.
        if (mo is TSM.Part part)
        {
            info.Name = part.Name ?? "";
            info.Class = part.Class ?? "";
            info.Profile = part.Profile?.ProfileString ?? "";
            info.Material = part.Material?.MaterialString ?? "";
            info.Finish = part.Finish;

            // Report properties are the reliable cross-object way to read derived values.
            var pos = "";
            if (part.GetReportProperty("ASSEMBLY_POS", ref pos) && !string.IsNullOrEmpty(pos))
                info.AssemblyPos = pos;

            double weight = 0;
            if (part.GetReportProperty("WEIGHT", ref weight))
                info.WeightKg = Math.Round(weight, 2);

            double length = 0;
            if (part.GetReportProperty("LENGTH", ref length))
                info.LengthMm = Math.Round(length, 1);

            if (mo is TSM.Beam beam)
            {
                info.StartX = Math.Round(beam.StartPoint.X, 2);
                info.StartY = Math.Round(beam.StartPoint.Y, 2);
                info.StartZ = Math.Round(beam.StartPoint.Z, 2);
                info.EndX = Math.Round(beam.EndPoint.X, 2);
                info.EndY = Math.Round(beam.EndPoint.Y, 2);
                info.EndZ = Math.Round(beam.EndPoint.Z, 2);
                info.CenterX = Math.Round((beam.StartPoint.X + beam.EndPoint.X) / 2.0, 2);
                info.CenterY = Math.Round((beam.StartPoint.Y + beam.EndPoint.Y) / 2.0, 2);
                info.CenterZ = Math.Round((beam.StartPoint.Z + beam.EndPoint.Z) / 2.0, 2);
            }

            try
            {
                var solid = part.GetSolid();
                if (solid != null)
                {
                    info.MinX = Math.Round(solid.MinimumPoint.X, 2);
                    info.MinY = Math.Round(solid.MinimumPoint.Y, 2);
                    info.MinZ = Math.Round(solid.MinimumPoint.Z, 2);
                    info.MaxX = Math.Round(solid.MaximumPoint.X, 2);
                    info.MaxY = Math.Round(solid.MaximumPoint.Y, 2);
                    info.MaxZ = Math.Round(solid.MaximumPoint.Z, 2);

                    if (!info.CenterX.HasValue)
                    {
                        info.CenterX = Math.Round((solid.MinimumPoint.X + solid.MaximumPoint.X) / 2.0, 2);
                        info.CenterY = Math.Round((solid.MinimumPoint.Y + solid.MaximumPoint.Y) / 2.0, 2);
                        info.CenterZ = Math.Round((solid.MinimumPoint.Z + solid.MaximumPoint.Z) / 2.0, 2);
                    }
                }
            }
            catch
            {
                // TODO(windows): verify solid extraction reliability for all model object types.
            }
        }

        return info;
    }

    private static bool Matches(ModelObjectInfo o, ObjectQuery q)
    {
        if (!string.IsNullOrWhiteSpace(q.Type) &&
            !string.Equals(o.Type, q.Type, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(q.Class) &&
            !string.Equals(o.Class, q.Class, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(q.Profile) &&
            o.Profile.IndexOf(q.Profile, StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (!string.IsNullOrWhiteSpace(q.Material) &&
            o.Material.IndexOf(q.Material, StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (!string.IsNullOrWhiteSpace(q.NameContains) &&
            o.Name.IndexOf(q.NameContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    private static bool MatchesUda(TSM.ModelObject mo, ObjectQuery q)
    {
        if (!string.IsNullOrWhiteSpace(q.UdaName) && !string.IsNullOrWhiteSpace(q.UdaEquals))
        {
            var udaName = q.UdaName!;
            if (!TryGetUserPropertyAsString(mo, udaName, out var udaValue))
                return false;
            if (!string.Equals(udaValue, q.UdaEquals, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(q.AttributeName))
        {
            var attributeName = q.AttributeName!;
            if (!TryGetAttributeValue(mo, attributeName, out var attrValue))
                return false;
            if (!string.IsNullOrWhiteSpace(q.AttributeEquals) &&
                !string.Equals(attrValue, q.AttributeEquals, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(q.AttributeContains) &&
                attrValue.IndexOf(q.AttributeContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        return true;
    }

    private static void Bump(IDictionary<string, int> d, string key)
    {
        key = Key(key);
        d[key] = d.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    private static string Key(string s) => string.IsNullOrEmpty(s) ? "(none)" : s;

    private static TSM.ModelObject? TrySelectObjectByGuid(TSM.Model model, string guid)
    {
        if (!Guid.TryParse(guid, out var parsed)) return null;
        try
        {
            var identifier = new global::Tekla.Structures.Identifier(parsed);
            return model.SelectModelObject(identifier);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetUserPropertyAsString(TSM.ModelObject mo, string udaName, out string value)
    {
        value = "";
        var strVal = "";
        if (mo.GetUserProperty(udaName, ref strVal))
        {
            value = strVal ?? "";
            return true;
        }

        var intVal = 0;
        if (mo.GetUserProperty(udaName, ref intVal))
        {
            value = intVal.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        var doubleVal = 0.0;
        if (mo.GetUserProperty(udaName, ref doubleVal))
        {
            value = doubleVal.ToString("G", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static bool TryGetAttributeValue(TSM.ModelObject mo, string attributeName, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(attributeName)) return false;

        var key = attributeName.Trim().ToUpperInvariant();
        switch (key)
        {
            case "GUID":
                value = mo.Identifier.GUID.ToString();
                return true;
            case "ID":
                value = mo.Identifier.ID.ToString(CultureInfo.InvariantCulture);
                return true;
        }

        if (TryGetUserPropertyAsString(mo, attributeName, out value))
            return true;

        var reportString = "";
        if (mo.GetReportProperty(attributeName, ref reportString) && !string.IsNullOrWhiteSpace(reportString))
        {
            value = reportString;
            return true;
        }

        var reportInt = 0;
        if (mo.GetReportProperty(attributeName, ref reportInt))
        {
            value = reportInt.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        var reportDouble = 0.0;
        if (mo.GetReportProperty(attributeName, ref reportDouble))
        {
            value = reportDouble.ToString("G", CultureInfo.InvariantCulture);
            return true;
        }

        if (mo is TSM.Part part)
        {
            switch (key)
            {
                case "NAME": value = part.Name ?? ""; return !string.IsNullOrWhiteSpace(value);
                case "CLASS": value = part.Class ?? ""; return !string.IsNullOrWhiteSpace(value);
                case "PROFILE": value = part.Profile?.ProfileString ?? ""; return !string.IsNullOrWhiteSpace(value);
                case "MATERIAL": value = part.Material?.MaterialString ?? ""; return !string.IsNullOrWhiteSpace(value);
                case "FINISH": value = part.Finish ?? ""; return !string.IsNullOrWhiteSpace(value);
            }
        }

        return false;
    }

    private static IReadOnlyList<string> BuildAttributeCandidateList(IReadOnlyList<string>? provided)
    {
        var set = new HashSet<string>(DefaultAttributeCandidates, StringComparer.OrdinalIgnoreCase);
        if (provided != null)
        {
            foreach (var candidate in provided)
                if (!string.IsNullOrWhiteSpace(candidate))
                    set.Add(candidate.Trim());
        }
        return set.ToList();
    }

    private static bool ValueMatches(string candidate, string expected, bool exactMatch)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;
        if (exactMatch) return string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase);
        return candidate.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasLineData(ModelObjectInfo beam) =>
        beam.StartX.HasValue && beam.StartY.HasValue && beam.StartZ.HasValue &&
        beam.EndX.HasValue && beam.EndY.HasValue && beam.EndZ.HasValue;

    private static void AddConnectionSignature(
        ModelObjectInfo beam,
        IReadOnlyList<ModelObjectInfo> all,
        double toleranceMm,
        bool useStartPoint,
        IDictionary<string, int> signatures)
    {
        var x = useStartPoint ? beam.StartX : beam.EndX;
        var y = useStartPoint ? beam.StartY : beam.EndY;
        var z = useStartPoint ? beam.StartZ : beam.EndZ;
        if (!x.HasValue || !y.HasValue || !z.HasValue) return;

        var neighbors = new List<string>();
        foreach (var obj in all)
        {
            if (string.Equals(obj.Guid, beam.Guid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsNearPoint(obj, x.Value, y.Value, z.Value, toleranceMm)) continue;
            neighbors.Add(BuildNeighborLabel(obj));
        }

        if (neighbors.Count == 0) return;
        var signature = string.Join(" + ", neighbors
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));

        signatures[signature] = signatures.TryGetValue(signature, out var cur) ? cur + 1 : 1;
    }

    private static bool IsNearPoint(ModelObjectInfo obj, double x, double y, double z, double toleranceMm)
    {
        var minX = (obj.MinX ?? obj.CenterX ?? x) - toleranceMm;
        var minY = (obj.MinY ?? obj.CenterY ?? y) - toleranceMm;
        var minZ = (obj.MinZ ?? obj.CenterZ ?? z) - toleranceMm;
        var maxX = (obj.MaxX ?? obj.CenterX ?? x) + toleranceMm;
        var maxY = (obj.MaxY ?? obj.CenterY ?? y) + toleranceMm;
        var maxZ = (obj.MaxZ ?? obj.CenterZ ?? z) + toleranceMm;

        return x >= minX && x <= maxX &&
               y >= minY && y <= maxY &&
               z >= minZ && z <= maxZ;
    }

    private static string BuildNeighborLabel(ModelObjectInfo obj)
    {
        var profile = string.IsNullOrWhiteSpace(obj.Profile) ? "(none)" : obj.Profile;
        return (obj.Type ?? "") + ":" + profile;
    }

    private static bool ApplyUdaUpdates(
        TSM.ModelObject mo,
        IReadOnlyDictionary<string, string> updates,
        out int changedFields)
    {
        changedFields = 0;
        foreach (var kv in updates)
        {
            var key = (kv.Key ?? "").Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;

            var raw = kv.Value ?? "";
            bool ok;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                ok = mo.SetUserProperty(key, intVal);
            else if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleVal))
                ok = mo.SetUserProperty(key, doubleVal);
            else
                ok = mo.SetUserProperty(key, raw);

            if (ok) changedFields++;
        }

        if (changedFields <= 0) return false;
        try
        {
            mo.Modify();
        }
        catch
        {
            // TODO(windows): verify whether Modify() is required for all object types.
        }

        return true;
    }
}
