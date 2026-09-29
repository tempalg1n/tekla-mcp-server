using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.FileExchange;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Mock;

/// <summary>
/// Mock half of the file-exchange tools. Unlike the rest of the mock these DO touch the disk:
/// the whole point of the feature is a real file, and running the same
/// <see cref="ExportRunner"/> / <see cref="UdaImportRunner"/> as the live backend is what keeps
/// the two file layouts identical. Only the row source is synthetic.
/// </summary>
public sealed partial class MockTeklaModelService
{
    /// <summary>
    /// Test seam: when set, replaces the environment-derived path policy so tests can point the
    /// allow-list at a temp directory without mutating process-wide environment variables.
    /// </summary>
    public FilePathPolicy? FilePolicyOverride { get; set; }

    private FilePathPolicy ResolveFilePolicy() => FilePolicyOverride ?? FilePathPolicy.FromEnvironment();

    public ExportResult ExportObjectsToFile(ObjectQuery query, ExportRequest request)
    {
        request = request ?? new ExportRequest();
        query = query ?? new ObjectQuery();

        if (!ExportFields.TryParse(request.Fields, out var fieldSet, out var fieldError))
            return new ExportResult { Backend = BackendName, Path = request.Path, Message = fieldError };

        // Same scan scope as the real backend: selection > explicit type > parts chain > all.
        // The filter is applied INSIDE the stream, not up front, so cursor offsets and
        // scannedObjects mean the same thing here as they do against live Tekla.
        var source = ScopedObjects(
            query.UseSelection, request.PartsOnly && string.IsNullOrWhiteSpace(query.Type)).ToList();

        return ExportRunner.Run(
            ResolveFilePolicy(),
            request.Path,
            request.Format,
            request.Append,
            request.Cursor,
            request.MaxObjects,
            BackendName,
            fieldSet.Names,
            ExportRowBuilder.PartPrototype(fieldSet),
            (skip, state) => StreamPartRows(source, query, fieldSet, skip, request.MaxObjects, state),
            request.MaxSeconds);
    }

    public ExportResult ExportReferenceObjectsToFile(ReferenceExportRequest request)
    {
        request = request ?? new ReferenceExportRequest();
        var entityFilter = new HashSet<string>(
            (request.EntityFilter ?? new List<string>())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim()),
            StringComparer.OrdinalIgnoreCase);

