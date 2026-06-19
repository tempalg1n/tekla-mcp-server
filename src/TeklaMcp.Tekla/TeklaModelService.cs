using System;
using System.Collections.Generic;
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
            var info = Map(en.Current);
            if (info is null || !Matches(info, query)) continue;
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

    private static void Bump(IDictionary<string, int> d, string key)
    {
        key = Key(key);
        d[key] = d.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    private static string Key(string s) => string.IsNullOrEmpty(s) ? "(none)" : s;
}
