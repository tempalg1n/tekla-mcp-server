using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.FileExchange;
using TeklaMcp.Core.Models;
using TS = Tekla.Structures;
using TSM = Tekla.Structures.Model;
using TSG = Tekla.Structures.Geometry3d;

namespace TeklaMcp.Tekla;

/// <summary>
/// Live-Tekla half of the file-exchange tools. The file mechanics (path policy, format,
/// paging, status sidecar) live in <see cref="ExportRunner"/> / <see cref="UdaImportRunner"/>;
/// what stays here is the only part that needs Tekla: walking the model and reading properties.
///
/// The scan follows the same rule as the rest of the backend — filter with <c>MapBasic</c>,
/// then pay for report properties, solids, coordinate systems and UDAs only on rows that
/// actually reach the file, and only for the fields the caller asked for.
/// </summary>
public sealed partial class TeklaModelService
{
    public ExportResult ExportObjectsToFile(ObjectQuery query, ExportRequest request)
    {
        request = request ?? new ExportRequest();
        query = query ?? new ObjectQuery();

        if (!ExportFields.TryParse(request.Fields, out var fieldSet, out var fieldError))
            return Failed(request.Path, fieldError);

        TSM.Model model;
        try
        {
            model = GetConnectedModel();
        }
        catch (Exception ex)
        {
            return Failed(request.Path, ErrorText.Flatten(ex));
        }

        var policy = FilePathPolicy.FromEnvironment(TryGetModelFolder(model));

        // The work plane must stay global for the WHOLE iteration, not just the setup — the
        // rows are produced lazily inside ExportRunner.Run.
        return InGlobalWorkPlane(model, () => ExportRunner.Run(
            policy,
            request.Path,
            request.Format,
            request.Append,
            request.Cursor,
            request.MaxObjects,
            BackendName,
            fieldSet.Names,
            ExportRowBuilder.PartPrototype(fieldSet),
            (skip, state) => StreamPartRows(model, query, fieldSet, request, skip, state),
            request.MaxSeconds));
    }

    public ExportResult ExportReferenceObjectsToFile(ReferenceExportRequest request)
    {
        request = request ?? new ReferenceExportRequest();

        TSM.Model model;
        try
        {
            model = GetConnectedModel();
        }
        catch (Exception ex)
        {
            return Failed(request.Path, ErrorText.Flatten(ex));
        }

        var modelPath = TryGetModelFolder(model);
        var policy = FilePathPolicy.FromEnvironment(modelPath);

        return InGlobalWorkPlane(model, () => ExportRunner.Run(
            policy,
            request.Path,
            request.Format,
            request.Append,
            request.Cursor,
            request.MaxObjects,
            BackendName,
            ExportRowBuilder.ReferenceFieldNames,
            ExportRowBuilder.ReferencePrototype(),
            (skip, state) => StreamReferenceRows(model, request, modelPath ?? "", skip, state),
            request.MaxSeconds));
    }

    public UdaFileWriteResult SetUdasFromFile(UdaFileWriteRequest request)
    {
        request = request ?? new UdaFileWriteRequest();

        TSM.Model model;
        try
        {
            model = GetConnectedModel();
        }
        catch (Exception ex)
        {
            return new UdaFileWriteResult
            {
                Applied = false,
                Backend = BackendName,
                Message = ErrorText.Flatten(ex),
            };
        }

        var policy = FilePathPolicy.FromEnvironment(TryGetModelFolder(model));
        return UdaImportRunner.Run(policy, request, BackendName, new TeklaUdaImportTarget(model));
    }

    // ------------------------------------------------------------------- sources ----

    private static IEnumerable<DataRow> StreamPartRows(
        TSM.Model model,
        ObjectQuery query,
        ExportFieldSet fieldSet,
        ExportRequest request,
        long skip,
        ExportScanState state)
    {
        var row = new DataRow();
        var record = new PartExportRecord();
        long skipped = 0;

        foreach (var mo in EnumerateSource(model, query, partsFallback: request.PartsOnly))
        {
            // Cursor skip: consume the enumerator without touching a single property — a bare
            // MoveNext is far cheaper than a property-reading iteration on live Tekla.
            if (skipped < skip) { skipped++; continue; }

            if (request.MaxObjects is int cap && cap > 0 && state.ScannedObjects >= cap)
            {
                state.StoppedEarly = true;
                yield break;
            }
            state.ScannedObjects++;

            var info = MapBasic(mo);
            if (info is null || !Matches(info, query) || !MatchesUda(mo, query)) continue;

            record.Reset();
            record.CopyFrom(info);
            FillExportRecord(mo, record, fieldSet);

            ExportRowBuilder.BuildPartRow(fieldSet, record, row);
            yield return row;
        }
    }

