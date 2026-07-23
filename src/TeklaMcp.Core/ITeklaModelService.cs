using System.Collections.Generic;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core;

/// <summary>
/// Abstraction over a single open Tekla Structures model.
///
/// There are two implementations:
///   * <c>MockTeklaModelService</c>  — cross-platform, returns synthetic data so the
///     MCP server can run on machines without Tekla (e.g. macOS).
///   * <c>TeklaModelService</c>      — Windows + .NET Framework, backed by the
///     Tekla Open API (Tekla.Structures.Model).
///
/// The MCP tool layer depends ONLY on this interface, so the same tools work against
/// either backend. All methods are synchronous and may block while talking to Tekla.
/// Implementations should never throw for "nothing found" — return empty collections
/// or <c>null</c> as documented.
/// </summary>
public interface ITeklaModelService
{
    /// <summary>
    /// Probe whether a Tekla model is open and reachable. Cheap; safe to call first.
    /// Never throws — failures are reported via <see cref="ConnectionInfo.Connected"/>
    /// and <see cref="ConnectionInfo.Message"/>.
    /// </summary>
    ConnectionInfo GetConnectionInfo();

    /// <summary>
    /// Aggregate statistics over every object in the model (counts by type/class/
    /// profile/material and total weight). Implementations must stream — never build an
    /// in-memory list of all objects. <paramref name="includeWeights"/> false skips the
    /// per-object weight lookup (much faster on huge models); <paramref name="maxObjects"/>
    /// caps the scan and marks the result <see cref="ModelSummary.Truncated"/>.
    /// </summary>
    ModelSummary GetModelSummary(bool includeWeights = true, int? maxObjects = null);

    /// <summary>
    /// Count objects matching <paramref name="query"/> WITHOUT materializing them.
    /// Implementations should use the cheapest possible path (e.g. an enumerator size for
    /// an unfiltered count) and must not read properties the query does not filter on.
    /// </summary>
    int CountObjects(ObjectQuery query);

    /// <summary>
    /// Return up to <paramref name="limit"/> objects (<c>null</c> = all). Large models
    /// can hold tens of thousands of objects, so callers should pass a sensible limit.
    /// </summary>
    IReadOnlyList<ModelObjectInfo> GetAllObjects(int? limit = null);

    /// <summary>Return objects matching <paramref name="query"/>. Empty filter = all.</summary>
    IReadOnlyList<ModelObjectInfo> FindObjects(ObjectQuery query, int? limit = null);

    /// <summary>Look up a single object by its Tekla GUID. Returns <c>null</c> if not found.</summary>
    ModelObjectInfo? GetObjectByGuid(string guid);

    /// <summary>
    /// Read arbitrary named properties for one object by GUID: report properties
    /// (e.g. VOLUME, AREA, HEIGHT), UDAs, or built-ins (GUID, ID, NAME, CLASS, PROFILE,
    /// MATERIAL, WEIGHT, LENGTH, ASSEMBLY_POS). Unknown/blank names are skipped. Uses the
    /// same name resolution as the attribute filters, so any name usable in a filter works.
    /// </summary>
    ObjectUdaResult GetProperties(string guid, IReadOnlyList<string> names);

    /// <summary>Objects currently selected by the user in the Tekla Structures UI.</summary>
    IReadOnlyList<ModelObjectInfo> GetSelectedObjects();

    /// <summary>
    /// Read semantic metadata and world geometry for reference-model objects. Reference objects
    /// are addressed by integer Tekla IDs because their Tekla GUID is commonly empty, or by
    /// external IFC GlobalIds via <paramref name="externalGuids"/>. When
    /// <paramref name="useSelection"/> is true, <paramref name="ids"/> may be empty.
    /// Face data is best-effort and capped per object; failures are returned in each DTO.
    /// When the Open API cannot deliver geometry, implementations should fall back to parsing
    /// the reference IFC file itself for placement + overall dimensions.
    /// </summary>
    IReadOnlyList<ReferenceGeometryInfo> GetReferenceGeometry(
        IReadOnlyList<int> ids,
        bool useSelection = false,
        int maxObjects = 20,
        int maxFacesPerObject = 100,
        int maxTotalFaces = 1000,
        int maxTotalPoints = 20000,
        IReadOnlyList<string>? externalGuids = null);

    /// <summary>
    /// Select objects in the Tekla UI by query and return a lightweight result snapshot.
    /// Implementations should be best-effort and return an empty selection on failure.
    /// </summary>
    SelectionResult SelectObjects(ObjectQuery query, int? limit = null);

