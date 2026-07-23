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
    private readonly List<ComponentInfo> _components = new List<ComponentInfo>();
    private readonly List<DrawingInfo> _drawings = BuildSampleDrawings();
    private readonly Dictionary<string, List<DrawingViewInfo>> _drawingViewsByDrawing =
        new Dictionary<string, List<DrawingViewInfo>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DrawingObjectInfo>> _drawingObjectsByDrawing =
        new Dictionary<string, List<DrawingObjectInfo>>(StringComparer.OrdinalIgnoreCase);
    private List<ModelObjectInfo> _selectedObjects;
    private string? _activeDrawingKey;
    private int _nextId = 100000;

    public MockTeklaModelService()
    {
        _selectedObjects = _objects.Take(3).ToList();
        _activeDrawingKey = _drawings.FirstOrDefault()?.Key;
        foreach (var drawing in _drawings)
        {
            _drawingViewsByDrawing[drawing.Key] = new List<DrawingViewInfo>();
            _drawingObjectsByDrawing[drawing.Key] = new List<DrawingObjectInfo>();
        }
        if (!string.IsNullOrWhiteSpace(_activeDrawingKey))
        {
            _drawingViewsByDrawing[_activeDrawingKey!] = BuildSampleDrawingViews();
            _drawingObjectsByDrawing[_activeDrawingKey!] = BuildSampleDrawingObjects();
        }
        SeedUdas();
        if (_objects.Count >= 2)
        {
            _components.Add(new ComponentInfo
            {
                Guid = "cccccccc-0000-0000-0000-000000000001",
                Id = 80001,
                Type = "Connection",
                Name = "Mock end plate",
                Number = -1,
                PrimaryGuid = _objects[0].Guid,
                SecondaryGuids = new List<string> { _objects[1].Guid },
                UpVector = new Point3D(0, 0, 1),
                AutoDirection = "AUTODIR_NA",
                Status = "OK",
            });
        }
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

    public IReadOnlyList<ReferenceGeometryInfo> GetReferenceGeometry(
        IReadOnlyList<int> ids,
        bool useSelection = false,
        int maxObjects = 20,
        int maxFacesPerObject = 100,
        int maxTotalFaces = 1000,
        int maxTotalPoints = 20000)
    {
        var requested = ids ?? new List<int>();
        if (useSelection)
            requested = _selectedObjects
                .Where(o => Eq(o.Type, "ReferenceModelObject"))
                .Select(o => o.Id)
                .ToList();

        var rows = new List<ReferenceGeometryInfo>();
        var facesLeft = Math.Max(0, maxTotalFaces);
        var pointsLeft = Math.Max(0, maxTotalPoints);
        foreach (var id in requested.Distinct().Take(Math.Max(0, Math.Min(maxObjects, 100))))
        {
            var includeFace =
                maxFacesPerObject > 0 && facesLeft > 0 && pointsLeft >= 4;
            var row = new ReferenceGeometryInfo
            {
                Id = id,
                Guid = "",
                ExternalGuid = "2X_m0ckWindowGuid",
                Entity = "IFCWINDOW",
                Name = "Mock window",
                ObjectType = "Window",
                ReferenceModelTitle = "Mock architectural IFC",
                ReferenceModelFile = "mock-architecture.ifc",
                OverallWidth = 1200,
                OverallHeight = 1500,
                MinX = 5400,
                MinY = 0,
                MinZ = 900,
                MaxX = 6600,
                MaxY = 200,
                MaxZ = 2400,
                Truncated = maxFacesPerObject > 0 && !includeFace,
                Faces = includeFace
                    ? new List<ReferenceFaceInfo>
                    {
                        new ReferenceFaceInfo
                        {
                            Points = new List<Point3D>
                            {
                                new Point3D(5400, 0, 900),
                                new Point3D(6600, 0, 900),
                                new Point3D(6600, 0, 2400),
                                new Point3D(5400, 0, 2400),
                            },
                        },
                    }
                    : new List<ReferenceFaceInfo>(),
                Attributes = new Dictionary<string, string>
                {
                    ["GlobalId"] = "2X_m0ckWindowGuid",
                    ["Entity"] = "IFCWINDOW",
                    ["OverallWidth"] = "1200",
                    ["OverallHeight"] = "1500",
                },
            };
            if (includeFace)
            {
                facesLeft--;
                pointsLeft -= 4;
            }
            rows.Add(row);
        }
        return rows;
    }

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
            var guid = apply ? Guid.NewGuid().ToString() : "(preview)";
            var info = MakeInfoFromSpec(spec, guid, apply ? _nextId++ : 0);
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
            var position = ResolveMockPosition(mod.MatchPositionGuid, mod.Position, obj.Position);
            if (position != null) obj.Position = position;
            if (mod.SwapHandles) SwapEnds(obj);
            if (mod.NewStart != null) { obj.StartX = mod.NewStart.X; obj.StartY = mod.NewStart.Y; obj.StartZ = mod.NewStart.Z; }
            if (mod.NewEnd != null) { obj.EndX = mod.NewEnd.X; obj.EndY = mod.NewEnd.Y; obj.EndZ = mod.NewEnd.Z; }
            Recenter(obj);
            StampOrigin(obj.Guid, "mcp:modify");
            result.ModifiedCount++;
        }
        return result;
    }

    public IReadOnlyList<ComponentInfo> GetConnections(string partGuid) =>
        _components
            .Where(c => Eq(c.PrimaryGuid, partGuid) ||
                        c.SecondaryGuids.Any(g => Eq(g, partGuid)))
            .ToList();

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

        foreach (var spec in specs)
        {
            result.PlannedCount++;
            var primary = GetObjectByGuid(spec.PrimaryGuid);
            var secondaries = spec.SecondaryGuids
                .Select(GetObjectByGuid)
                .Where(o => o != null)
                .ToList();
            if (primary == null)
            {
                result.Errors.Add("Primary not found: " + spec.PrimaryGuid);
                continue;
            }
            if (secondaries.Count != spec.SecondaryGuids.Count)
            {
                result.Errors.Add("One or more secondary objects were not found.");
                continue;
            }
            var id = apply ? _nextId++ : 0;
            var guid = apply ? Guid.NewGuid().ToString() : "(preview)";
            var component = new ComponentInfo
            {
                Guid = guid,
                Id = id,
                Type = "Connection",
                Name = spec.Name ?? "",
                Number = spec.Number,
                PrimaryGuid = spec.PrimaryGuid,
                SecondaryGuids = spec.SecondaryGuids.ToList(),
                UpVector = spec.UpVector,
                AutoDirection = spec.AutoDirection ?? "NA",
                Status = "OK",
            };
            if (result.ComponentPreview.Count < 20) result.ComponentPreview.Add(component);
            if (!apply) continue;

            _components.Add(component);
            result.CreatedCount++;
            result.CreatedGuids.Add(guid);
            result.CreatedIds.Add(id);
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

    // ---------------------------------------------------------------- drawings ----

    public DrawingStatusInfo GetDrawingStatus()
    {
        var active = _drawings.FirstOrDefault(d =>
            string.Equals(d.Key, _activeDrawingKey, StringComparison.OrdinalIgnoreCase));
        foreach (var drawing in _drawings)
            drawing.IsActive = ReferenceEquals(drawing, active);
        return new DrawingStatusInfo
        {
            Connected = true,
            AnyDrawingOpen = active != null,
            ActiveDrawing = active,
            Backend = BackendName,
            Message = "Synthetic drawing data — no Tekla involved.",
        };
    }

    public IReadOnlyList<DrawingInfo> FindDrawings(DrawingQuery query, int? limit = null)
    {
        query = query ?? new DrawingQuery();
        IEnumerable<DrawingInfo> rows = _drawings;
        if (query.SelectedOnly) rows = rows.Take(Math.Min(2, _drawings.Count));
        if (query.KeyIn != null && query.KeyIn.Count > 0)
        {
            var keys = new HashSet<string>(query.KeyIn, StringComparer.OrdinalIgnoreCase);
            rows = rows.Where(d => keys.Contains(d.Key));
        }
        if (!string.IsNullOrWhiteSpace(query.Type))
            rows = rows.Where(d => DrawingTypeMatches(d.Type, query.Type!));
        if (!string.IsNullOrWhiteSpace(query.MarkContains))
            rows = rows.Where(d => Contains(d.Mark, query.MarkContains));
        if (!string.IsNullOrWhiteSpace(query.NameContains))
            rows = rows.Where(d => Contains(d.Name, query.NameContains));
        if (!string.IsNullOrWhiteSpace(query.TitleContains))
            rows = rows.Where(d => Contains(d.Title1 + "\n" + d.Title2 + "\n" + d.Title3, query.TitleContains));
        if (!string.IsNullOrWhiteSpace(query.AssociatedModelGuid))
            rows = rows.Where(d => Eq(d.AssociatedModelGuid, query.AssociatedModelGuid));
        if (!string.IsNullOrWhiteSpace(query.UpToDateStatusContains))
            rows = rows.Where(d => Contains(d.UpToDateStatus, query.UpToDateStatusContains));
        if (query.IsIssued.HasValue) rows = rows.Where(d => d.IsIssued == query.IsIssued.Value);
        if (query.IsLocked.HasValue) rows = rows.Where(d => d.IsLocked == query.IsLocked.Value);
        if (query.IsReadyForIssue.HasValue)
            rows = rows.Where(d => d.IsReadyForIssue == query.IsReadyForIssue.Value);

        foreach (var drawing in _drawings)
            drawing.IsActive = string.Equals(drawing.Key, _activeDrawingKey, StringComparison.OrdinalIgnoreCase);
        return Limit(rows.ToList(), limit);
    }

    public DrawingModelObjectResult GetDrawingModelObjects(
        string keyOrMark,
        int offset = 0,
        int limit = 500)
    {
        var result = new DrawingModelObjectResult
        {
            Backend = BackendName,
            Offset = Math.Max(0, offset),
        };
        var drawing = ResolveMockDrawing(keyOrMark);
        if (drawing == null)
        {
            result.Message = "Drawing not found or key/mark is ambiguous.";
            return result;
        }
        List<TeklaIdentifierInfo> identifiers;
        if (!string.IsNullOrWhiteSpace(drawing.AssociatedModelGuid))
            identifiers = new List<TeklaIdentifierInfo>
            {
                new TeklaIdentifierInfo
                {
                    Id = _objects.FirstOrDefault(o => Eq(o.Guid, drawing.AssociatedModelGuid))?.Id ?? 0,
                    Guid = drawing.AssociatedModelGuid,
                    Key = (_objects.FirstOrDefault(o =>
                        Eq(o.Guid, drawing.AssociatedModelGuid))?.Id ?? 0) + ":0",
                },
            };
        else
            identifiers = _objects.Take(8).Select(o => new TeklaIdentifierInfo
            {
                Id = o.Id,
                Guid = o.Guid,
                Key = o.Id + ":0",
            }).ToList();
        result.TotalCount = identifiers.Count;
        var cap = Math.Max(0, Math.Min(limit, 5000));
        result.Items = identifiers.Skip(result.Offset).Take(cap).ToList();
        result.ReturnedCount = result.Items.Count;
        result.Truncated = result.Offset + result.ReturnedCount < result.TotalCount;
        return result;
    }

    public DrawingSheetInfo GetDrawingSheet()
    {
        var active = ResolveMockDrawing(_activeDrawingKey ?? "");
        if (active == null)
            return new DrawingSheetInfo
            {
                Backend = BackendName,
                Message = "No active drawing.",
            };

        return new DrawingSheetInfo
        {
            Available = true,
            DrawingKey = active.Key,
            Origin = new Point3D(0, 0, 0),
            FrameOrigin = new Point3D(0, 0, 0),
            Width = 841,
            Height = 594,
            SizeDefinitionMode = "SpecifiedSize",
            AutoSizeOptions = "CalculatedAndFixedSizes",
            LayoutSheetWidth = 841,
            LayoutSheetHeight = 594,
            Backend = BackendName,
        };
    }

    public IReadOnlyList<DrawingViewInfo> GetDrawingViews() =>
        string.IsNullOrWhiteSpace(_activeDrawingKey)
            ? new List<DrawingViewInfo>()
            : GetActiveMockDrawingViews().ToList();

    public IReadOnlyList<DrawingObjectInfo> FindDrawingObjects(
        DrawingObjectQuery query,
        int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(_activeDrawingKey))
            return new List<DrawingObjectInfo>();

        query = query ?? new DrawingObjectQuery();
        var activeObjects = GetActiveMockDrawingObjects();
        IEnumerable<DrawingObjectInfo> rows = query.UseSelection
            ? activeObjects.Take(Math.Min(2, activeObjects.Count))
            : activeObjects;
        if (query.IndexIn != null && query.IndexIn.Count > 0)
        {
            var indices = new HashSet<int>(query.IndexIn);
            rows = rows.Where(o => indices.Contains(o.Index));
        }
        if (query.ObjectIds != null && query.ObjectIds.Count > 0)
            rows = rows.Where(o => query.ObjectIds.Any(id =>
                id.Id == o.ObjectId && id.Id2 == o.ObjectId2));
        if (query.TypeIn != null && query.TypeIn.Count > 0)
        {
            var types = new HashSet<string>(query.TypeIn, StringComparer.OrdinalIgnoreCase);
            rows = rows.Where(o => types.Contains(o.Type));
        }
        if (query.ViewIndex.HasValue) rows = rows.Where(o => o.ViewIndex == query.ViewIndex.Value);
        if (query.ViewId.HasValue)
            rows = rows.Where(o => o.ViewId == query.ViewId.Value &&
                                   (!query.ViewId2.HasValue || o.ViewId2 == query.ViewId2.Value));
        if (!string.IsNullOrWhiteSpace(query.ModelGuid))
            rows = rows.Where(o => Eq(o.ModelGuid, query.ModelGuid));
        if (!string.IsNullOrWhiteSpace(query.TextContains))
            rows = rows.Where(o => Contains(o.Text, query.TextContains));
        return Limit(rows.ToList(), limit);
    }

    public DrawingSelectionResult SelectDrawingObjects(DrawingObjectQuery query, int? limit = null)
    {
        var selected = FindDrawingObjects(query, limit).ToList();
        return new DrawingSelectionResult
        {
            SelectedCount = selected.Count,
            Preview = selected.Take(20).ToList(),
            Backend = BackendName,
        };
    }

    public DrawingWriteResult OpenDrawing(string keyOrMark, bool showDrawing, bool apply)
    {
        var drawing = ResolveMockDrawing(keyOrMark);
        var result = NewDrawingResult("open_drawing", apply);
        if (drawing == null)
        {
            result.Message = "Drawing not found or exact mark is ambiguous.";
            return result;
        }
        result.PlannedCount = 1;
        result.DrawingPreview.Add(drawing);
        if (!apply) return result;
        var active = ResolveMockDrawing(_activeDrawingKey ?? "");
        if (active != null && !Eq(active.Key, drawing.Key))
        {
            result.Message =
                "Another drawing is active. Close it explicitly before opening a different drawing.";
            return result;
        }
        if (active != null)
        {
            result.Message = "The requested drawing is already active.";
            return result;
        }
        _activeDrawingKey = drawing.Key;
        result.ModifiedCount = 1;
        return result;
    }

    public DrawingWriteResult CloseActiveDrawing(bool save, bool apply)
    {
        var result = NewDrawingResult("close_drawing", apply);
        var active = ResolveMockDrawing(_activeDrawingKey ?? "");
        if (active == null)
        {
            result.Message = "No active drawing.";
            return result;
        }
        result.PlannedCount = 1;
        result.DrawingPreview.Add(active);
        if (apply)
        {
            _activeDrawingKey = null;
            result.ModifiedCount = 1;
        }
        return result;
    }

    public DrawingWriteResult SaveActiveDrawing(bool apply)
    {
        var result = NewDrawingResult("save_drawing", apply);
        var active = ResolveMockDrawing(_activeDrawingKey ?? "");
        if (active == null)
        {
            result.Message = "No active drawing.";
            return result;
        }
        result.PlannedCount = 1;
        result.DrawingPreview.Add(active);
        if (apply)
        {
            active.ModificationDate = DateTime.UtcNow;
            result.ModifiedCount = 1;
        }
        return result;
    }

    public DrawingWriteResult CreateDrawings(IReadOnlyList<DrawingSpec> specs, bool apply)
    {
        var result = NewDrawingResult("create_drawings", apply);
        var safe = (specs ?? new List<DrawingSpec>()).Take(200).ToList();
        result.PlannedCount = safe.Count;
        var firstDrawingId = _drawings.Select(d => d.DrawingId).DefaultIfEmpty(10000).Max() + 1;
        for (var i = 0; i < safe.Count; i++)
        {
            var spec = safe[i];
            var index = _drawings.Count + i + 1;
            var drawingId = firstDrawingId + i;
            var type = NormalizeDrawingType(spec.Type);
            var drawing = new DrawingInfo
            {
                Key = "drawing:" + drawingId + ":0",
                DrawingId = drawingId,
                Type = type,
                Mark = type == "GeneralArrangementDrawing" ? "G-" + index : "A-" + index,
                Name = string.IsNullOrWhiteSpace(spec.Name) ? "MCP drawing " + index : spec.Name,
                AssociatedModelGuid = spec.ModelGuid ?? "",
                SheetNumber = spec.SheetNumber,
                UpToDateStatus = "DrawingIsUpToDate",
                CreationDate = DateTime.UtcNow,
                ModificationDate = DateTime.UtcNow,
            };
            result.DrawingPreview.Add(drawing);
        }
        if (!apply) return result;
        if (!string.IsNullOrWhiteSpace(_activeDrawingKey))
        {
            result.Message =
                "Drawing creation requires the drawing editor to be closed. " +
                "Close the active drawing explicitly.";
            return result;
        }
        foreach (var drawing in result.DrawingPreview)
        {
            _drawings.Add(drawing);
            _drawingViewsByDrawing[drawing.Key] = new List<DrawingViewInfo>();
            _drawingObjectsByDrawing[drawing.Key] = new List<DrawingObjectInfo>();
            result.CreatedCount++;
        }
        return result;
    }

    public DrawingWriteResult CreateDrawingsFromRule(
        string ruleFile,
        IReadOnlyList<string> modelGuids,
        bool apply)
    {
        var result = NewDrawingResult("create_drawings_from_rule", apply);
        var guids = (modelGuids ?? new List<string>()).Take(50).ToList();
        result.PlannedCount = guids.Count;
        foreach (var guid in guids)
            result.DrawingPreview.Add(new DrawingInfo
            {
                Key = "(preview)",
                Type = "AutoDrawingRule",
                Name = ruleFile ?? "",
                AssociatedModelGuid = guid,
            });
        if (!apply) return result;
        if (!string.IsNullOrWhiteSpace(_activeDrawingKey))
        {
            result.Message =
                "AutoDrawing creation requires the drawing editor to be closed.";
            return result;
        }
        if (string.IsNullOrWhiteSpace(ruleFile))
        {
            result.Message = "ruleFile is required.";
            return result;
        }
        var created = CreateDrawings(guids.Select((guid, i) => new DrawingSpec
        {
            Type = "assembly",
            ModelGuid = guid,
            Name = "Rule " + ruleFile + " " + (i + 1),
        }).ToList(), true);
        result.CreatedCount = created.CreatedCount;
        result.DrawingPreview = created.DrawingPreview;
        result.Errors.AddRange(created.Errors);
        result.Warnings.AddRange(created.Warnings);
        result.Message = created.Message;
        return result;
    }

    public DrawingWriteResult ModifyDrawings(
        DrawingQuery query,
        DrawingModification modification,
        bool apply,
        int? limit = null)
    {
        var result = NewDrawingResult("modify_drawings", apply);
        modification = modification ?? new DrawingModification();
        if (modification.Name == null &&
            modification.Title1 == null &&
            modification.Title2 == null &&
            modification.Title3 == null &&
            !modification.IsFrozen.HasValue &&
            !modification.IsLocked.HasValue &&
            !modification.IsMasterDrawing.HasValue &&
            !modification.IsReadyForIssue.HasValue)
        {
            result.Message = "No drawing fields were supplied to modify.";
            return result;
        }
        var targets = FindDrawings(query, limit ?? 200).ToList();
        result.PlannedCount = targets.Count;
        result.DrawingPreview.AddRange(targets.Take(20));
        if (!apply) return result;
        foreach (var d in targets)
        {
            if (modification.Name != null) d.Name = modification.Name;
            if (modification.Title1 != null) d.Title1 = modification.Title1;
            if (modification.Title2 != null) d.Title2 = modification.Title2;
            if (modification.Title3 != null) d.Title3 = modification.Title3;
            if (modification.IsFrozen.HasValue) d.IsFrozen = modification.IsFrozen.Value;
            if (modification.IsLocked.HasValue) d.IsLocked = modification.IsLocked.Value;
            if (modification.IsMasterDrawing.HasValue) d.IsMasterDrawing = modification.IsMasterDrawing.Value;
            if (modification.IsReadyForIssue.HasValue) d.IsReadyForIssue = modification.IsReadyForIssue.Value;
            d.ModificationDate = DateTime.UtcNow;
            result.ModifiedCount++;
        }
        return result;
    }

    public DrawingWriteResult OperateDrawings(
        DrawingQuery query,
        string operation,
        DrawingPrintOptions? printOptions,
        bool apply,
        int? limit = null)
    {
        var op = (operation ?? "").Trim().ToLowerInvariant();
        var result = NewDrawingResult(op + "_drawings", apply);
        var targets = FindDrawings(query, limit ?? 200).ToList();
        result.PlannedCount = targets.Count;
        result.DrawingPreview.AddRange(targets.Take(20));
        if (op == "print")
        {
            var options = printOptions ?? new DrawingPrintOptions();
            if (string.Equals(options.OutputType, "PDF", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(options.OutputFile))
            {
                result.Message =
                    "PDF export requires an explicit absolute outputFile. Use {mark}/{name} " +
                    "in a batch template.";
                return result;
            }
            foreach (var drawing in targets)
            {
                var path = ResolveMockPrintOutput(drawing, options);
                if (!string.IsNullOrWhiteSpace(path)) result.OutputFiles.Add(path);
            }
            if (result.OutputFiles.Any(path => !System.IO.Path.IsPathRooted(path)))
            {
                result.Message = "Every print output path must be absolute.";
                return result;
            }
            if (result.OutputFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                result.OutputFiles.Count)
            {
                result.Message =
                    "The output template resolves multiple drawings to the same file.";
                return result;
            }
            if (!options.Overwrite)
            {
                var existing = result.OutputFiles.Where(System.IO.File.Exists).ToList();
                if (existing.Count > 0)
                {
                    result.Message =
                        "Export was not started because output file(s) already exist and overwrite=false.";
                    result.Errors.AddRange(existing);
                    return result;
                }
            }
        }
        if (!apply) return result;
        if (op == "print" && !string.IsNullOrWhiteSpace(_activeDrawingKey))
        {
            result.Message =
                "Printing requires the drawing editor to be closed. Save and close it explicitly first.";
            return result;
        }

        foreach (var drawing in targets.ToList())
        {
            switch (op)
            {
                case "delete":
                    if (Eq(drawing.Key, _activeDrawingKey))
                    {
                        result.Errors.Add("Cannot delete active drawing " + drawing.Key + ".");
                        continue;
                    }
                    _drawings.Remove(drawing);
                    _drawingViewsByDrawing.Remove(drawing.Key);
                    _drawingObjectsByDrawing.Remove(drawing.Key);
                    result.DeletedCount++;
                    break;
                case "issue":
                    drawing.IsIssued = true;
                    drawing.IsIssuedButModified = false;
                    drawing.IssuingDate = DateTime.UtcNow;
                    result.ModifiedCount++;
                    break;
                case "unissue":
                    drawing.IsIssued = false;
                    result.ModifiedCount++;
                    break;
                case "update":
                    if (Eq(drawing.Key, _activeDrawingKey))
                    {
                        result.Errors.Add("Cannot update active drawing " + drawing.Key + ".");
                        continue;
                    }
                    drawing.UpToDateStatus = "DrawingIsUpToDate";
                    drawing.ModificationDate = DateTime.UtcNow;
                    result.ModifiedCount++;
                    break;
                case "place_views":
                    if (!Eq(drawing.Key, _activeDrawingKey))
                    {
                        result.Errors.Add("PlaceViews requires drawing " + drawing.Key + " to be active.");
                        continue;
                    }
                    result.ModifiedCount++;
                    break;
                case "print":
                    var options = printOptions ?? new DrawingPrintOptions();
                    var output = ResolveMockPrintOutput(drawing, options);
                    if (!options.Overwrite && !string.IsNullOrWhiteSpace(output) &&
                        System.IO.File.Exists(output))
                    {
                        result.Errors.Add(
                            "Output file already exists and overwrite=false: " + output);
                        continue;
                    }
                    drawing.OutputDate = DateTime.UtcNow;
                    result.ModifiedCount++;
                    break;
                default:
                    result.Errors.Add("Unknown drawing operation: " + op + ".");
                    break;
            }
        }
        return result;
    }

    public DrawingWriteResult CreateDrawingViews(IReadOnlyList<DrawingViewSpec> specs, bool apply)
    {
        var result = NewDrawingResult("create_drawing_views", apply);
        if (string.IsNullOrWhiteSpace(_activeDrawingKey))
        {
            result.Message = "No active drawing.";
            return result;
        }
        var safe = (specs ?? new List<DrawingViewSpec>()).Take(50).ToList();
        result.PlannedCount = safe.Count;
        var views = GetActiveMockDrawingViews();
        var firstViewId = NextMockViewId();
        var baseViewCount = views.Count;
        var validSequence = 0;
        var activeDrawing = ResolveMockDrawing(_activeDrawingKey!);
        for (var i = 0; i < safe.Count; i++)
        {
            var spec = safe[i];
            var kind = (spec.Type ?? "").Trim().Replace("-", "_").ToLowerInvariant();
            var activeIsGa = activeDrawing != null &&
                Eq(activeDrawing.Type, "GeneralArrangementDrawing");
            if (kind == "ga_model")
            {
                if (!activeIsGa)
                {
                    result.Errors.Add(
                        "ga_model views require an active general-arrangement drawing.");
                    continue;
                }
                if (spec.ViewCoordinateSystem == null ||
                    spec.DisplayCoordinateSystem == null ||
                    spec.RestrictionMin == null ||
                    spec.RestrictionMax == null)
                {
                    result.Errors.Add(
                        "ga_model requires view/display coordinate systems and restriction bounds.");
                    continue;
                }
            }
            else if (activeIsGa &&
                     (kind == "front" || kind == "top" || kind == "back" ||
                      kind == "bottom" || kind == "3d" || kind == "_3d"))
            {
                result.Errors.Add(
                    "Front/top/back/bottom/3D helpers do not support GA drawings; use ga_model.");
                continue;
            }
            var view = new DrawingViewInfo
            {
                Index = baseViewCount + validSequence,
                ViewId = firstViewId + validSequence,
                Type = NormalizeViewType(spec.Type),
                Name = string.IsNullOrWhiteSpace(spec.Name) ? NormalizeViewType(spec.Type) : spec.Name,
                Origin = spec.InsertionPoint,
                FrameOrigin = new Point3D(),
                Width = 180,
                Height = 120,
                Scale = spec.Scale ?? 10,
                ModelObjectCount = 12,
            };
            validSequence++;
            result.ViewPreview.Add(view);
            if (!apply) continue;
            views.Add(view);
            result.CreatedCount++;
        }
        return result;
    }

    public DrawingWriteResult ModifyDrawingViews(
        IReadOnlyList<DrawingViewModification> modifications,
        bool apply)
    {
        var result = NewDrawingResult("modify_drawing_views", apply);
        var views = GetActiveMockDrawingViews();
        var objects = GetActiveMockDrawingObjects();
        foreach (var mod in (modifications ?? new List<DrawingViewModification>()).Take(50))
        {
            var view = views.FirstOrDefault(v => v.Index == mod.ViewIndex);
            if (mod.ViewId.HasValue)
                view = mod.ViewId.Value == 0 &&
                       (!mod.ViewId2.HasValue || mod.ViewId2.Value == 0)
                    ? null
                    : views.FirstOrDefault(v =>
                    v.ViewId == mod.ViewId.Value &&
                    (!mod.ViewId2.HasValue || v.ViewId2 == mod.ViewId2.Value));
            if (view == null)
            {
                result.Errors.Add("View index not found: " + mod.ViewIndex + ".");
                continue;
            }
            result.PlannedCount++;
            result.ViewPreview.Add(view);
            if (!apply) continue;
            if (mod.Delete)
            {
                views.Remove(view);
                objects.RemoveAll(obj =>
                    obj.ViewId == view.ViewId && obj.ViewId2 == view.ViewId2);
                ReindexMockDrawingViews();
                ReindexMockDrawingObjects();
                result.DeletedCount++;
                continue;
            }
            if (mod.Name != null) view.Name = mod.Name;
            if (mod.Origin != null) view.Origin = mod.Origin;
            if (mod.Width.HasValue) view.Width = mod.Width.Value;
            if (mod.Height.HasValue) view.Height = mod.Height.Value;
            if (mod.Scale.HasValue) view.Scale = mod.Scale.Value;
            result.ModifiedCount++;
        }
        return result;
    }

    public DrawingWriteResult CreateDrawingObjects(
        IReadOnlyList<DrawingObjectSpec> specs,
        bool apply)
    {
        var result = NewDrawingResult("create_drawing_objects", apply);
        if (string.IsNullOrWhiteSpace(_activeDrawingKey))
        {
            result.Message = "No active drawing.";
            return result;
        }
        var safe = (specs ?? new List<DrawingObjectSpec>()).Take(200).ToList();
        result.PlannedCount = safe.Count;
        var views = GetActiveMockDrawingViews();
        var objects = GetActiveMockDrawingObjects();
        var firstObjectId = NextMockDrawingObjectId();
        var baseObjectCount = objects.Count;
        var validSequence = 0;
        foreach (var spec in safe)
        {
            var isSheet = spec.ViewIndex == -1 && !spec.ViewId.HasValue;
            DrawingViewInfo? view = null;
            if (!isSheet)
                view = spec.ViewId.HasValue
                    ? views.FirstOrDefault(v =>
                        v.ViewId == spec.ViewId.Value &&
                        (!spec.ViewId2.HasValue || v.ViewId2 == spec.ViewId2.Value))
                    : views.FirstOrDefault(v => v.Index == spec.ViewIndex);
            if (!isSheet && view == null)
            {
                result.Errors.Add("View index not found: " + spec.ViewIndex + ".");
                continue;
            }
            if (isSheet && !Eq(spec.CoordinateSpace, "sheet"))
            {
                result.Errors.Add(
                    "The sheet target requires coordinateSpace=sheet (paper millimetres).");
                continue;
            }
            if (!isSheet && Eq(spec.CoordinateSpace, "sheet"))
            {
                result.Errors.Add("sheet coordinates require viewIndex=-1 (the drawing sheet).");
                continue;
            }
            var info = new DrawingObjectInfo
            {
                Index = baseObjectCount + validSequence,
                ObjectId = firstObjectId + validSequence,
                ViewIndex = isSheet ? -1 : view!.Index,
                ViewId = isSheet ? 0 : view!.ViewId,
                ViewId2 = isSheet ? 0 : view!.ViewId2,
                ViewName = isSheet ? "(sheet)" : view!.Name,
                CoordinateSpace = isSheet ? "sheet" : "view",
                Type = NormalizeDrawingObjectType(spec.Kind),
                ModelGuid = spec.ModelGuid ?? "",
                Text = spec.Text ?? "",
                Points = (spec.Points ?? new List<Point3D>()).ToList(),
                Radius = spec.Radius,
                Bulge = spec.Bulge,
                Udas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MCP_ORIGIN"] = "mcp:create_drawing_object",
                },
            };
            validSequence++;
            result.ObjectPreview.Add(info);
            if (!apply) continue;
            objects.Add(info);
            result.CreatedCount++;
        }
        return result;
    }

    public DrawingWriteResult ModifyDrawingObjects(
        DrawingObjectQuery query,
        DrawingObjectModification modification,
        bool apply,
        int? limit = null)
    {
        modification = modification ?? new DrawingObjectModification();
        var result = NewDrawingResult(
            modification.Delete ? "delete_drawing_objects" : "modify_drawing_objects", apply);
        if (!modification.Delete &&
            modification.Text == null &&
            modification.MoveBy == null &&
            string.IsNullOrWhiteSpace(modification.Visibility) &&
            string.IsNullOrWhiteSpace(modification.AttributeFile))
        {
            result.Message = "No drawing-object changes were supplied.";
            return result;
        }
        var objects = GetActiveMockDrawingObjects();
        var targets = FindDrawingObjects(query, limit ?? 200).ToList();
        result.PlannedCount = targets.Count;
        result.ObjectPreview.AddRange(targets.Take(20));
        if (!apply) return result;

        foreach (var obj in targets.ToList())
        {
            if (modification.Delete)
            {
                objects.Remove(obj);
                result.DeletedCount++;
                continue;
            }
            if (modification.Text != null && obj.Type == "Text") obj.Text = modification.Text;
            if (modification.MoveBy != null)
            {
                foreach (var p in obj.Points)
                {
                    p.X += modification.MoveBy.X;
                    p.Y += modification.MoveBy.Y;
                    p.Z += modification.MoveBy.Z;
                }
            }
            if (!string.IsNullOrWhiteSpace(modification.Visibility))
                obj.IsHidden = modification.Visibility == "drawing" || modification.Visibility == "view";
            obj.Udas["MCP_ORIGIN"] = "mcp:modify_drawing_object";
            result.ModifiedCount++;
        }
        ReindexMockDrawingObjects();
        return result;
    }

    public DrawingWriteResult OperateDrawingMarks(
        DrawingObjectQuery query,
        string operation,
        bool apply,
        int? limit = null)
    {
        var result = NewDrawingResult((operation ?? "") + "_drawing_marks", apply);
        var targets = FindDrawingObjects(query, limit ?? 200)
            .Where(o => o.Type == "Mark" || o.Type == "MarkBase" || o.Type == "MarkSet")
            .ToList();
        result.PlannedCount = targets.Count;
        result.ObjectPreview.AddRange(targets.Take(20));
        if (apply) result.ModifiedCount = targets.Count;
        return result;
    }

    // ---------------------------------------------------------------- scripts ----

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

        var policy = Scripting.ScriptPolicy.Analyze(code, allowMutations);
        result.DetectedMutatingMembers.AddRange(policy.MutatingMembers);
        if (policy.Violations.Count > 0)
        {
            result.PolicyViolations.AddRange(policy.Violations);
            result.Guidance = "Fix the policy violations and retry.";
            result.DurationMs = watch.ElapsedMilliseconds;
            return result;
        }

        // Compile-only validation: possible on any OS when the Tekla DLLs are available
        // (e.g. extracted from the NuGet packages — see tools/TeklaApiDoc/README.md).
        var compilationSkipped = true;
        var dllDir = Environment.GetEnvironmentVariable("TEKLA_MCP_SCRIPT_REF_DIR");
        if (!string.IsNullOrWhiteSpace(dllDir) && System.IO.Directory.Exists(dllDir))
        {
            compilationSkipped = false;
            result.Stage = "compile";
            result.CompilationAttempted = true;
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
            result.Compiled = true;
        }
        else
        {
            result.Guidance = "Compilation was SKIPPED (no Tekla DLLs on this machine — set " +
                              "TEKLA_MCP_SCRIPT_REF_DIR to a folder with Tekla.Structures*.dll to enable it). ";
        }

        result.Success = !compileOnly || result.Compiled;
        result.Guidance = (result.Guidance ?? "") +
                          (compileOnly
                              ? compilationSkipped
                                  ? "Compile-only policy check completed, but the source was NOT compiled. "
                                  : "Compile-only validation succeeded; the source was NOT executed. "
                              : "Mock backend: the script was validated but NOT executed — execution requires " +
                                "the real Tekla backend on Windows. Do not fabricate results from this run.");
        result.DurationMs = watch.ElapsedMilliseconds;
        return result;
    }

    private DrawingInfo? ResolveMockDrawing(string keyOrMark)
    {
        if (string.IsNullOrWhiteSpace(keyOrMark)) return null;
        var byKey = _drawings.Where(d => Eq(d.Key, keyOrMark)).Take(2).ToList();
        if (byKey.Count > 0) return byKey.Count == 1 ? byKey[0] : null;
        var byMark = _drawings.Where(d => Eq(d.Mark, keyOrMark)).Take(2).ToList();
        return byMark.Count == 1 ? byMark[0] : null;
    }

    private List<DrawingViewInfo> GetActiveMockDrawingViews()
    {
        if (string.IsNullOrWhiteSpace(_activeDrawingKey))
            return new List<DrawingViewInfo>();
        if (!_drawingViewsByDrawing.TryGetValue(_activeDrawingKey!, out var views))
        {
            views = new List<DrawingViewInfo>();
            _drawingViewsByDrawing[_activeDrawingKey!] = views;
        }
        return views;
    }

    private List<DrawingObjectInfo> GetActiveMockDrawingObjects()
    {
        if (string.IsNullOrWhiteSpace(_activeDrawingKey))
            return new List<DrawingObjectInfo>();
        if (!_drawingObjectsByDrawing.TryGetValue(_activeDrawingKey!, out var objects))
        {
            objects = new List<DrawingObjectInfo>();
            _drawingObjectsByDrawing[_activeDrawingKey!] = objects;
        }
        return objects;
    }

    private int NextMockViewId() =>
        _drawingViewsByDrawing.Values.SelectMany(rows => rows)
            .Select(view => view.ViewId).DefaultIfEmpty(11000).Max() + 1;

    private int NextMockDrawingObjectId() =>
        _drawingObjectsByDrawing.Values.SelectMany(rows => rows)
            .Select(obj => obj.ObjectId).DefaultIfEmpty(12000).Max() + 1;

    private static string ResolveMockPrintOutput(
        DrawingInfo drawing,
        DrawingPrintOptions options)
    {
        var template = options.OutputFile ?? "";
        if (string.IsNullOrWhiteSpace(template)) return drawing.PlotFileName ?? "";
        return template
            .Replace("{mark}", SafeMockFileToken(drawing.Mark))
            .Replace("{name}", SafeMockFileToken(drawing.Name));
    }

    private static string SafeMockFileToken(string? value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string((value ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static DrawingWriteResult NewDrawingResult(string operation, bool apply) =>
        new DrawingWriteResult { Operation = operation, Applied = apply, Backend = BackendName };

    private static bool DrawingTypeMatches(string actual, string requested) =>
        Eq(actual, NormalizeDrawingType(requested));

    private static string NormalizeDrawingType(string? raw)
    {
        switch ((raw ?? "").Trim().Replace("-", "_").ToLowerInvariant())
        {
            case "assembly":
            case "assemblydrawing": return "AssemblyDrawing";
            case "single":
            case "single_part":
            case "singlepartdrawing": return "SinglePartDrawing";
            case "cast":
            case "cast_unit":
            case "castunitdrawing": return "CastUnitDrawing";
            case "ga":
            case "general_arrangement":
            case "gadrawing":
            case "generalarrangementdrawing": return "GeneralArrangementDrawing";
            default: return raw ?? "";
        }
    }

    private static string NormalizeViewType(string? raw)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "front": return "FrontView";
            case "top": return "TopView";
            case "back": return "BackView";
            case "bottom": return "BottomView";
            case "3d":
            case "_3d": return "3DView";
            case "ga_model": return "GeneralArrangementView";
            case "section": return "SectionView";
            case "curved_section": return "CurvedSectionView";
            case "detail": return "DetailView";
            default: return raw ?? "";
        }
    }

    private static string NormalizeDrawingObjectType(string? raw)
    {
        var value = (raw ?? "").Trim().Replace("-", "_").ToLowerInvariant();
        switch (value)
        {
            case "straight_dimension":
            case "dimension": return "StraightDimensionSet";
            case "angle_dimension": return "AngleDimension";
            case "radius_dimension": return "RadiusDimension";
            case "curved_radial": return "CurvedDimensionSetRadial";
            case "curved_orthogonal": return "CurvedDimensionSetOrthogonal";
            case "level_mark": return "LevelMark";
        }
        if (value.Length == 0) return "";
        return char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant();
    }

    private void ReindexMockDrawingObjects()
    {
        var objects = GetActiveMockDrawingObjects();
        for (var i = 0; i < objects.Count; i++) objects[i].Index = i;
    }

    private void ReindexMockDrawingViews()
    {
        var views = GetActiveMockDrawingViews();
        var objects = GetActiveMockDrawingObjects();
        for (var i = 0; i < views.Count; i++)
        {
            views[i].Index = i;
            foreach (var obj in objects.Where(item =>
                         item.ViewId == views[i].ViewId && item.ViewId2 == views[i].ViewId2))
                obj.ViewIndex = i;
        }
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

    private ModelObjectInfo MakeInfoFromSpec(PartSpec spec, string guid, int id)
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
            Position = ResolveMockPosition(spec.MatchPositionGuid, spec.Position),
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

    private PartPosition? ResolveMockPosition(
        string? matchGuid,
        PartPosition? explicitPosition,
        PartPosition? current = null)
    {
        PartPosition? basis = current;
        if (!string.IsNullOrWhiteSpace(matchGuid))
            basis = GetObjectByGuid(matchGuid!)?.Position;

        if (basis == null && explicitPosition == null) return null;
        return MergePosition(basis, explicitPosition);
    }

    private static PartPosition MergePosition(PartPosition? basis, PartPosition? overlay) =>
        new PartPosition
        {
            Plane = overlay?.Plane ?? basis?.Plane,
            PlaneOffset = overlay?.PlaneOffset ?? basis?.PlaneOffset,
            Rotation = overlay?.Rotation ?? basis?.Rotation,
            RotationOffset = overlay?.RotationOffset ?? basis?.RotationOffset,
            Depth = overlay?.Depth ?? basis?.Depth,
            DepthOffset = overlay?.DepthOffset ?? basis?.DepthOffset,
        };

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

    private static IReadOnlyList<T> Limit<T>(List<T> src, int? limit)
    {
        if (limit is int n && n <= 0) return new List<T>();
        return limit is int cap && cap < src.Count ? src.GetRange(0, cap) : src;
    }

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

    private static List<DrawingInfo> BuildSampleDrawings() =>
        new List<DrawingInfo>
        {
            new DrawingInfo
            {
                Key = "AssemblyDrawing|00000000-0000-0000-0000-000000001000|1|A-1",
                DrawingId = 10001,
                Type = "AssemblyDrawing",
                Mark = "A-1",
                Name = "COLUMN ASSEMBLY",
                Title1 = "COLUMN C1",
                AssociatedModelGuid = "00000000-0000-0000-0000-000000001000",
                SheetNumber = 1,
                IsReadyForIssue = true,
                UpToDateStatus = "DrawingIsUpToDate",
                CreationDate = new DateTime(2026, 1, 10),
                ModificationDate = new DateTime(2026, 7, 20),
                PlotFileName = "A-1.pdf",
            },
            new DrawingInfo
            {
                Key = "GeneralArrangementDrawing|||G-101",
                DrawingId = 10002,
                Type = "GeneralArrangementDrawing",
                Mark = "G-101",
                Name = "PLAN +4000",
                Title1 = "STEEL FRAMING PLAN",
                IsIssued = true,
                IsIssuedButModified = true,
                UpToDateStatus = "PartsWereModified",
                CreationDate = new DateTime(2026, 2, 1),
                ModificationDate = new DateTime(2026, 7, 22),
                IssuingDate = new DateTime(2026, 7, 15),
                PlotFileName = "G-101.pdf",
            },
            new DrawingInfo
            {
                Key = "SinglePartDrawing|00000000-0000-0000-0000-000000001004|1|B-1",
                DrawingId = 10003,
                Type = "SinglePartDrawing",
                Mark = "B-1",
                Name = "BEAM",
                AssociatedModelGuid = "00000000-0000-0000-0000-000000001004",
                SheetNumber = 1,
                IsLocked = true,
                IsLockedBy = "mock-user",
                UpToDateStatus = "DrawingIsUpToDateButMayNeedChecking",
                CreationDate = new DateTime(2026, 3, 5),
                ModificationDate = new DateTime(2026, 7, 18),
                PlotFileName = "B-1.pdf",
            },
        };

    private static List<DrawingViewInfo> BuildSampleDrawingViews() =>
        new List<DrawingViewInfo>
        {
            new DrawingViewInfo
            {
                Index = 0,
                ViewId = 11001,
                Type = "FrontView",
                Name = "FRONT",
                Origin = new Point3D(40, 80, 0),
                FrameOrigin = new Point3D(0, 0, 0),
                Width = 180,
                Height = 120,
                Scale = 10,
                RestrictionMin = new Point3D(-1000, -500, -500),
                RestrictionMax = new Point3D(1000, 4500, 500),
                ModelObjectCount = 8,
            },
            new DrawingViewInfo
            {
                Index = 1,
                ViewId = 11002,
                Type = "TopView",
                Name = "TOP",
                Origin = new Point3D(240, 80, 0),
                FrameOrigin = new Point3D(0, 0, 0),
                Width = 160,
                Height = 120,
                Scale = 10,
                RestrictionMin = new Point3D(-1000, -1000, -100),
                RestrictionMax = new Point3D(7000, 7000, 4100),
                ModelObjectCount = 18,
            },
        };

    private static List<DrawingObjectInfo> BuildSampleDrawingObjects() =>
        new List<DrawingObjectInfo>
        {
            new DrawingObjectInfo
            {
                Index = 0,
                ObjectId = 12001,
                ViewIndex = 0,
                ViewId = 11001,
                ViewName = "FRONT",
                Type = "Part",
                ModelGuid = "00000000-0000-0000-0000-000000001000",
                ModelId = 1000,
                IsHidden = false,
            },
            new DrawingObjectInfo
            {
                Index = 1,
                ObjectId = 12002,
                ViewIndex = 0,
                ViewId = 11001,
                ViewName = "FRONT",
                Type = "Text",
                Text = "TYP.",
                Points = new List<Point3D> { new Point3D(120, 60, 0) },
                BoundingMin = new Point3D(120, 60, 0),
                BoundingMax = new Point3D(145, 66, 0),
                IsHidden = false,
            },
            new DrawingObjectInfo
            {
                Index = 2,
                ObjectId = 12003,
                ViewIndex = 1,
                ViewId = 11002,
                ViewName = "TOP",
                Type = "Line",
                Points = new List<Point3D>
                {
                    new Point3D(0, 0, 0),
                    new Point3D(6000, 0, 0),
                },
                Bulge = 0,
                IsHidden = false,
            },
            new DrawingObjectInfo
            {
                Index = 3,
                ObjectId = 12004,
                ViewIndex = 0,
                ViewId = 11001,
                ViewName = "FRONT",
                Type = "StraightDimensionSet",
                Points = new List<Point3D>
                {
                    new Point3D(0, 0, 0),
                    new Point3D(0, 4000, 0),
                },
                IsHidden = false,
            },
        };

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
                Position = type == "Beam" || type == "ContourPlate"
                    ? new PartPosition
                    {
                        Plane = "MIDDLE",
                        PlaneOffset = 0,
                        Rotation = "FRONT",
                        RotationOffset = 0,
                        Depth = "MIDDLE",
                        DepthOffset = 0,
                    }
                    : null,
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