        return ExportRunner.Run(
            ResolveFilePolicy(),
            request.Path,
            request.Format,
            request.Append,
            request.Cursor,
            request.MaxObjects,
            BackendName,
            ExportRowBuilder.ReferenceFieldNames,
            ExportRowBuilder.ReferencePrototype(),
            (skip, state) => StreamReferenceRows(entityFilter, skip, request.MaxObjects, state),
            request.MaxSeconds);
    }

    public UdaFileWriteResult SetUdasFromFile(UdaFileWriteRequest request) =>
        UdaImportRunner.Run(
            ResolveFilePolicy(),
            request ?? new UdaFileWriteRequest(),
            BackendName,
            new MockUdaImportTarget(this));

    // ------------------------------------------------------------------- sources ----

    private IEnumerable<DataRow> StreamPartRows(
        List<ModelObjectInfo> source,
        ObjectQuery query,
        ExportFieldSet fieldSet,
        long skip,
        int? maxObjects,
        ExportScanState state)
    {
        var row = new DataRow();
        var record = new PartExportRecord();
        long skipped = 0;

        foreach (var info in source)
        {
            if (skipped < skip) { skipped++; continue; }
            if (maxObjects is int cap && cap > 0 && state.ScannedObjects >= cap)
            {
                state.StoppedEarly = true;
                yield break;
            }
            state.ScannedObjects++;

            if (!MatchesFilters(info, query)) continue;

            record.Reset();
            record.CopyFrom(info);
            FillSyntheticGeometry(record, info, fieldSet);
            if (_udasByGuid.TryGetValue(info.Guid, out var udas))
            {
                foreach (var name in fieldSet.UdaNames)
                    if (udas.TryGetValue(name, out var value)) record.Udas[name] = value;
            }

            ExportRowBuilder.BuildPartRow(fieldSet, record, row);
            yield return row;
        }
    }

    /// <summary>
    /// Believable stand-ins for the members the real backend reads off the Tekla object: a COG
    /// at the AABB centre, an axis-aligned coordinate system and a rectangular plate contour.
    /// </summary>
    private static void FillSyntheticGeometry(
        PartExportRecord record, ModelObjectInfo info, ExportFieldSet fieldSet)
    {
        if (fieldSet.NeedsCog && info.CenterX.HasValue)
            record.Cog = new Point3D(info.CenterX.Value, info.CenterY ?? 0, info.CenterZ ?? 0);

        if (fieldSet.NeedsCoordSystem)
        {
            record.CsOrigin = record.StartPoint ??
                (info.CenterX.HasValue ? new Point3D(info.CenterX.Value, info.CenterY ?? 0, info.CenterZ ?? 0) : null);
            if (record.CsOrigin != null)
            {
                record.CsAxisX = new Point3D(1, 0, 0);
                record.CsAxisY = new Point3D(0, 1, 0);
                record.CsAxisZ = new Point3D(0, 0, 1);
            }
        }

        if (fieldSet.NeedsContour &&
            string.Equals(info.Type, "ContourPlate", StringComparison.OrdinalIgnoreCase) &&
            info.MinX.HasValue && info.MaxX.HasValue)
        {
            var z = info.MinZ ?? 0;
            record.ContourPoints = new List<Point3D>
            {
                new Point3D(info.MinX.Value, info.MinY ?? 0, z),
                new Point3D(info.MaxX.Value, info.MinY ?? 0, z),
                new Point3D(info.MaxX.Value, info.MaxY ?? 0, z),
                new Point3D(info.MinX.Value, info.MaxY ?? 0, z),
            };
        }
    }

    /// <summary>
    /// A small synthetic reference model: enough entity variety for entityFilter and enough rows
    /// for cursor paging to be exercised without Tekla.
    /// </summary>
    private static IReadOnlyList<ReferenceGeometryInfo> BuildMockReferenceObjects()
    {
        var entities = new[] { "IFCBEAM", "IFCCOLUMN", "IFCPLATE", "IFCMEMBER", "IFCBEAM", "IFCDISCRETEACCESSORY" };
        var rows = new List<ReferenceGeometryInfo>();
        for (var i = 0; i < entities.Length; i++)
        {
            var x = 6000.0 * i;
            rows.Add(new ReferenceGeometryInfo
            {
                Id = 901 + i,
                ExternalGuid = "2X_m0ckRef" + i.ToString("000"),
                Entity = entities[i],
                Name = entities[i] + " " + (i + 1),
                Description = "Mock reference object",
                ObjectType = "Steel",
                ReferenceModelTitle = "Mock KMD IFC",
                ReferenceModelFile = "mock-kmd.ifc",
                MinX = x,
                MinY = 0,
                MinZ = 0,
                MaxX = x + 5800,
                MaxY = 200,
                MaxZ = 400,
                AabbSource = "tekla-faces",
                PlacementOrigin = new Point3D(x, 0, 0),
                PlacementXAxis = new Point3D(1, 0, 0),
                PlacementYAxis = new Point3D(0, 1, 0),
                PlacementZAxis = new Point3D(0, 0, 1),
                PlacementSource = "ifc-file",
            });
        }
        return rows;
    }

    private static IEnumerable<DataRow> StreamReferenceRows(
        HashSet<string> entityFilter, long skip, int? maxObjects, ExportScanState state)
    {
        var row = new DataRow();
        long skipped = 0;

        foreach (var info in BuildMockReferenceObjects())
        {
            if (skipped < skip) { skipped++; continue; }
            if (maxObjects is int cap && cap > 0 && state.ScannedObjects >= cap)
            {
                state.StoppedEarly = true;
                yield break;
            }
            state.ScannedObjects++;

            if (entityFilter.Count > 0 && !entityFilter.Contains(info.Entity)) continue;

            ExportRowBuilder.BuildReferenceRow(info, row);
            yield return row;
        }
    }

    // -------------------------------------------------------------- import target ----

    /// <summary>Routes the shared import runner at the mock's in-memory UDA dictionary.</summary>
    private sealed class MockUdaImportTarget : IUdaImportTarget
    {
        private readonly MockTeklaModelService _owner;

        public MockUdaImportTarget(MockTeklaModelService owner) => _owner = owner;

        public object? Resolve(string guid) => _owner.GetObjectByGuid(guid);

        public UdaImportIdentity Describe(object handle)
        {
            var info = (ModelObjectInfo)handle;
            return new UdaImportIdentity
            {
                Guid = info.Guid,
                Id = info.Id,
                Type = info.Type,
                AssemblyPos = info.AssemblyPos,
            };
        }

        public string? ReadUda(object handle, string name)
        {
            var info = (ModelObjectInfo)handle;
            if (!_owner._udasByGuid.TryGetValue(info.Guid, out var udas)) return null;
            return udas.TryGetValue(name, out var value) ? value : null;
        }

        public int WriteUdas(object handle, IReadOnlyList<KeyValuePair<string, string>> values)
        {
            var info = (ModelObjectInfo)handle;
            if (!_owner._udasByGuid.TryGetValue(info.Guid, out var udas))
            {
                udas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _owner._udasByGuid[info.Guid] = udas;
            }

            var written = 0;
            foreach (var pair in values)
            {
                udas[pair.Key] = pair.Value ?? "";
                written++;
            }
            return written;
        }

        public void Commit()
        {
            // The mock has no transaction to commit.
        }
    }
}