    /// <summary>
    /// Search candidate attributes by known value (for "which field stores X?" discovery).
    /// Implementations should inspect common report properties and UDAs.
    /// </summary>
    IReadOnlyList<AttributeValueMatch> FindAttributesByValue(
        string value,
        IReadOnlyList<string>? candidateAttributeNames = null,
        bool exactMatch = false,
        int? objectLimit = 2000,
        int? resultLimit = 50);

    /// <summary>
    /// Analyze how members of a profile connect to neighboring elements near beam ends.
    /// Returns unique connection signatures and their frequencies.
    /// </summary>
    ProfileConnectionSummary AnalyzeConnectionsForProfile(
        string profile,
        double toleranceMm = 50,
        int? limit = 1000);

    /// <summary>
    /// Read specified UDA fields from one object by GUID.
    /// </summary>
    ObjectUdaResult GetObjectUdas(string guid, IReadOnlyList<string> udaNames);

    /// <summary>
    /// Set UDA fields on one object by GUID. By default, implementations should support
    /// preview mode when <paramref name="apply"/> is false.
    /// </summary>
    UdaOperationResult SetObjectUdas(string guid, IReadOnlyDictionary<string, string> updates, bool apply);

    /// <summary>
    /// Set UDA fields on objects matching a query. By default, implementations should
    /// support preview mode when <paramref name="apply"/> is false.
    /// </summary>
    UdaOperationResult SetUdas(
        ObjectQuery query,
        IReadOnlyDictionary<string, string> updates,
        bool apply,
        int? limit = null);

    // -- Geometry / grids -----------------------------------------------------------------

    /// <summary>
    /// Read the model's grid lines (labels + coordinates), so callers can translate human
    /// axis references into coordinates. Best-effort; returns an empty list if no grids.
    /// </summary>
    IReadOnlyList<GridLineInfo> GetGrids();

    /// <summary>
    /// Resolve a model point from an X-axis label, a Y-axis label and an elevation (mm),
    /// using <see cref="GetGrids"/>. <see cref="PointResult.Resolved"/> is false if a label
    /// is not found.
    /// </summary>
    PointResult ResolvePoint(string axisXLabel, string axisYLabel, double z);

    // -- Mutations (create / modify / delete) ---------------------------------------------
    //
    // ALL mutations honor preview-by-default: when apply == false NOTHING is written, but the
    // plan + counts are returned. Implementations stamp created/modified objects with a
    // "MCP_ORIGIN" UDA for traceability, and should cap batch sizes for safety.

    /// <summary>Create one or more parts (beams/columns/plates) from specs. Preview unless apply.</summary>
    WriteResult CreateParts(IReadOnlyList<PartSpec> specs, bool apply);

    /// <summary>Modify existing parts (properties, endpoints, handle swap) by GUID. Preview unless apply.</summary>
    WriteResult ModifyParts(IReadOnlyList<PartModification> modifications, bool apply);

    /// <summary>Delete objects matching a query. Preview unless apply. Capped by <paramref name="limit"/>.</summary>
    WriteResult DeleteObjects(ObjectQuery query, bool apply, int? limit = null);

    /// <summary>List connections/components attached to a part. Empty if the part is not found.</summary>
    IReadOnlyList<ComponentInfo> GetConnections(string partGuid);

    /// <summary>
    /// Create one or more connections. Preview unless apply; implementations commit geometry
    /// once before inserting components so newly-created parts can be addressed reliably.
    /// </summary>
    WriteResult CreateConnections(IReadOnlyList<ConnectionSpec> specs, bool apply);

    // -- Drawings ---------------------------------------------------------------------------

    /// <summary>Probe the Drawing API and return the currently active drawing, if any.</summary>
    DrawingStatusInfo GetDrawingStatus();

    /// <summary>List/filter drawing-list rows. Empty result means no matches.</summary>
    IReadOnlyList<DrawingInfo> FindDrawings(DrawingQuery query, int? limit = null);

    /// <summary>Return a bounded page of model identifiers represented by one drawing.</summary>
    DrawingModelObjectResult GetDrawingModelObjects(
        string keyOrMark,
        int offset = 0,
        int limit = 500);

    /// <summary>
    /// Return active sheet geometry and configured layout size in paper millimetres.
    /// When no drawing is active, <see cref="DrawingSheetInfo.Available"/> is false.
    /// </summary>
    DrawingSheetInfo GetDrawingSheet();

