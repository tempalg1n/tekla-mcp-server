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
    /// profile/material and total weight).
    /// </summary>
    ModelSummary GetModelSummary();

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
}
