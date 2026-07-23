using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TS = Tekla.Structures;
using TSM = Tekla.Structures.Model;
using TSMC = Tekla.Structures.Model.Collaboration;
using TSMI = Tekla.Structures.ModelInternal;
using TSMUI = Tekla.Structures.Model.UI;
using TSG = Tekla.Structures.Geometry3d;

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
public sealed partial class TeklaModelService : ITeklaModelService
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

    private static bool _oneTimeInitDone;

    private static void EnsureTeklaReady()
    {
        // Per-version build (issue #11): refuse to talk to a Tekla whose major version differs
        // from the one this build was compiled for — BEFORE any remoting call can fail with
        // something cryptic. Also catches binds the resolver never saw (e.g. a same-baseline
        // GAC copy on a machine running a different Tekla, the old issue #7 failure mode).
        TeklaAssemblyResolver.EnsureVersionMatch();

        // Align() caches its own result and keeps retrying while Tekla publishes no pipes yet,
        // so calling it per-operation makes "start server first, open Tekla later" work.
        TeklaRemotingChannel.Align();

        if (_oneTimeInitDone) return;
        _oneTimeInitDone = true;

        // Process-wide switch (static): fetch object data in batches during enumeration instead
        // of one remoting round-trip per property read — the biggest speedup on large models
        // (issue #5).
        try { TSM.ModelObjectEnumerator.AutoFetch = true; }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[tekla] AutoFetch unavailable (continuing): " + ErrorText.Flatten(ex));
        }

        // A GAC-sourced bind is harmless with per-version builds — a strong-named bind needs
        // the exact compiled version, which is protocol-compatible by definition — but log it,
        // as it means the running Tekla's own (possibly service-packed) DLLs were bypassed.
        var asm = typeof(TSM.Model).Assembly;
        if (asm.GlobalAssemblyCache)
            Console.Error.WriteLine(
                $"[tekla] note: Tekla.Structures.Model {asm.GetName().Version} was loaded from the GAC " +
                $"(same version as this build), not from {TeklaAssemblyResolver.BinDir ?? "the Tekla bin"}.");
    }

    static TeklaModelService()
    {
        // Intentionally empty — touching Tekla types here (Align / AutoFetch) can recurse
        // through AssemblyResolve while TeklaMcp.Tekla is still loading and overflow the stack.
        // All Tekla-touching init lives in EnsureTeklaReady(), called lazily per operation.
    }

    public ConnectionInfo GetConnectionInfo()
    {
        try
        {
            EnsureTeklaReady();
            var model = new TSM.Model();
            if (!model.GetConnectionStatus())
            {
                return new ConnectionInfo
                {
                    Connected = false,
                    Backend = BackendName,
                    Message = "Not connected. Is Tekla Structures running with a model open? " +
                              "(" + TeklaRemotingChannel.Describe() + ")",
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
            return new ConnectionInfo { Connected = false, Backend = BackendName, Message = ErrorText.Flatten(ex) };
        }
    }

    public ModelSummary GetModelSummary(bool includeWeights = true, int? maxObjects = null)
    {
        var model = GetConnectedModel();
        var summary = new ModelSummary { Backend = BackendName };

        // Streaming aggregation over cheap reads only: type + direct Part properties and
        // (optionally) the WEIGHT report property. No Map(), no solids, no LENGTH/ASSEMBLY_POS —
        // on large models those made this tool run past the MCP client's timeout (issue #5).
        var en = model.GetModelObjectSelector().GetAllObjects();
        while (en.MoveNext())
        {
            var mo = en.Current;
            if (mo is null) continue;

            if (maxObjects is int cap && cap > 0 && summary.TotalObjects >= cap)
            {
                summary.Truncated = true;
                summary.Message = $"Scan stopped after {cap} objects (maxObjects); all counts are partial.";
                break;
            }

            summary.TotalObjects++;
            Bump(summary.CountByType, mo.GetType().Name);

            if (mo is TSM.Part part)
            {
                var material = part.Material?.MaterialString ?? "";
                Bump(summary.CountByClass, part.Class ?? "");
                Bump(summary.CountByProfile, part.Profile?.ProfileString ?? "");
                Bump(summary.CountByMaterial, material);

                if (includeWeights)
                {
                    double weight = 0;
                    if (part.GetReportProperty("WEIGHT", ref weight))
                    {
                        summary.TotalWeightKg += weight;
                        summary.WeightByMaterialKg[Key(material)] =
                            summary.WeightByMaterialKg.TryGetValue(Key(material), out var cur) ? cur + weight : weight;
                    }
                }
            }
            else
            {
                Bump(summary.CountByClass, "");
                Bump(summary.CountByProfile, "");
                Bump(summary.CountByMaterial, "");
            }
        }

        if (!includeWeights)
            summary.Message = (summary.Message + " Weights skipped (includeWeights=false).").TrimStart();
        summary.TotalWeightKg = Math.Round(summary.TotalWeightKg, 1);
        return summary;
    }

    public int CountObjects(ObjectQuery query)
    {
        var model = GetConnectedModel();
        query = query ?? new ObjectQuery();

        // Completely unfiltered whole-model count: the enumerator knows its size — no walk.
        if (!query.UseSelection && IsUnfiltered(query))
            return model.GetModelObjectSelector().GetAllObjects().GetSize();

        // Filtered count: match on cheap direct properties only — never Map() (no report
        // properties, no solids). UDA/attribute reads happen only when the query uses them.
        var count = 0;
        foreach (var mo in EnumerateSource(model, query))
        {
            var info = MapBasic(mo);
            if (info is null || !Matches(info, query) || !MatchesUda(mo, query)) continue;
            count++;
        }
        return count;
    }

    private static bool IsUnfiltered(ObjectQuery q) =>
        (q.GuidIn is null || q.GuidIn.Count == 0) &&
        string.IsNullOrWhiteSpace(q.Type) &&
        string.IsNullOrWhiteSpace(q.Class) &&
        string.IsNullOrWhiteSpace(q.Profile) &&
        string.IsNullOrWhiteSpace(q.Material) &&
        string.IsNullOrWhiteSpace(q.NameContains) &&
        string.IsNullOrWhiteSpace(q.UdaName) &&
        string.IsNullOrWhiteSpace(q.AttributeName);

    public IReadOnlyList<ModelObjectInfo> GetAllObjects(int? limit = null)
    {
        var model = GetConnectedModel();
        return InGlobalWorkPlane(model, () =>
        {
            var result = new List<ModelObjectInfo>();
            var en = model.GetModelObjectSelector().GetAllObjects();
            while (en.MoveNext())
            {
                var info = Map(en.Current);
                if (info is null) continue;
                result.Add(info);
                if (limit is int n && result.Count >= n) break;
            }
            return (IReadOnlyList<ModelObjectInfo>)result;
        });
    }

    public IReadOnlyList<ModelObjectInfo> FindObjects(ObjectQuery query, int? limit = null)
    {
        var model = GetConnectedModel();
        return InGlobalWorkPlane(model, () =>
        {
            var result = new List<ModelObjectInfo>();
            foreach (var mo in EnumerateSource(model, query))
            {
                // Cheap-first: match on directly readable properties, then pay for report
                // properties + solid only on the objects that matched (bounded by limit).
                var info = MapBasic(mo);
                if (info is null || !Matches(info, query) || !MatchesUda(mo, query)) continue;
                Enrich(mo, info, includeSolid: true);
                result.Add(info);
                if (limit is int n && result.Count >= n) break;
            }
            return (IReadOnlyList<ModelObjectInfo>)result;
        });
    }

    public ObjectUdaResult GetProperties(string guid, IReadOnlyList<string> names)
    {
        var result = new ObjectUdaResult { Guid = guid ?? "", Backend = BackendName };

        try
        {
            var model = GetConnectedModel();
            var mo = TrySelectObjectByGuid(model, guid ?? "");
            if (mo is null)
            {
                result.Message = "Object not found.";
                return result;
            }

            result.Guid = mo.Identifier.GUID.ToString();
            result.Id = mo.Identifier.ID;
            result.Type = mo.GetType().Name;

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (TryGetAttributeValue(mo, name, out var value)) result.Udas[name] = value;
            }
        }
        catch (Exception ex)
        {
            result.Message = ErrorText.Flatten(ex);
        }

        return result;
    }

    public ModelObjectInfo? GetObjectByGuid(string guid)
    {
        var model = GetConnectedModel();
        if (!Guid.TryParse(guid, out var g)) return null;

        try
        {
            return InGlobalWorkPlane(model, () =>
            {
                var identifier = new global::Tekla.Structures.Identifier(g);
                var mo = model.SelectModelObject(identifier);
                return mo is null ? null : Map(mo);
            });
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<ModelObjectInfo> GetSelectedObjects()
    {
        var model = GetConnectedModel();
        return InGlobalWorkPlane(model, () =>
        {
            var result = new List<ModelObjectInfo>();
            var selector = new TSMUI.ModelObjectSelector();
            var en = selector.GetSelectedObjects();
            while (en.MoveNext())
            {
                var info = Map(en.Current);
                if (info != null) result.Add(info);
            }
            return (IReadOnlyList<ModelObjectInfo>)result;
        });
    }

    public IReadOnlyList<ReferenceGeometryInfo> GetReferenceGeometry(
        IReadOnlyList<int> ids,
        bool useSelection = false,
        int maxObjects = 20,
        int maxFacesPerObject = 100,
        int maxTotalFaces = 1000,
        int maxTotalPoints = 20000,
        IReadOnlyList<string>? externalGuids = null)
    {
        var result = new List<ReferenceGeometryInfo>();
        maxObjects = Math.Max(0, Math.Min(maxObjects, 100));
        maxFacesPerObject = Math.Max(0, Math.Min(maxFacesPerObject, 1000));
        maxTotalFaces = Math.Max(0, Math.Min(maxTotalFaces, 5000));
        maxTotalPoints = Math.Max(0, Math.Min(maxTotalPoints, 100000));
        if (maxObjects == 0) return result;

        try
        {
            var model = GetConnectedModel();
            var modelPath = "";
            try { modelPath = model.GetInfo()?.ModelPath ?? ""; }
            catch { /* used only to resolve relative reference-model paths */ }
            var objects = new List<TSM.ReferenceModelObject>();

            if (externalGuids != null && externalGuids.Count > 0)
            {
                foreach (var externalGuid in externalGuids
                             .Where(g => !string.IsNullOrWhiteSpace(g))
                             .Distinct()
                             .Take(maxObjects))
                {
                    var found = FindByExternalGuid(model, externalGuid, out var lookupMessage);
                    if (found != null) objects.Add(found);
                    else
                        result.Add(new ReferenceGeometryInfo
                        {
                            ExternalGuid = externalGuid,
                            Message = lookupMessage ?? "No reference object with this IFC GUID.",
                        });
                }
            }
            else if (useSelection)
            {
                var selected = new TSMUI.ModelObjectSelector().GetSelectedObjects();
                while (objects.Count < maxObjects && selected.MoveNext())
                    if (selected.Current is TSM.ReferenceModelObject selectedReference)
                        objects.Add(selectedReference);
            }
            else
            {
                foreach (var id in (ids ?? new List<int>()).Distinct().Take(maxObjects))
                {
                    try
                    {
                        var selected = model.SelectModelObject(new TS.Identifier(id));
                        if (selected is TSM.ReferenceModelObject reference)
                            objects.Add(reference);
                        else
                            result.Add(new ReferenceGeometryInfo
                            {
                                Id = id,
                                Message = selected == null
                                    ? "Reference model object not found."
                                    : "Object is " + selected.GetType().Name + ", not ReferenceModelObject.",
                            });
                    }
                    catch (Exception exItem)
                    {
                        result.Add(new ReferenceGeometryInfo { Id = id, Message = ErrorText.Flatten(exItem) });
                    }
                }
            }

            var facesLeft = maxTotalFaces;
            var pointsLeft = maxTotalPoints;
            foreach (var reference in objects.Take(Math.Max(0, maxObjects - result.Count)))
            {
                var allowedFaces = Math.Min(maxFacesPerObject, facesLeft);
                var info = InGlobalWorkPlane(
                    model,
                    () => MapReferenceGeometry(reference, allowedFaces, pointsLeft, modelPath));
                facesLeft -= info.Faces.Count;
                pointsLeft -= info.Faces.Sum(face => face.Points.Count);
                if (maxFacesPerObject > 0 &&
                    (allowedFaces == 0 || pointsLeft <= 0 || facesLeft <= 0))
                {
                    info.Truncated = true;
                    info.Message = AppendMessage(
                        info.Message, "Global face/point response budget reached.");
                }
                result.Add(info);
            }
        }
        catch (Exception ex)
        {
            result.Add(new ReferenceGeometryInfo { Message = ErrorText.Flatten(ex) });
        }

        return result;
    }

    public SelectionResult SelectObjects(ObjectQuery query, int? limit = null)
    {
        try
        {
            var model = GetConnectedModel();
            var toSelect = new ArrayList();
            var preview = new List<ModelObjectInfo>();

            foreach (var mo in EnumerateSource(model, query))
            {
                var info = MapBasic(mo);
                if (info is null || !Matches(info, query) || !MatchesUda(mo, query)) continue;

                toSelect.Add(mo);
                if (preview.Count < 20)
                {
                    Enrich(mo, info, includeSolid: true);
                    preview.Add(info);
                }
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
            result.Message = ErrorText.Flatten(ex);
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
            result.Message = ErrorText.Flatten(ex);
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
            foreach (var mo in EnumerateSource(model, query))
            {
                var info = MapBasic(mo);
                if (info is null || !Matches(info, query) || !MatchesUda(mo, query)) continue;

                if (limit is int n && n > 0 && result.MatchedObjects >= n) break;
                result.MatchedObjects++;
                if (result.Preview.Count < 20)
                {
                    Enrich(mo, info, includeSolid: true);
                    result.Preview.Add(info);
                }

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
            result.Message = ErrorText.Flatten(ex);
        }

        return result;
    }

    // -- Geometry / grids ---------------------------------------------------------------
    //
    // ⚠️ UNTESTED. Grid coordinate-string parsing and label generation are the most fragile
    //    part here. Tekla grid coordinate strings may be absolute or relative, and custom
    //    labels (incl. Cyrillic) are NOT read yet — labels are generated by convention
    //    (X => 1,2,3…; Y => А,Б,В…). Verify and adjust on the live model.

    private static readonly string[] CyrillicLabels =
        { "А", "Б", "В", "Г", "Д", "Е", "Ж", "И", "К", "Л", "М", "Н", "П", "Р", "С", "Т" };

    public IReadOnlyList<GridLineInfo> GetGrids()
    {
        var grids = new List<GridLineInfo>();
        try
        {
            var model = GetConnectedModel();
            var en = model.GetModelObjectSelector()
                          .GetAllObjectsWithType(TSM.ModelObject.ModelObjectEnum.GRID);
            while (en.MoveNext())
            {
                if (!(en.Current is TSM.Grid grid)) continue;
                AddGridLines("X", grid.CoordinateX, grids);
                AddGridLines("Y", grid.CoordinateY, grids);
            }
        }
        catch
        {
            // TODO(windows): verify Grid type + CoordinateX/Y parsing.
        }
        return grids;
    }

    public PointResult ResolvePoint(string axisXLabel, string axisYLabel, double z)
    {
        var result = new PointResult { AxisX = axisXLabel, AxisY = axisYLabel, Z = z };
        var grids = GetGrids();
        var gx = grids.FirstOrDefault(g => g.Axis == "X" &&
                    string.Equals(g.Label, axisXLabel, StringComparison.OrdinalIgnoreCase));
        var gy = grids.FirstOrDefault(g => g.Axis == "Y" &&
                    string.Equals(g.Label, axisYLabel, StringComparison.OrdinalIgnoreCase));
        if (gx is null || gy is null)
        {
            result.Message = $"Grid label not found (X='{axisXLabel}': {gx != null}, Y='{axisYLabel}': {gy != null}).";
            return result;
        }
        result.Resolved = true;
        result.X = gx.Coordinate;
        result.Y = gy.Coordinate;
        return result;
    }

    private static void AddGridLines(string axis, string coordString, List<GridLineInfo> sink)
    {
        if (string.IsNullOrWhiteSpace(coordString)) return;
        var coords = ParseGridCoordinates(coordString);
        for (var index = 0; index < coords.Count; index++)
        {
            sink.Add(new GridLineInfo { Axis = axis, Label = LabelFor(axis, index), Coordinate = coords[index] });
        }
    }

    private static List<double> ParseGridCoordinates(string coordString)
    {
        var coords = new List<double>();
        var tokens = coordString.Split(new[] { ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim();
            var starIndex = token.IndexOf('*');

            // Tekla grids often use repeat syntax such as "4*6000".
            if (starIndex > 0 &&
                starIndex < token.Length - 1 &&
                int.TryParse(token.Substring(0, starIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var repeat) &&
                repeat > 0 &&
                TryParseInvariantDouble(token.Substring(starIndex + 1), out var step))
            {
                if (coords.Count == 0) coords.Add(0d);
                var current = coords[coords.Count - 1];
                for (var i = 0; i < repeat; i++)
                {
                    current += step;
                    coords.Add(current);
                }
                continue;
            }

            if (TryParseInvariantDouble(token, out var absoluteCoord))
                coords.Add(absoluteCoord);
        }
        return coords;
    }

    private static bool TryParseInvariantDouble(string token, out double value)
        => double.TryParse(token.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string LabelFor(string axis, int index)
    {
        if (axis == "X") return (index + 1).ToString(CultureInfo.InvariantCulture);
        return index < CyrillicLabels.Length ? CyrillicLabels[index] : "Y" + (index + 1);
    }

    // -- Mutations (create / modify / delete) -------------------------------------------
    //
    // ⚠️ UNTESTED. Preview (apply == false) never touches Tekla. On apply we force the GLOBAL
    //    transformation plane (so coordinates are interpreted as global model mm), insert/
    //    modify/delete, stamp MCP_ORIGIN, then CommitChanges once. Verify on the live model.

    private static TSG.Point ToPoint(Point3D p) => new TSG.Point(p.X, p.Y, p.Z);

    public WriteResult CreateParts(IReadOnlyList<PartSpec> specs, bool apply)
    {
        var result = new WriteResult { Operation = "create", Applied = apply, Backend = BackendName };
        if (specs == null || specs.Count == 0) { result.Message = "No specs provided."; return result; }

        if (!apply)
        {
            foreach (var spec in specs)
            {
                result.PlannedCount++;
                if (result.Preview.Count < 20) result.Preview.Add(PreviewInfo(spec));
            }
            return result;
        }

        try
        {
            var model = GetConnectedModel();
            var wph = model.GetWorkPlaneHandler();
            var previous = wph.GetCurrentTransformationPlane();
            wph.SetCurrentTransformationPlane(new TSM.TransformationPlane()); // global
            try
            {
                foreach (var spec in specs)
                {
                    result.PlannedCount++;
                    try
                    {
                        var created = CreateOne(model, spec);
                        if (created is null) { result.Errors.Add("Create failed for kind=" + spec.Kind); continue; }
                        created.SetUserProperty("MCP_ORIGIN", "mcp:create");
                        result.CreatedCount++;
                        var info = Map(created);
                        if (info != null)
                        {
                            result.CreatedGuids.Add(info.Guid);
                            if (result.Preview.Count < 20) result.Preview.Add(info);
                        }
                    }
                    catch (Exception exItem) { result.Errors.Add(ErrorText.Flatten(exItem)); }
                }
                model.CommitChanges();
            }
            finally { wph.SetCurrentTransformationPlane(previous); }
        }
        catch (Exception ex) { result.Message = ErrorText.Flatten(ex); }
        return result;
    }

    public WriteResult ModifyParts(IReadOnlyList<PartModification> modifications, bool apply)
    {
        var result = new WriteResult { Operation = "modify", Applied = apply, Backend = BackendName };
        if (modifications == null || modifications.Count == 0) { result.Message = "No modifications provided."; return result; }

        try
        {
            var model = GetConnectedModel();

            if (!apply)
            {
                foreach (var mod in modifications)
                {
                    result.PlannedCount++;
                    var moPrev = TrySelectObjectByGuid(model, mod.Guid);
                    if (moPrev is null) { result.Errors.Add("Not found: " + mod.Guid); continue; }
                    var info = Map(moPrev);
                    if (info != null && result.Preview.Count < 20) result.Preview.Add(info);
                }
                return result;
            }

            var wph = model.GetWorkPlaneHandler();
            var previous = wph.GetCurrentTransformationPlane();
            wph.SetCurrentTransformationPlane(new TSM.TransformationPlane()); // global
            try
            {
                foreach (var mod in modifications)
                {
                    result.PlannedCount++;
                    var mo = TrySelectObjectByGuid(model, mod.Guid);
                    if (mo is null) { result.Errors.Add("Not found: " + mod.Guid); continue; }
                    try
                    {
                        if (mo is TSM.Part part)
                        {
                            if (!string.IsNullOrWhiteSpace(mod.Profile)) part.Profile.ProfileString = mod.Profile;
                            if (!string.IsNullOrWhiteSpace(mod.Material)) part.Material.MaterialString = mod.Material;
                            if (!string.IsNullOrWhiteSpace(mod.Class)) part.Class = mod.Class;
                            if (!string.IsNullOrWhiteSpace(mod.Name)) part.Name = mod.Name;
                            ApplyMatchedAndExplicitPosition(
                                model, part, mod.MatchPositionGuid, mod.Position);
                        }
                        if (mo is TSM.Beam beam)
                        {
                            if (mod.SwapHandles)
                            {
                                var tmp = beam.StartPoint;
                                beam.StartPoint = beam.EndPoint;
                                beam.EndPoint = tmp;
                            }
                            if (mod.NewStart != null) beam.StartPoint = ToPoint(mod.NewStart);
                            if (mod.NewEnd != null) beam.EndPoint = ToPoint(mod.NewEnd);
                        }
                        mo.SetUserProperty("MCP_ORIGIN", "mcp:modify");
                        mo.Modify();
                        result.ModifiedCount++;
                        var info = Map(mo);
                        if (info != null && result.Preview.Count < 20) result.Preview.Add(info);
                    }
                    catch (Exception exItem) { result.Errors.Add(ErrorText.Flatten(exItem)); }
                }
                model.CommitChanges();
            }
            finally { wph.SetCurrentTransformationPlane(previous); }
        }
        catch (Exception ex) { result.Message = ErrorText.Flatten(ex); }
        return result;
    }

    public WriteResult DeleteObjects(ObjectQuery query, bool apply, int? limit = null)
    {
        var result = new WriteResult { Operation = "delete", Applied = apply, Backend = BackendName };
        try
        {
            var model = GetConnectedModel();
            var matched = new List<TSM.ModelObject>();
            foreach (var mo in EnumerateSource(model, query))
            {
                var info = MapBasic(mo);
                if (info is null || !Matches(info, query) || !MatchesUda(mo, query)) continue;
                matched.Add(mo);
                if (result.Preview.Count < 20)
                {
                    Enrich(mo, info, includeSolid: true);
                    result.Preview.Add(info);
                }
                if (limit is int n && n > 0 && matched.Count >= n) break;
            }

            result.PlannedCount = matched.Count;
            if (!apply) return result;

            foreach (var mo in matched)
            {
                try { if (mo.Delete()) result.DeletedCount++; }
                catch (Exception exItem) { result.Errors.Add(ErrorText.Flatten(exItem)); }
            }
            model.CommitChanges();
        }
        catch (Exception ex) { result.Message = ErrorText.Flatten(ex); }
        return result;
    }

    public IReadOnlyList<ComponentInfo> GetConnections(string partGuid)
    {
        var result = new List<ComponentInfo>();
        try
        {
            var model = GetConnectedModel();
            var part = TrySelectObjectByGuid(model, partGuid) as TSM.Part;
            if (part == null) return result;

            var components = part.GetComponents();
            while (components.MoveNext())
            {
                if (components.Current is TSM.BaseComponent component)
                    result.Add(MapComponent(component));
            }
        }
        catch
        {
            // "Nothing found" and per-component remoting failures degrade to an empty list.
        }
        return result;
    }

    public WriteResult CreateConnections(IReadOnlyList<ConnectionSpec> specs, bool apply)
    {
        var result = new WriteResult
        {
            Operation = "create_connections",
            Applied = apply,
            Backend = BackendName,
        };
        if (specs == null || specs.Count == 0)
        {
            result.Message = "No connection specs provided.";
            return result;
        }

        try
        {
            var model = GetConnectedModel();

            // A geometry CommitChanges before component insertion closes the common workflow
            // race where freshly-created primary/secondary parts are not selectable yet.
            if (apply) model.CommitChanges();

            foreach (var spec in specs)
            {
                result.PlannedCount++;
                try
                {
                    var primary = TrySelectObjectByGuid(model, spec.PrimaryGuid);
                    if (primary == null)
                    {
                        result.Errors.Add("Primary not found: " + spec.PrimaryGuid);
                        continue;
                    }

                    var secondaries = new ArrayList();
                    foreach (var guid in spec.SecondaryGuids ?? new List<string>())
                    {
                        var secondary = TrySelectObjectByGuid(model, guid);
                        if (secondary == null)
                            throw new InvalidOperationException("Secondary not found: " + guid);
                        secondaries.Add(secondary);
                    }
                    if (secondaries.Count == 0)
                        throw new InvalidOperationException("At least one secondary object is required.");

                    var preview = new ComponentInfo
                    {
                        Guid = "(preview)",
                        Type = "Connection",
                        Name = spec.Name ?? "",
                        Number = spec.Number,
                        PrimaryGuid = ModelGuid(primary),
                        SecondaryGuids = secondaries.Cast<TSM.ModelObject>()
                            .Select(ModelGuid).ToList(),
                        UpVector = spec.UpVector,
                        AutoDirection = NormalizeAutoDirection(spec.AutoDirection),
                    };
                    if (result.ComponentPreview.Count < 20)
                        result.ComponentPreview.Add(preview);
                    if (!apply) continue;

                    var connection = new TSM.Connection
                    {
                        Name = spec.Name ?? "",
                        Number = spec.Number < 0
                            ? TSM.BaseComponent.CUSTOM_OBJECT_NUMBER
                            : spec.Number,
                    };
                    if (!connection.SetPrimaryObject(primary))
                        throw new InvalidOperationException(
                            "Tekla rejected the connection primary object.");
                    if (!connection.SetSecondaryObjects(secondaries))
                        throw new InvalidOperationException(
                            "Tekla rejected one or more connection secondary objects.");

                    if (spec.UpVector != null)
                        connection.UpVector = new TSG.Vector(
                            spec.UpVector.X, spec.UpVector.Y, spec.UpVector.Z);

                    connection.AutoDirectionType = ParseAutoDirection(spec.AutoDirection);
                    if (!string.IsNullOrWhiteSpace(spec.AttributesFile) &&
                        !connection.LoadAttributesFromFile(spec.AttributesFile))
                        throw new InvalidOperationException(
                            "Connection attributes file could not be loaded: " + spec.AttributesFile);

                    if (!connection.Insert())
                        throw new InvalidOperationException(
                            "Tekla rejected connection insert: " + (spec.Name ?? ""));

                    connection.SetUserProperty("MCP_ORIGIN", "mcp:create_connection");
                    connection.Modify();
                    result.CreatedCount++;
                    result.CreatedIds.Add(connection.Identifier.ID);
                    var createdGuid = ModelGuid(connection);
                    if (!string.IsNullOrWhiteSpace(createdGuid))
                        result.CreatedGuids.Add(createdGuid);

                    var mapped = MapComponent(connection);
                    if (result.ComponentPreview.Count > 0 &&
                        result.ComponentPreview[result.ComponentPreview.Count - 1].Guid == "(preview)")
                        result.ComponentPreview[result.ComponentPreview.Count - 1] = mapped;
                    else if (result.ComponentPreview.Count < 20)
                        result.ComponentPreview.Add(mapped);
                }
                catch (Exception exItem)
                {
                    result.Errors.Add(ErrorText.Flatten(exItem));
                }
            }

            if (apply) model.CommitChanges();
        }
        catch (Exception ex)
        {
            result.Message = ErrorText.Flatten(ex);
        }
        return result;
    }

    // -- Script escape hatch --------------------------------------------------------------

    /// <summary>
    /// Validate → compile → EXECUTE an agent-authored script against the live model.
    /// The script connects itself (<c>var model = new Model();</c>); Tekla references come
    /// from the assemblies already loaded in this process (resolved by TeklaAssemblyResolver,
    /// so they always match the running Tekla version).
    /// </summary>
    // Verified on live Tekla 2023 (2026-07-08): read-only scripts compile and execute, Print +
    // JSON return value work, results match the dedicated tools. Mutation path and the timeout
    // abort remain unverified — see docs/tekla-api-notes.md.
    public ScriptResult ExecuteScript(
        string code,
        bool allowMutations = false,
        int timeoutSeconds = 60,
        bool compileOnly = false)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ScriptResult
        {
            Backend = BackendName,
            Stage = "policy",
            CodeSha256 = Scripting.ScriptEngine.ComputeCodeSha256(code),
        };
        try
        {
            // A compile-only check is intentionally remoting-free: it is safe before user
            // approval and can work even when no model is open. Live execution still aligns
            // the Tekla remoting channel before the script's `new Model()`.
            if (compileOnly)
                TeklaAssemblyResolver.EnsureVersionMatch();
            else
                EnsureTeklaReady();

            var policy = Scripting.ScriptPolicy.Analyze(code, allowMutations);
            result.DetectedMutatingMembers.AddRange(policy.MutatingMembers);
            if (policy.Violations.Count > 0)
            {
                result.PolicyViolations.AddRange(policy.Violations);
                result.Guidance = "Fix the policy violations and retry.";
                return result;
            }

            result.Stage = "compile";
            var script = Scripting.ScriptEngine.Create(code, BuildScriptReferences());
            result.CompilationAttempted = true;
            result.CompileErrors.AddRange(Scripting.ScriptEngine.Compile(script));
            if (result.CompileErrors.Count > 0)
            {
                result.Guidance = "Fix the compile errors and retry. Verify signatures with tekla_search_api.";
                return result;
            }
            result.Compiled = true;

            if (compileOnly)
            {
                result.Success = true;
                result.Guidance =
                    "Compile-only validation succeeded. The script was NOT executed and no Tekla model/drawing " +
                    "changes were made. Use the reported codeSha256 when presenting a mutation for approval.";
                return result;
            }

            var globals = new Scripting.ScriptGlobals();
            Scripting.ScriptEngine.Run(script, globals, timeoutSeconds, result);
            // Printed output survives even a timeout/exception — the globals object is ours.
            result.PrintedOutput.AddRange(globals.SnapshotOutput());
        }
        catch (Exception ex)
        {
            result.Error = ErrorText.Flatten(ex);
        }
        finally
        {
            result.DurationMs = watch.ElapsedMilliseconds;
        }
        return result;
    }

    /// <summary>
    /// Metadata references for script compilation. Prefer every managed Tekla.Structures*.dll
    /// available in the resolver's bin directory over a hand-maintained shortlist: this gives
    /// the escape hatch the Drawing, Dialog, Datatype, Plugins and future Open API surfaces
    /// when installed, without taking compile-time dependencies in TeklaMcp.Scripting.
    /// </summary>
    private static IReadOnlyList<Microsoft.CodeAnalysis.MetadataReference> BuildScriptReferences()
    {
        var bin = TeklaAssemblyResolver.BinDir;
        if (bin != null)
        {
            try
            {
                var paths = System.IO.Directory
                    .GetFiles(bin, "Tekla.Structures*.dll", System.IO.SearchOption.TopDirectoryOnly)
                    .Concat(System.IO.Directory
                        .GetFiles(bin, "Tekla.Dialog*.dll", System.IO.SearchOption.TopDirectoryOnly))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (paths.Length > 0)
                    return Scripting.ScriptEngine.BuildReferences(teklaDllPaths: paths);
            }
            catch
            {
                // TODO(windows): an unusual locked/protected Tekla bin should degrade to the
                // loaded-assembly fallback below, not make the escape hatch unavailable.
            }
        }

        // No resolver bin located (unusual), or it could not be enumerated: fall back to every
        // already-loaded managed Tekla API assembly with a readable Location.
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly =>
            {
                var name = assembly.GetName().Name ?? "";
                return name.StartsWith("Tekla.Structures", StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith("Tekla.Dialog", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        return Scripting.ScriptEngine.BuildReferences(
            teklaAssemblies: loaded.Length > 0
                ? loaded
                : new[]
                {
                    typeof(TSM.Model).Assembly,
                    typeof(global::Tekla.Structures.Identifier).Assembly,
                });
    }

    private static TSM.ModelObject? CreateOne(TSM.Model model, PartSpec spec)
    {
        var kind = (spec.Kind ?? "beam").Trim().ToLowerInvariant();

        if (kind == "plate")
        {
            if (spec.Contour is null || spec.Contour.Count < 3) return null;
            var plate = new TSM.ContourPlate();
            foreach (var p in spec.Contour)
                plate.AddContourPoint(new TSM.ContourPoint(ToPoint(p), null));
            if (!string.IsNullOrWhiteSpace(spec.Profile)) plate.Profile.ProfileString = spec.Profile;
            if (!string.IsNullOrWhiteSpace(spec.Material)) plate.Material.MaterialString = spec.Material;
            if (!string.IsNullOrWhiteSpace(spec.Class)) plate.Class = spec.Class;
            if (!string.IsNullOrWhiteSpace(spec.Name)) plate.Name = spec.Name;
            ApplyMatchedAndExplicitPosition(
                model, plate, spec.MatchPositionGuid, spec.Position);
            return plate.Insert() ? plate : null;
        }

        if (spec.Start is null || spec.End is null) return null;
        var beam = new TSM.Beam(ToPoint(spec.Start), ToPoint(spec.End));
        if (!string.IsNullOrWhiteSpace(spec.Profile)) beam.Profile.ProfileString = spec.Profile;
        if (!string.IsNullOrWhiteSpace(spec.Material)) beam.Material.MaterialString = spec.Material;
        if (!string.IsNullOrWhiteSpace(spec.Class)) beam.Class = spec.Class;
        if (!string.IsNullOrWhiteSpace(spec.Name)) beam.Name = spec.Name;
        ApplyMatchedAndExplicitPosition(
            model, beam, spec.MatchPositionGuid, spec.Position);
        return beam.Insert() ? beam : null;
    }

    /// <summary>Build a believable preview DTO from a spec WITHOUT touching Tekla.</summary>
    private static ModelObjectInfo PreviewInfo(PartSpec spec)
    {
        var kind = (spec.Kind ?? "beam").Trim().ToLowerInvariant();
        var info = new ModelObjectInfo
        {
            Guid = "(preview)",
            Type = kind == "plate" ? "ContourPlate" : (kind == "column" ? "Column" : "Beam"),
            Name = spec.Name ?? "",
            Class = spec.Class ?? "",
            Profile = spec.Profile ?? "",
            Material = spec.Material ?? "",
            Position = spec.Position,
        };
        if (spec.Start != null && spec.End != null)
        {
            info.StartX = spec.Start.X; info.StartY = spec.Start.Y; info.StartZ = spec.Start.Z;
            info.EndX = spec.End.X; info.EndY = spec.End.Y; info.EndZ = spec.End.Z;
            info.CenterX = (spec.Start.X + spec.End.X) / 2.0;
            info.CenterY = (spec.Start.Y + spec.End.Y) / 2.0;
            info.CenterZ = (spec.Start.Z + spec.End.Z) / 2.0;
            var dx = spec.End.X - spec.Start.X;
            var dy = spec.End.Y - spec.Start.Y;
            var dz = spec.End.Z - spec.Start.Z;
            info.LengthMm = Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1);
        }
        return info;
    }

    private static void ApplyMatchedAndExplicitPosition(
        TSM.Model model,
        TSM.Part target,
        string? matchPositionGuid,
        PartPosition? explicitPosition)
    {
        if (!string.IsNullOrWhiteSpace(matchPositionGuid))
        {
            var source = TrySelectObjectByGuid(model, matchPositionGuid!) as TSM.Part;
            if (source == null)
                throw new InvalidOperationException(
                    "Position source part not found: " + matchPositionGuid);
            CopyPosition(source.Position, target.Position);
        }

        if (explicitPosition == null) return;
        if (!string.IsNullOrWhiteSpace(explicitPosition.Plane))
            target.Position.Plane = ParseEnum<TSM.Position.PlaneEnum>(
                explicitPosition.Plane!, "plane");
        if (explicitPosition.PlaneOffset.HasValue)
            target.Position.PlaneOffset = explicitPosition.PlaneOffset.Value;
        if (!string.IsNullOrWhiteSpace(explicitPosition.Rotation))
            target.Position.Rotation = ParseEnum<TSM.Position.RotationEnum>(
                explicitPosition.Rotation!, "rotation");
        if (explicitPosition.RotationOffset.HasValue)
            target.Position.RotationOffset = explicitPosition.RotationOffset.Value;
        if (!string.IsNullOrWhiteSpace(explicitPosition.Depth))
            target.Position.Depth = ParseEnum<TSM.Position.DepthEnum>(
                explicitPosition.Depth!, "depth");
        if (explicitPosition.DepthOffset.HasValue)
            target.Position.DepthOffset = explicitPosition.DepthOffset.Value;
    }

    private static void CopyPosition(TSM.Position source, TSM.Position target)
    {
        target.Plane = source.Plane;
        target.PlaneOffset = source.PlaneOffset;
        target.Rotation = source.Rotation;
        target.RotationOffset = source.RotationOffset;
        target.Depth = source.Depth;
        target.DepthOffset = source.DepthOffset;
    }

    private static TEnum ParseEnum<TEnum>(string value, string field)
        where TEnum : struct
    {
        TEnum parsed;
        if (Enum.TryParse(value.Trim(), true, out parsed)) return parsed;
        throw new ArgumentException(
            "Invalid " + field + " value '" + value + "'. Allowed: " +
            string.Join(", ", Enum.GetNames(typeof(TEnum))));
    }

    private static TS.AutoDirectionTypeEnum ParseAutoDirection(string? value)
    {
        var normalized = NormalizeAutoDirection(value);
        return ParseEnum<TS.AutoDirectionTypeEnum>(normalized, "autoDirection");
    }

    private static string NormalizeAutoDirection(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "AUTODIR_NA" : value!.Trim();
        if (!normalized.StartsWith("AUTODIR_", StringComparison.OrdinalIgnoreCase))
            normalized = "AUTODIR_" + normalized;
        return normalized.ToUpperInvariant();
    }

    private static string ModelGuid(TSM.ModelObject? modelObject)
    {
        if (modelObject == null) return "";
        var guid = modelObject.Identifier.GUID;
        return guid == Guid.Empty ? "" : guid.ToString();
    }

    private static ComponentInfo MapComponent(TSM.BaseComponent component)
    {
        var info = new ComponentInfo
        {
            Guid = ModelGuid(component),
            Id = component.Identifier.ID,
            Type = component.GetType().Name,
            Name = component.Name ?? "",
            Number = component.Number,
        };

        if (component is TSM.Connection connection)
        {
            info.PrimaryGuid = ModelGuid(connection.GetPrimaryObject());
            foreach (var item in connection.GetSecondaryObjects())
                if (item is TSM.ModelObject secondary)
                    info.SecondaryGuids.Add(ModelGuid(secondary));
            if (connection.UpVector != null)
                info.UpVector = new Point3D(
                    connection.UpVector.X, connection.UpVector.Y, connection.UpVector.Z);
            info.AutoDirection = connection.AutoDirectionType.ToString();
            info.Status = connection.Status.ToString();
        }
        return info;
    }

    private static ReferenceGeometryInfo MapReferenceGeometry(
        TSM.ReferenceModelObject reference,
        int maxFaces,
        int maxPoints,
        string modelPath = "")
    {
        var info = new ReferenceGeometryInfo
        {
            Id = reference.Identifier.ID,
            Guid = ModelGuid(reference),
        };
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var referenceModel = reference.GetReferenceModel();
            if (referenceModel != null)
            {
                info.ReferenceModelFile = referenceModel.Filename ?? "";
                var titleProperty = referenceModel.GetType().GetProperty("Title");
                info.ReferenceModelTitle =
                    titleProperty?.GetValue(referenceModel, null) as string ??
                    System.IO.Path.GetFileNameWithoutExtension(info.ReferenceModelFile) ?? "";
            }
        }
        catch (Exception ex)
        {
            info.Message = AppendMessage(info.Message, "Reference model: " + ErrorText.Flatten(ex));
        }

        try
        {
            var enumerator = new TSMC.ReferenceModelObjectAttributeEnumerator(reference);
            while (enumerator.MoveNext())
            {
                var attribute = enumerator.Current as TSMC.ReferenceModelObjectAttribute;
                if (attribute == null) continue;
                if (string.IsNullOrWhiteSpace(info.Name)) info.Name = attribute.Name ?? "";
                if (string.IsNullOrWhiteSpace(info.Description))
                    info.Description = attribute.Description ?? "";
                if (string.IsNullOrWhiteSpace(info.ObjectType))
                    info.ObjectType = attribute.ObjectType ?? "";
                AddAttribute(attributes, "Name", attribute.Name);
                AddAttribute(attributes, "Description", attribute.Description);
                AddAttribute(attributes, "ObjectType", attribute.ObjectType);
                AddAttribute(attributes, "ProfileName", attribute.ProfileName);
            }
        }
        catch (Exception ex)
        {
            info.Message = AppendMessage(info.Message, "Parametric attributes: " + ErrorText.Flatten(ex));
        }

        try
        {
            // TODO(windows): ModelInternal.Operation is signature-verified for Tekla 2026,
            // but its runtime behavior and key names must be checked across Tekla 2021–2026.
            var customMethod = typeof(TSMI.Operation).GetMethod(
                "GetReferenceModelObjectCustomAttributes",
                new[] { typeof(int) });
            var custom = customMethod?.Invoke(null, new object[] { reference.Identifier.ID })
                as IDictionary<string, string>;
            if (custom != null)
                foreach (var pair in custom.Take(100))
                    AddAttribute(attributes, pair.Key, pair.Value);
            if (custom != null && custom.Count > 100) info.Truncated = true;
        }
        catch (Exception ex)
        {
            info.Message = AppendMessage(info.Message, "Custom attributes: " + ErrorText.Flatten(ex));
        }

        foreach (var key in new[]
                 {
                     "EXTERNAL_GUID", "ExternalGuid", "GlobalId", "GLOBALID", "IFC_GUID",
                     "IFC_GLOBAL_ID", "IfcGuid", "GUID",
                 })
            if (TryAttributeBySuffix(attributes, key, out var value))
            {
                info.ExternalGuid = value;
                break;
            }
        if (string.IsNullOrWhiteSpace(info.ExternalGuid))
            info.ExternalGuid = ReadReferenceString(reference, "EXTERNAL.GUID", "IFC_GUID", "GlobalId");

        foreach (var key in new[] { "ENTITY", "Entity", "IFC_ENTITY", "EntityType" })
            if (TryAttributeBySuffix(attributes, key, out var value))
            {
                info.Entity = value;
                break;
            }
        if (string.IsNullOrWhiteSpace(info.Entity))
            info.Entity = ReadReferenceString(reference, "ENTITY", "IFC_ENTITY", "ENTITY_TYPE");
        if (string.IsNullOrWhiteSpace(info.Entity) &&
            info.ObjectType.StartsWith("IFC", StringComparison.OrdinalIgnoreCase))
            info.Entity = info.ObjectType;

        info.OverallWidth = ReadReferenceDouble(
            reference, attributes, "OverallWidth", "OVERALL_WIDTH", "WIDTH");
        info.OverallHeight = ReadReferenceDouble(
            reference, attributes, "OverallHeight", "OVERALL_HEIGHT", "HEIGHT");

        if (maxFaces > 0)
        {
            try
            {
                // TODO(windows): verify that returned face points are in global model
                // coordinates for rotated/scaled/base-point reference models.
                var cappedMethod = typeof(TSMI.Operation).GetMethod(
                    "GetReferenceModelObjectFaces",
                    new[] { typeof(TS.Identifier), typeof(int) });
                var faces = cappedMethod != null
                    ? cappedMethod.Invoke(
                        null, new object[] { reference.Identifier, maxFaces + 1 })
                        as List<List<TSG.Point>>
                    : TSMI.Operation.GetReferenceModelObjectFaces(reference.Identifier);
                if (faces != null)
                {
                    info.Truncated |= faces.Count > maxFaces;
                    var pointsLeft = Math.Max(0, maxPoints);
                    foreach (var face in faces.Take(maxFaces))
                    {
                        if (face.Count > pointsLeft)
                        {
                            info.Truncated = true;
                            info.Message = AppendMessage(
                                info.Message, "Face-vertex response budget reached.");
                            break;
                        }
                        var mapped = new ReferenceFaceInfo();
                        foreach (var point in face)
                        {
                            var p = new Point3D(
                                Math.Round(point.X, 2),
                                Math.Round(point.Y, 2),
                                Math.Round(point.Z, 2));
                            mapped.Points.Add(p);
                            ExtendBounds(info, p);
                        }
                        info.Faces.Add(mapped);
                        pointsLeft -= mapped.Points.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                info.Message = AppendMessage(info.Message, "Faces: " + ErrorText.Flatten(ex));
            }
        }
        if (info.MinX.HasValue) info.AabbSource = "tekla-faces";

        // Field report: the internal face query fails for IFC overlay objects while the IFC
        // file itself carries correct placements. Parse the file directly — placement origin +
        // axes (and OverallWidth/Height) are enough to place фахверк members around openings.
        TryFillFromIfcFile(reference, info, modelPath);

        info.Attributes = new Dictionary<string, string>(attributes);
        return info;
    }

    /// <summary>
    /// Best-effort fallback/enrichment from the reference IFC file on disk: world placement
    /// (origin + axes in GLOBAL mm, honoring the reference model's Position/Scale/Rotation),
    /// entity/name/dimensions when Tekla did not deliver them, and an estimated AABB when no
    /// exact face geometry is available.
    /// </summary>
    private static void TryFillFromIfcFile(
        TSM.ReferenceModelObject reference,
        ReferenceGeometryInfo info,
        string modelPath)
    {
        if (string.IsNullOrWhiteSpace(info.ExternalGuid)) return;
        try
        {
            var referenceModel = reference.GetReferenceModel();
            if (referenceModel is null) return;

            var path = ResolveReferenceFile(referenceModel, modelPath);
            if (path is null)
            {
                info.Message = AppendMessage(
                    info.Message,
                    "IFC fallback: reference file not found on disk (" +
                    (referenceModel.Filename ?? "") + ").");
                return;
            }

            var placement = ReadIfcPlacementCached(path, info.ExternalGuid, out var error);
            if (placement is null)
            {
                info.Message = AppendMessage(info.Message, "IFC fallback: " + error);
                return;
            }

            // Insertion transform of the overlay: world = Position + Rz(rot) * (Scale * p).
            // TODO(windows): verify Rotation semantics and Rotation3D/base-point setups live.
            var scale = referenceModel.Scale > 0 ? referenceModel.Scale : 1.0;
            var position = referenceModel.Position;
            var rotationDegrees = ReadOptionalDouble(referenceModel, "Rotation") ?? 0.0;
            var basePoint = ReadOptionalGuid(referenceModel, "BasePointGuid");
            if (basePoint.HasValue && basePoint.Value != Guid.Empty)
                info.Message = AppendMessage(
                    info.Message,
                    "IFC fallback: reference model uses a base point; placement may be offset.");

            var origin = RotateZ(
                placement.Origin.X * scale, placement.Origin.Y * scale, placement.Origin.Z * scale,
                rotationDegrees);
            info.PlacementOrigin = new Point3D(
                Math.Round(origin.X + (position?.X ?? 0), 2),
                Math.Round(origin.Y + (position?.Y ?? 0), 2),
                Math.Round(origin.Z + (position?.Z ?? 0), 2));
            info.PlacementXAxis = RotateZ(placement.AxisX.X, placement.AxisX.Y, placement.AxisX.Z, rotationDegrees);
            info.PlacementYAxis = RotateZ(placement.AxisY.X, placement.AxisY.Y, placement.AxisY.Z, rotationDegrees);
            info.PlacementZAxis = RotateZ(placement.AxisZ.X, placement.AxisZ.Y, placement.AxisZ.Z, rotationDegrees);
            info.PlacementSource = "ifc-file";

            if (string.IsNullOrWhiteSpace(info.Entity)) info.Entity = placement.EntityType;
            if (string.IsNullOrWhiteSpace(info.Name)) info.Name = placement.Name;
            if (string.IsNullOrWhiteSpace(info.ObjectType)) info.ObjectType = placement.ObjectType;
            if (!info.OverallWidth.HasValue && placement.OverallWidth.HasValue)
                info.OverallWidth = Math.Round(placement.OverallWidth.Value * scale, 2);
            if (!info.OverallHeight.HasValue && placement.OverallHeight.HasValue)
                info.OverallHeight = Math.Round(placement.OverallHeight.Value * scale, 2);
            if (!string.IsNullOrWhiteSpace(placement.Warning))
                info.Message = AppendMessage(info.Message, "IFC fallback: " + placement.Warning);

            // No exact solid available? Estimate the AABB from the placement rectangle
            // (IfcWindow/IfcDoor convention: width along local X, height along local Z).
            // Correct position and in-wall extent, zero thickness — clearly labeled as such.
            if (!info.MinX.HasValue && info.OverallWidth.HasValue && info.OverallHeight.HasValue)
            {
                var o = info.PlacementOrigin!;
                var x = info.PlacementXAxis!;
                var z = info.PlacementZAxis!;
                var w = info.OverallWidth.Value;
                var h = info.OverallHeight.Value;
                foreach (var corner in new[]
                         {
                             o,
                             new Point3D(o.X + x.X * w, o.Y + x.Y * w, o.Z + x.Z * w),
                             new Point3D(o.X + z.X * h, o.Y + z.Y * h, o.Z + z.Z * h),
                             new Point3D(
                                 o.X + x.X * w + z.X * h,
                                 o.Y + x.Y * w + z.Y * h,
                                 o.Z + x.Z * w + z.Z * h),
                         })
                    ExtendBounds(info, corner);
                info.AabbSource = "ifc-placement-estimate";
            }
        }
        catch (Exception ex)
        {
            info.Message = AppendMessage(info.Message, "IFC fallback: " + ErrorText.Flatten(ex));
        }
    }

    /// <summary>Result cache for IFC placement lookups, keyed by path + mtime + guid.</summary>
    private static readonly object IfcCacheGate = new object();
    private static readonly Dictionary<string, (TeklaMcp.Core.Ifc.IfcEntityPlacement? Placement, string? Error)>
        IfcCache = new Dictionary<string, (TeklaMcp.Core.Ifc.IfcEntityPlacement?, string?)>();

    private static TeklaMcp.Core.Ifc.IfcEntityPlacement? ReadIfcPlacementCached(
        string path, string externalGuid, out string? error)
    {
        var key = path + "|" + System.IO.File.GetLastWriteTimeUtc(path).Ticks + "|" + externalGuid;
        lock (IfcCacheGate)
        {
            if (IfcCache.TryGetValue(key, out var cached))
            {
                error = cached.Error;
                return cached.Placement;
            }
        }
        var placement = TeklaMcp.Core.Ifc.IfcPlacementReader.TryRead(path, externalGuid, out error);
        lock (IfcCacheGate)
        {
            if (IfcCache.Count > 512) IfcCache.Clear();
            IfcCache[key] = (placement, error);
        }
        return placement;
    }

    /// <summary>Reference file path: prefer the local revision copy, else Filename (which may be relative to the model folder).</summary>
    private static string? ResolveReferenceFile(TSM.ReferenceModel referenceModel, string modelPath)
    {
        var candidates = new List<string?>
        {
            ReadOptionalString(referenceModel, "ActiveFilePath"),
            referenceModel.Filename,
        };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var raw = candidate!.Trim();
            if (System.IO.File.Exists(raw)) return raw;
            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                var combined = System.IO.Path.Combine(modelPath, raw.TrimStart('.', '\\', '/'));
                if (System.IO.File.Exists(combined)) return combined;
            }
        }
        return null;
    }

    private static Point3D RotateZ(double x, double y, double z, double degrees)
    {
        if (Math.Abs(degrees) < 1e-9) return new Point3D(x, y, z);
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Point3D(x * cos - y * sin, x * sin + y * cos, z);
    }

    /// <summary>Optional Tekla API members (absent on older versions) are read reflectively.</summary>
    private static double? ReadOptionalDouble(object source, string propertyName)
    {
        try
        {
            var value = source.GetType().GetProperty(propertyName)?.GetValue(source, null);
            return value is double d ? d : (double?)null;
        }
        catch { return null; }
    }

    private static string? ReadOptionalString(object source, string propertyName)
    {
        try
        {
            return source.GetType().GetProperty(propertyName)?.GetValue(source, null) as string;
        }
        catch { return null; }
    }

    private static Guid? ReadOptionalGuid(object source, string propertyName)
    {
        try
        {
            var value = source.GetType().GetProperty(propertyName)?.GetValue(source, null);
            return value is Guid g ? g : (Guid?)null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Finds a ReferenceModelObject by its IFC GlobalId across all reference models, via the
    /// (newer) ReferenceModel.GetReferenceModelObjectByExternalGuid API when available.
    /// </summary>
    private static TSM.ReferenceModelObject? FindByExternalGuid(
        TSM.Model model, string externalGuid, out string? message)
    {
        message = null;
        var sawLookupApi = false;
        try
        {
            var enumerator = model.GetModelObjectSelector().GetAllObjectsWithType(
                TSM.ModelObject.ModelObjectEnum.REFERENCE_MODEL);
            while (enumerator.MoveNext())
            {
                if (!(enumerator.Current is TSM.ReferenceModel referenceModel)) continue;
                var lookup = referenceModel.GetType().GetMethod(
                    "GetReferenceModelObjectByExternalGuid", new[] { typeof(string) });
                if (lookup is null) continue;
                sawLookupApi = true;
                try
                {
                    if (lookup.Invoke(referenceModel, new object[] { externalGuid })
                        is TSM.ReferenceModelObject found)
                        return found;
                }
                catch (Exception exLookup)
                {
                    message = AppendMessage(message, ErrorText.Flatten(exLookup));
                }
            }
            if (!sawLookupApi)
                message = AppendMessage(
                    message,
                    "This Tekla version has no GetReferenceModelObjectByExternalGuid API; " +
                    "address the object by integer id or selection instead.");
        }
        catch (Exception ex)
        {
            message = AppendMessage(message, ErrorText.Flatten(ex));
        }
        return null;
    }

    private static void AddAttribute(
        IDictionary<string, string> attributes,
        string? name,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) return;
        attributes[name!] = value!;
    }

    private static bool TryAttribute(
        IDictionary<string, string> attributes,
        string name,
        out string value)
    {
        if (attributes.TryGetValue(name, out value!)) return true;
        value = "";
        return false;
    }

    private static bool TryAttributeBySuffix(
        IDictionary<string, string> attributes,
        string name,
        out string value)
    {
        if (TryAttribute(attributes, name, out value)) return true;
        foreach (var pair in attributes)
        {
            if (pair.Key.EndsWith("." + name, StringComparison.OrdinalIgnoreCase) ||
                pair.Key.EndsWith(":" + name, StringComparison.OrdinalIgnoreCase) ||
                pair.Key.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        value = "";
        return false;
    }

    private static string ReadReferenceString(
        TSM.ReferenceModelObject reference,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = "";
            try
            {
                if (reference.GetReportProperty(name, ref value) &&
                    !string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch
            {
                // Try the next commonly-used reference property name.
            }
        }
        return "";
    }

    private static double? ReadReferenceDouble(
        TSM.ReferenceModelObject reference,
        IDictionary<string, string> attributes,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (TryAttributeBySuffix(attributes, name, out var raw) &&
                TryParseInvariantDouble(raw, out var parsed))
                return parsed;
            double value = 0;
            try
            {
                if (reference.GetReportProperty(name, ref value)) return value;
            }
            catch
            {
                // Try the next candidate.
            }
        }
        return null;
    }

    private static void ExtendBounds(ReferenceGeometryInfo info, Point3D point)
    {
        info.MinX = !info.MinX.HasValue ? point.X : Math.Min(info.MinX.Value, point.X);
        info.MinY = !info.MinY.HasValue ? point.Y : Math.Min(info.MinY.Value, point.Y);
        info.MinZ = !info.MinZ.HasValue ? point.Z : Math.Min(info.MinZ.Value, point.Z);
        info.MaxX = !info.MaxX.HasValue ? point.X : Math.Max(info.MaxX.Value, point.X);
        info.MaxY = !info.MaxY.HasValue ? point.Y : Math.Max(info.MaxY.Value, point.Y);
        info.MaxZ = !info.MaxZ.HasValue ? point.Z : Math.Max(info.MaxZ.Value, point.Z);
    }

    private static string AppendMessage(string? current, string next) =>
        string.IsNullOrWhiteSpace(current) ? next : current + " " + next;

    // ---------------------------------------------------------------- internals ----

    private static TSM.Model GetConnectedModel()
    {
        EnsureTeklaReady();
        var model = new TSM.Model();
        if (!model.GetConnectionStatus())
            throw new InvalidOperationException(
                "No connection to Tekla Structures. Start Tekla and open a model first. " +
                "(" + TeklaRemotingChannel.Describe() + ")");

        // First successful connection = the only moment we KNOW the channels are aligned and
        // Tekla is up — initialize the write-path proxies (ModuleManager base channel) now,
        // instead of letting the first Insert/Modify trigger them at an arbitrary later time.
        TeklaRemotingChannel.WarmUpWriteProxies();
        return model;
    }

    private static T InGlobalWorkPlane<T>(TSM.Model model, Func<T> action)
    {
        var handler = model.GetWorkPlaneHandler();
        var previous = handler.GetCurrentTransformationPlane();
        if (!handler.SetCurrentTransformationPlane(new TSM.TransformationPlane()))
            throw new InvalidOperationException("Could not switch to the global transformation plane.");
        try
        {
            return action();
        }
        finally
        {
            handler.SetCurrentTransformationPlane(previous);
        }
    }

    /// <summary>
    /// Known object-type names → Tekla enumeration values, for API-level pre-filtering.
    /// Conservative: only types whose class name maps 1:1 to an enum value. Anything else
    /// falls back to a full scan (Matches() verifies the exact type name either way).
    /// </summary>
    private static readonly Dictionary<string, TSM.ModelObject.ModelObjectEnum> TypeEnumMap =
        new Dictionary<string, TSM.ModelObject.ModelObjectEnum>(StringComparer.OrdinalIgnoreCase)
        {
            ["Beam"] = TSM.ModelObject.ModelObjectEnum.BEAM,
            ["PolyBeam"] = TSM.ModelObject.ModelObjectEnum.POLYBEAM,
            ["ContourPlate"] = TSM.ModelObject.ModelObjectEnum.CONTOURPLATE,
            ["Grid"] = TSM.ModelObject.ModelObjectEnum.GRID,
            ["ControlLine"] = TSM.ModelObject.ModelObjectEnum.CONTROL_LINE,
            ["ReferenceModelObject"] = TSM.ModelObject.ModelObjectEnum.REFERENCE_MODEL_OBJECT,
        };

    /// <summary>
    /// Yield the objects a query should operate on: the current UI selection
    /// (<see cref="ObjectQuery.UseSelection"/>), the type-filtered subset when the queried
    /// type maps to a Tekla enum (lets Tekla skip non-candidates), or every object.
    /// AutoFetch (enabled process-wide in the static constructor) batches object data during
    /// enumeration instead of one remoting round-trip per property read.
    /// </summary>
    private static IEnumerable<TSM.ModelObject> EnumerateSource(TSM.Model model, ObjectQuery query)
    {
        TSM.ModelObjectEnumerator en;
        if (query != null && query.UseSelection)
            en = new TSMUI.ModelObjectSelector().GetSelectedObjects();
        else if (query != null && !string.IsNullOrWhiteSpace(query.Type) &&
                 TypeEnumMap.TryGetValue(query.Type!.Trim(), out var objectType))
            en = model.GetModelObjectSelector().GetAllObjectsWithType(objectType);
        else
            en = model.GetModelObjectSelector().GetAllObjects();

        while (en.MoveNext())
        {
            if (en.Current != null) yield return en.Current;
        }
    }

    /// <summary>
    /// Convert a Tekla <c>ModelObject</c> into our flat DTO — full fidelity (report
    /// properties + solid bounding box). For scans over many objects, use
    /// <see cref="MapBasic"/> to filter first and <see cref="Enrich"/> only the matches:
    /// report properties and especially <c>GetSolid()</c> are per-object remoting calls
    /// that dominated whole-model scans (issue #5).
    /// </summary>
    private static ModelObjectInfo? Map(TSM.ModelObject mo)
    {
        var info = MapBasic(mo);
        if (info != null) Enrich(mo, info, includeSolid: true);
        return info;
    }

    /// <summary>
    /// Cheap part of the mapping: identity, type and the Part properties that are readable
    /// without extra remoting round-trips (with AutoFetch on). Fills everything the query
    /// filters (<see cref="Matches"/>) look at. Returns null to skip.
    /// </summary>
    private static ModelObjectInfo? MapBasic(TSM.ModelObject mo)
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
            info.Position = new PartPosition
            {
                Plane = part.Position.Plane.ToString(),
                PlaneOffset = Math.Round(part.Position.PlaneOffset, 2),
                Rotation = part.Position.Rotation.ToString(),
                RotationOffset = Math.Round(part.Position.RotationOffset, 2),
                Depth = part.Position.Depth.ToString(),
                DepthOffset = Math.Round(part.Position.DepthOffset, 2),
            };

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
        }
        else if (mo is TSM.ControlLine controlLine && controlLine.Line != null)
        {
            info.StartX = Math.Round(controlLine.Line.Point1.X, 2);
            info.StartY = Math.Round(controlLine.Line.Point1.Y, 2);
            info.StartZ = Math.Round(controlLine.Line.Point1.Z, 2);
            info.EndX = Math.Round(controlLine.Line.Point2.X, 2);
            info.EndY = Math.Round(controlLine.Line.Point2.Y, 2);
            info.EndZ = Math.Round(controlLine.Line.Point2.Z, 2);
            info.CenterX = Math.Round((controlLine.Line.Point1.X + controlLine.Line.Point2.X) / 2.0, 2);
            info.CenterY = Math.Round((controlLine.Line.Point1.Y + controlLine.Line.Point2.Y) / 2.0, 2);
            info.CenterZ = Math.Round((controlLine.Line.Point1.Z + controlLine.Line.Point2.Z) / 2.0, 2);
        }

        return info;
    }

    /// <summary>
    /// Expensive part of the mapping: report properties (ASSEMBLY_POS, WEIGHT, LENGTH) and,
    /// when <paramref name="includeSolid"/>, the solid bounding box. Each is a per-object
    /// remoting call — only enrich objects that are actually returned to the caller.
    /// </summary>
    private static void Enrich(TSM.ModelObject mo, ModelObjectInfo info, bool includeSolid)
    {
        if (mo is TSM.ReferenceModelObject reference)
        {
            var referenceInfo = MapReferenceGeometry(reference, 0, 0);
            info.ExternalGuid = referenceInfo.ExternalGuid;
            info.ExternalEntity = referenceInfo.Entity;
            info.Name = referenceInfo.Name;
            return;
        }
        if (!(mo is TSM.Part part)) return;

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

        if (!includeSolid) return;
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

    private static bool Matches(ModelObjectInfo o, ObjectQuery q)
    {
        if (q.GuidIn != null && q.GuidIn.Count > 0 &&
            !q.GuidIn.Exists(g => string.Equals(g, o.Guid, StringComparison.OrdinalIgnoreCase)))
            return false;
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