    /// <summary>List views on the active drawing sheet. Requires an active drawing.</summary>
    IReadOnlyList<DrawingViewInfo> GetDrawingViews();

    /// <summary>List/filter objects in the active drawing or current drawing selection.</summary>
    IReadOnlyList<DrawingObjectInfo> FindDrawingObjects(DrawingObjectQuery query, int? limit = null);

    /// <summary>Select matching objects in the active drawing editor.</summary>
    DrawingSelectionResult SelectDrawingObjects(DrawingObjectQuery query, int? limit = null);

    /// <summary>Open a drawing by exact MCP key or unambiguous exact mark. Preview unless apply.</summary>
    DrawingWriteResult OpenDrawing(string keyOrMark, bool showDrawing, bool apply);

    /// <summary>Close the active drawing, optionally saving it. Preview unless apply.</summary>
    DrawingWriteResult CloseActiveDrawing(bool save, bool apply);

    /// <summary>Save the active drawing. Preview unless apply.</summary>
    DrawingWriteResult SaveActiveDrawing(bool apply);

    /// <summary>Create assembly/single-part/cast-unit/GA drawings. Preview unless apply.</summary>
    DrawingWriteResult CreateDrawings(IReadOnlyList<DrawingSpec> specs, bool apply);

    /// <summary>Create drawings using a saved Tekla AutoDrawing rule. Preview unless apply.</summary>
    DrawingWriteResult CreateDrawingsFromRule(
        string ruleFile,
        IReadOnlyList<string> modelGuids,
        bool apply);

    /// <summary>Modify drawing-list metadata/flags for matched drawings. Preview unless apply.</summary>
    DrawingWriteResult ModifyDrawings(
        DrawingQuery query,
        DrawingModification modification,
        bool apply,
        int? limit = null);

    /// <summary>
    /// Run a drawing-list operation: delete, issue, unissue, update, place_views, or print.
    /// Preview unless apply.
    /// </summary>
    DrawingWriteResult OperateDrawings(
        DrawingQuery query,
        string operation,
        DrawingPrintOptions? printOptions,
        bool apply,
        int? limit = null);

    /// <summary>Create orthogonal/3D views on the active drawing. Preview unless apply.</summary>
    DrawingWriteResult CreateDrawingViews(IReadOnlyList<DrawingViewSpec> specs, bool apply);

    /// <summary>Modify active-drawing views by their current zero-based indices. Preview unless apply.</summary>
    DrawingWriteResult ModifyDrawingViews(
        IReadOnlyList<DrawingViewModification> modifications,
        bool apply);

    /// <summary>Create annotations/graphics/dimensions/marks in the active drawing. Preview unless apply.</summary>
    DrawingWriteResult CreateDrawingObjects(IReadOnlyList<DrawingObjectSpec> specs, bool apply);

    /// <summary>Modify/move/hide/show/delete active-drawing objects. Preview unless apply.</summary>
    DrawingWriteResult ModifyDrawingObjects(
        DrawingObjectQuery query,
        DrawingObjectModification modification,
        bool apply,
        int? limit = null);

    /// <summary>Merge or split matched drawing marks. Preview unless apply.</summary>
    DrawingWriteResult OperateDrawingMarks(
        DrawingObjectQuery query,
        string operation,
        bool apply,
        int? limit = null);

    // -- Script escape hatch ----------------------------------------------------------------

    /// <summary>
    /// Validate, compile and (real backend only) execute an agent-authored C# script against
    /// the model, for capabilities no dedicated tool covers yet. Never throws — every failure
    /// (policy violation, compile error, runtime exception, timeout) is reported inside
    /// <see cref="ScriptResult"/>. The safety policy (read-only unless
    /// <paramref name="allowMutations"/>, no file/network/process/reflection access, bounded
    /// execution deadline with best-effort abort) is enforced by the implementation; the mock backend validates and compiles
    /// but never executes (<see cref="ScriptResult.Executed"/> stays false).
    /// <paramref name="compileOnly"/> runs policy + compilation on either backend without
    /// connecting to or executing against the live model; mutation syntax may be allowed so
    /// the exact source can be checked before user approval.
    /// </summary>
    ScriptResult ExecuteScript(
        string code,
        bool allowMutations = false,
        int timeoutSeconds = 60,
        bool compileOnly = false);
}
