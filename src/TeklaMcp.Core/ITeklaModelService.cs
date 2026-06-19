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

    /// <summary>Objects currently selected by the user in the Tekla Structures UI.</summary>
    IReadOnlyList<ModelObjectInfo> GetSelectedObjects();
}