    /// <summary>
    /// Read the expensive members — but only the ones the field set asked for. Each block below
    /// is a per-object remoting call; <c>GetSolid()</c> alone is ~1.5 ms on a live model, which
    /// is ~100 s over a 65k-part export.
    /// </summary>
    private static void FillExportRecord(
        TSM.ModelObject mo, PartExportRecord record, ExportFieldSet fieldSet)
    {
        var part = mo as TSM.Part;

        if (fieldSet.NeedsReportProperties && part != null)
        {
            var assemblyPos = "";
            if (part.GetReportProperty("ASSEMBLY_POS", ref assemblyPos) && assemblyPos.Length > 0)
                record.AssemblyPos = assemblyPos;

            double weight = 0;
            if (part.GetReportProperty("WEIGHT", ref weight)) record.WeightKg = Math.Round(weight, 2);

            double length = 0;
            if (part.GetReportProperty("LENGTH", ref length)) record.LengthMm = Math.Round(length, 1);
        }

        if (fieldSet.NeedsCog && part != null)
        {
            // TODO(windows): COG_X/COG_Y/COG_Z are standard part report properties; confirm they
            // are populated for CustomPart/Brep on the live model.
            double cogX = 0, cogY = 0, cogZ = 0;
            if (part.GetReportProperty("COG_X", ref cogX) &&
                part.GetReportProperty("COG_Y", ref cogY) &&
                part.GetReportProperty("COG_Z", ref cogZ))
                record.Cog = new Point3D(Math.Round(cogX, 2), Math.Round(cogY, 2), Math.Round(cogZ, 2));
        }

        if (fieldSet.NeedsSolid && part != null)
        {
            try
            {
                var solid = part.GetSolid();
                if (solid != null)
                {
                    record.MinX = Math.Round(solid.MinimumPoint.X, 2);
                    record.MinY = Math.Round(solid.MinimumPoint.Y, 2);
                    record.MinZ = Math.Round(solid.MinimumPoint.Z, 2);
                    record.MaxX = Math.Round(solid.MaximumPoint.X, 2);
                    record.MaxY = Math.Round(solid.MaximumPoint.Y, 2);
                    record.MaxZ = Math.Round(solid.MaximumPoint.Z, 2);
                }
            }
            catch
            {
                // Some object types have no solid; an empty AABB is the honest answer.
            }
        }

        if (fieldSet.NeedsCoordSystem)
        {
            try
            {
                var cs = mo.GetCoordinateSystem();
                if (cs != null)
                {
                    record.CsOrigin = new Point3D(
                        Math.Round(cs.Origin.X, 2), Math.Round(cs.Origin.Y, 2), Math.Round(cs.Origin.Z, 2));
                    record.CsAxisX = Normalize(cs.AxisX.X, cs.AxisX.Y, cs.AxisX.Z);
                    record.CsAxisY = Normalize(cs.AxisY.X, cs.AxisY.Y, cs.AxisY.Z);
                    // Documented definition: Z is the cross product of X and Y.
                    record.CsAxisZ = Normalize(
                        cs.AxisX.Y * cs.AxisY.Z - cs.AxisX.Z * cs.AxisY.Y,
                        cs.AxisX.Z * cs.AxisY.X - cs.AxisX.X * cs.AxisY.Z,
                        cs.AxisX.X * cs.AxisY.Y - cs.AxisX.Y * cs.AxisY.X);
                }
            }
            catch
            {
                // TODO(windows): verify GetCoordinateSystem across every part type.
            }
        }

        if (fieldSet.NeedsContour || fieldSet.NeedsEndPoints)
        {
            var contour = ReadContourPoints(mo);
            if (contour != null && contour.Count > 0)
            {
                if (fieldSet.NeedsContour) record.ContourPoints = contour;
                // MapBasic only knows Beam endpoints; a PolyBeam's run is its contour.
                if (fieldSet.NeedsEndPoints && record.StartPoint is null)
                {
                    record.StartPoint = contour[0];
                    record.EndPoint = contour[contour.Count - 1];
                }
            }
        }

        foreach (var name in fieldSet.UdaNames)
            if (TryGetUserPropertyAsString(mo, name, out var value))
                record.Udas[name] = value;
    }

