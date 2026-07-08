using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TSM = Tekla.Structures.Model;
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

    private static bool _teklaReady;

    private static void EnsureTeklaReady()
    {
        if (_teklaReady) return;
        _teklaReady = true;
        TeklaRemotingChannel.Align();
        try { TSM.ModelObjectEnumerator.AutoFetch = true; }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[tekla] AutoFetch unavailable (continuing): " + ex.Message);
        }
    }

    static TeklaModelService()
    {
        // Intentionally empty — touching Tekla types here (Align / AutoFetch) can recurse
        // through AssemblyResolve while TeklaMcp.Tekla is still loading and overflow the stack.
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
            return new ConnectionInfo { Connected = false, Backend = BackendName, Message = ex.Message };
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
        return result;
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
            result.Message = ex.Message;
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
            result.Message = ex.Message;
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
                        var created = CreateOne(spec);
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
                    catch (Exception exItem) { result.Errors.Add(exItem.Message); }
                }
                model.CommitChanges();
            }
            finally { wph.SetCurrentTransformationPlane(previous); }
        }
        catch (Exception ex) { result.Message = ex.Message; }
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
                    catch (Exception exItem) { result.Errors.Add(exItem.Message); }
                }
                model.CommitChanges();
            }
            finally { wph.SetCurrentTransformationPlane(previous); }
        }
        catch (Exception ex) { result.Message = ex.Message; }
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
                catch (Exception exItem) { result.Errors.Add(exItem.Message); }
            }
            model.CommitChanges();
        }
        catch (Exception ex) { result.Message = ex.Message; }
        return result;
    }

    // -- Script escape hatch --------------------------------------------------------------

    /// <summary>
    /// Validate → compile → EXECUTE an agent-authored script against the live model.
    /// The script connects itself (<c>var model = new Model();</c>); Tekla references come
    /// from the assemblies already loaded in this process (resolved by TeklaAssemblyResolver,
    /// so they always match the running Tekla version).
    /// </summary>
    // TODO(windows): verify Roslyn scripting under net48 against live Tekla — assembly binding
    // redirects for System.Collections.Immutable/System.Memory are auto-generated by the server
    // exe build, but this whole path is UNTESTED on the real machine (see docs/tekla-api-notes.md).
    public ScriptResult ExecuteScript(string code, bool allowMutations = false, int timeoutSeconds = 60)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ScriptResult { Backend = BackendName, Stage = "policy" };
        try
        {
            var violations = Scripting.ScriptPolicy.Validate(code, allowMutations);
            if (violations.Count > 0)
            {
                result.PolicyViolations.AddRange(violations);
                result.Guidance = "Fix the policy violations and retry.";
                return result;
            }

            result.Stage = "compile";
            var references = Scripting.ScriptEngine.BuildReferences(teklaAssemblies: new[]
            {
                typeof(TSM.Model).Assembly,                          // Tekla.Structures.Model.dll
                typeof(global::Tekla.Structures.Identifier).Assembly, // Tekla.Structures.dll
            });
            var script = Scripting.ScriptEngine.Create(code, references);
            result.CompileErrors.AddRange(Scripting.ScriptEngine.Compile(script));
            if (result.CompileErrors.Count > 0)
            {
                result.Guidance = "Fix the compile errors and retry. Verify signatures with tekla_search_api.";
                return result;
            }

            var globals = new Scripting.ScriptGlobals();
            Scripting.ScriptEngine.Run(script, globals, timeoutSeconds, result);
            // Printed output survives even a timeout/exception — the globals object is ours.
            result.PrintedOutput.AddRange(globals.PrintedOutput);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            result.DurationMs = watch.ElapsedMilliseconds;
        }
        return result;
    }

    private static TSM.ModelObject? CreateOne(PartSpec spec)
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
            return plate.Insert() ? plate : null;
        }

        if (spec.Start is null || spec.End is null) return null;
        var beam = new TSM.Beam(ToPoint(spec.Start), ToPoint(spec.End));
        if (!string.IsNullOrWhiteSpace(spec.Profile)) beam.Profile.ProfileString = spec.Profile;
        if (!string.IsNullOrWhiteSpace(spec.Material)) beam.Material.MaterialString = spec.Material;
        if (!string.IsNullOrWhiteSpace(spec.Class)) beam.Class = spec.Class;
        if (!string.IsNullOrWhiteSpace(spec.Name)) beam.Name = spec.Name;
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

    // ---------------------------------------------------------------- internals ----

    private static TSM.Model GetConnectedModel()
    {
        EnsureTeklaReady();
        var model = new TSM.Model();
        if (!model.GetConnectionStatus())
            throw new InvalidOperationException(
                "No connection to Tekla Structures. Start Tekla and open a model first. " +
                "(" + TeklaRemotingChannel.Describe() + ")");
        return model;
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

        return info;
    }

    /// <summary>
    /// Expensive part of the mapping: report properties (ASSEMBLY_POS, WEIGHT, LENGTH) and,
    /// when <paramref name="includeSolid"/>, the solid bounding box. Each is a per-object
    /// remoting call — only enrich objects that are actually returned to the caller.
    /// </summary>
    private static void Enrich(TSM.ModelObject mo, ModelObjectInfo info, bool includeSolid)
    {
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