    /// <summary>Contour polygon of a ContourPlate / PolyBeam in global mm, or null.</summary>
    private static List<Point3D>? ReadContourPoints(TSM.ModelObject mo)
    {
        TSM.Contour? contour = null;
        if (mo is TSM.ContourPlate plate) contour = plate.Contour;
        else if (mo is TSM.PolyBeam polyBeam) contour = polyBeam.Contour;
        if (contour?.ContourPoints is null) return null;

        try
        {
            var points = new List<Point3D>();
            foreach (var item in (IEnumerable)contour.ContourPoints)
            {
                if (!(item is TSG.Point point)) continue;
                points.Add(new Point3D(
                    Math.Round(point.X, 2), Math.Round(point.Y, 2), Math.Round(point.Z, 2)));
            }
            return points;
        }
        catch
        {
            // TODO(windows): verify ContourPoints materialization for bent/lofted plates.
            return null;
        }
    }

    private static IEnumerable<DataRow> StreamReferenceRows(
        TSM.Model model,
        ReferenceExportRequest request,
        string modelPath,
        long skip,
        ExportScanState state)
    {
        var entityFilter = new HashSet<string>(
            (request.EntityFilter ?? new List<string>())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim()),
            StringComparer.OrdinalIgnoreCase);

        // OFF by default. Field report (Tekla 2021, model 3155, 2026-08-13): running the
        // internal face query across an IFC overlay pegged a core inside Tekla and left the UI
        // unresponsive; killing this process did NOT release it, because the work had already
        // been handed to Tekla. Probe a new reference model with maxObjects=5 before enabling.
        var maxFaces = request.IncludeFaceAabb ? Math.Max(1, request.MaxFacesPerObject) : 0;
        var maxPoints = maxFaces > 0 ? Math.Min(200000, Math.Max(1024, maxFaces * 64)) : 0;
        var row = new DataRow();
        long skipped = 0;

        foreach (var mo in EnumerateReferenceObjects(model, request, state))
        {
            if (skipped < skip) { skipped++; continue; }

            if (request.MaxObjects is int cap && cap > 0 && state.ScannedObjects >= cap)
            {
                state.StoppedEarly = true;
                yield break;
            }
            state.ScannedObjects++;

            if (!(mo is TSM.ReferenceModelObject reference)) continue;

            ReferenceGeometryInfo info;
            try
            {
                // maxFaces>0 gives an exact AABB from face polygons; the faces themselves are
                // NOT written — only the box they span. Keep the vertex budget bounded anyway:
                // an unbounded per-object point list is a memory risk on pathological meshes.
                info = MapReferenceGeometry(reference, maxFaces, maxPoints, modelPath);
            }
            catch (Exception ex)
            {
                state.Message = Aggregation.Append(
                    state.Message, $"Object {reference.Identifier.ID}: " + ErrorText.Flatten(ex));
                continue;
            }

            if (entityFilter.Count > 0 && !entityFilter.Contains(info.Entity ?? "")) continue;

            // An AABB spanned by a truncated face set is too SMALL — never pass it off as exact.
            if (info.Truncated && string.Equals(info.AabbSource, "tekla-faces", StringComparison.Ordinal))
                info.AabbSource = "tekla-faces-truncated";

            ExportRowBuilder.BuildReferenceRow(info, row);
            yield return row;
        }
    }

    /// <summary>
    /// Source for the reference export: one named reference model's children when the caller
    /// asked for it (cheaper and unambiguous), otherwise every reference object in the model.
    /// </summary>
    private static IEnumerable<TSM.ModelObject> EnumerateReferenceObjects(
        TSM.Model model, ReferenceExportRequest request, ExportScanState state)
    {
        var referenceModel = FindReferenceModel(model, request, out var lookupError);
        if (lookupError != null)
        {
            state.Message = Aggregation.Append(state.Message, lookupError);
            return Enumerable.Empty<TSM.ModelObject>();
        }

        if (referenceModel != null) return Drain(referenceModel.GetChildren());

        return Drain(model.GetModelObjectSelector()
                          .GetAllObjectsWithType(TSM.ModelObject.ModelObjectEnum.REFERENCE_MODEL_OBJECT));
    }

    private static TSM.ReferenceModel? FindReferenceModel(
        TSM.Model model, ReferenceExportRequest request, out string? error)
    {
        error = null;
        var hasId = request.ReferenceModelId.HasValue;
        var hasGuid = !string.IsNullOrWhiteSpace(request.ReferenceModelGuid);
        if (!hasId && !hasGuid) return null;

        try
        {
            TS.Identifier identifier;
            if (hasId)
            {
                identifier = new TS.Identifier(request.ReferenceModelId!.Value);
            }
            else if (Guid.TryParse(request.ReferenceModelGuid!.Trim(), out var parsed))
            {
                identifier = new TS.Identifier(parsed);
            }
            else
            {
                error = $"'{request.ReferenceModelGuid}' is not a GUID.";
                return null;
            }

            if (model.SelectModelObject(identifier) is TSM.ReferenceModel found) return found;

            error = hasId
                ? $"No reference model with id {request.ReferenceModelId}."
                : $"No reference model with GUID '{request.ReferenceModelGuid}'.";
            return null;
        }
        catch (Exception ex)
        {
            error = "Reference model lookup failed: " + ErrorText.Flatten(ex);
            return null;
        }
    }

    // -------------------------------------------------------------- import target ----

    /// <summary>
    /// Routes the shared import runner at the live model: GUID lookup, per-field UDA read, and
    /// a write that ends in <c>Modify()</c> per object plus one <c>CommitChanges()</c> for the
    /// batch (same shape as the other write paths in this backend).
    /// </summary>
    private sealed class TeklaUdaImportTarget : IUdaImportTarget
    {
        private readonly TSM.Model _model;

        public TeklaUdaImportTarget(TSM.Model model) => _model = model;

        public object? Resolve(string guid) => TrySelectObjectByGuid(_model, guid);

        public UdaImportIdentity Describe(object handle)
        {
            var mo = (TSM.ModelObject)handle;
            var identity = new UdaImportIdentity
            {
                Guid = mo.Identifier.GUID.ToString(),
                Id = mo.Identifier.ID,
                Type = mo.GetType().Name,
            };

            try
            {
                var assemblyPos = "";
                if (mo.GetReportProperty("ASSEMBLY_POS", ref assemblyPos) && assemblyPos.Length > 0)
                    identity.AssemblyPos = assemblyPos;
            }
            catch
            {
                // The mark is a convenience in the preview, never a reason to fail a row.
            }

            return identity;
        }

        public string? ReadUda(object handle, string name) =>
            TryGetUserPropertyAsString((TSM.ModelObject)handle, name, out var value) ? value : null;

        public int WriteUdas(object handle, IReadOnlyList<KeyValuePair<string, string>> values)
        {
            var mo = (TSM.ModelObject)handle;
            var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values) updates[pair.Key] = pair.Value;

            return ApplyUdaUpdates(mo, updates, out var changedFields) ? changedFields : 0;
        }

        public void Commit()
        {
            try
            {
                _model.CommitChanges();
            }
            catch
            {
                // Per-object Modify() already persisted the values; the commit is the batch
                // flush the other write paths also perform.
            }
        }
    }

    // ------------------------------------------------------------------- helpers ----

    private static ExportResult Failed(string? path, string message) => new ExportResult
    {
        Backend = BackendName,
        Path = (path ?? "").Trim(),
        Message = message,
    };

    /// <summary>Folder of the open model — an implicitly allowed export root.</summary>
    private static string? TryGetModelFolder(TSM.Model model)
    {
        try
        {
            var path = model.GetInfo()?.ModelPath;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static Point3D? Normalize(double x, double y, double z)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);
        if (length <= 1e-9) return null;
        return new Point3D(
            Math.Round(x / length, 6), Math.Round(y / length, 6), Math.Round(z / length, 6));
    }
}
