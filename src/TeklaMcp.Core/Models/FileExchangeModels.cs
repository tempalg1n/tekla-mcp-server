using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Request for a streaming export to a file on disk. The point of these tools is that the DATA
/// never enters the tool response — a geometric reconciliation moves tens of MB, which cannot
/// pass through an LLM context.
/// </summary>
public sealed class ExportRequest
{
    /// <summary>Absolute path of the output file. Must sit under an allowed root.</summary>
    public string Path { get; set; } = "";

    /// <summary>"jsonl" (default) or "csv".</summary>
    public string? Format { get; set; }

    /// <summary>Requested columns; empty means the default identity + BOM set.</summary>
    public List<string> Fields { get; set; } = new List<string>();

    /// <summary>Cap on SOURCE objects scanned in this call — the client-timeout safety valve.</summary>
    public int? MaxObjects { get; set; }

    /// <summary>
    /// Wall-clock budget for one call, in seconds. The scan stops between objects once it is
    /// exceeded and reports a cursor, so a page whose per-object cost was guessed wrong still
    /// returns instead of running past the client's patience.
    ///
    /// It bounds MANY slow objects, not ONE wedged call: the check can only run between rows,
    /// so an Open API call that never returns is still unbounded. Nothing inside this process
    /// can interrupt Tekla once it is computing.
    /// </summary>
    public double? MaxSeconds { get; set; }

    /// <summary>Continuation cursor from the previous page's NextCursor.</summary>
    public string? Cursor { get; set; }

    /// <summary>Append to an existing file instead of replacing it. Required when paging.</summary>
    public bool Append { get; set; }

    /// <summary>Scan physical parts only (default), as in the analytics tools.</summary>
    public bool PartsOnly { get; set; } = true;
}

/// <summary>Outcome of one export call. Carries counters only — never exported rows.</summary>
public sealed class ExportResult
{
    /// <summary>Canonical full path actually written.</summary>
    public string Path { get; set; } = "";

    /// <summary>Format used: "jsonl" or "csv".</summary>
    public string Format { get; set; } = "";

    /// <summary>Columns written, in file order.</summary>
    public List<string> Fields { get; set; } = new List<string>();

    /// <summary>Rows written by THIS call (not the whole file when appending).</summary>
    public long Written { get; set; }

    /// <summary>Source objects consumed by this call, cursor skip excluded.</summary>
    public long ScannedObjects { get; set; }

    /// <summary>File size in bytes after the call.</summary>
    public long Bytes { get; set; }

    /// <summary>True when the scan stopped on <see cref="ExportRequest.MaxObjects"/>.</summary>
    public bool Truncated { get; set; }

    /// <summary>Pass back as Cursor (with Append=true) to continue. Null when finished.</summary>
    public string? NextCursor { get; set; }

    /// <summary>Wall-clock seconds spent in this call.</summary>
    public double Seconds { get; set; }

    /// <summary>Whether this call appended to existing content.</summary>
    public bool Appended { get; set; }

    /// <summary>
    /// Sidecar file holding these same counters. Written on every call so a lost response
    /// (MCP clients abort at ~60 s) does not destroy the evidence that the export finished.
    /// </summary>
    public string? StatusPath { get; set; }

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>Status, warning or error text. Failures land here — these tools do not throw.</summary>
    public string? Message { get; set; }
}

/// <summary>Request for a bulk UDA write driven by a file of GUID → value rows.</summary>
public sealed class UdaFileWriteRequest
{
    /// <summary>Absolute path of the input file. Must sit under an allowed root.</summary>
    public string Path { get; set; } = "";

    /// <summary>"jsonl", "csv", or null to infer from the extension.</summary>
    public string? Format { get; set; }

    /// <summary>Column/property holding the Tekla GUID. Default "guid".</summary>
    public string GuidColumn { get; set; } = "guid";

    /// <summary>Columns to write as UDAs. Empty means every column except the GUID column.</summary>
    public List<string> UdaColumns { get; set; } = new List<string>();

    /// <summary>Safety switch. False (default) = preview: counters only, nothing written.</summary>
    public bool Apply { get; set; }

    /// <summary>
    /// False (default) leaves a UDA that already holds a value untouched. Decisions a human
    /// made are not the agent's to overwrite.
    /// </summary>
    public bool OverwriteNonEmpty { get; set; }

    /// <summary>"skip" (default) or "fail" when a GUID is not in the model.</summary>
    public string OnMissing { get; set; } = "skip";

    /// <summary>Cap on rows processed in this call — the client-timeout safety valve.</summary>
    public int? MaxObjects { get; set; }

    /// <summary>Continuation cursor from the previous page's NextCursor (a row offset).</summary>
    public string? Cursor { get; set; }
}

/// <summary>Outcome of one bulk UDA write. Same counters in preview and apply mode.</summary>
public sealed class UdaFileWriteResult
{
    /// <summary>False = preview mode, true = changes were applied.</summary>
    public bool Applied { get; set; }

    /// <summary>Data rows consumed by this call.</summary>
    public long RowsRead { get; set; }

    /// <summary>Rows whose GUID resolved to an object in the model.</summary>
    public long Matched { get; set; }

    /// <summary>Objects that were (or, in preview, would be) updated.</summary>
    public long Updated { get; set; }

    /// <summary>Total UDA fields written across all objects.</summary>
    public long UpdatedFields { get; set; }

    /// <summary>Fields left alone because they already held a value and OverwriteNonEmpty was false.</summary>
    public long SkippedNonEmpty { get; set; }

    /// <summary>Rows whose GUID is not in the model.</summary>
    public long NotFound { get; set; }

    /// <summary>Objects whose UDAs already equal the requested values.</summary>
    public long Unchanged { get; set; }

    /// <summary>Rows that could not be parsed or carried no GUID.</summary>
    public long InvalidRows { get; set; }

    /// <summary>
    /// UDA columns this call considered, taken from the file (after any UdaColumns filtering).
    /// Shows WHICH fields the run is about; whether each one changed is in the per-row sample
    /// and the Updated/SkippedNonEmpty/Unchanged counters.
    /// </summary>
    public List<string> Fields { get; set; } = new List<string>();

    /// <summary>First few decisions, for the user to eyeball before applying.</summary>
    public List<UdaFileWritePreview> Sample { get; set; } = new List<UdaFileWritePreview>();

    /// <summary>True when the run stopped on <see cref="UdaFileWriteRequest.MaxObjects"/>.</summary>
    public bool Truncated { get; set; }

    /// <summary>Pass back as Cursor to continue. Null when the file is exhausted.</summary>
    public string? NextCursor { get; set; }

    /// <summary>Wall-clock seconds spent in this call.</summary>
    public double Seconds { get; set; }

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>Status, warning or error text. Failures land here — these tools do not throw.</summary>
    public string? Message { get; set; }
}

/// <summary>One sampled decision from a bulk UDA write.</summary>
public sealed class UdaFileWritePreview
{
    public string Guid { get; set; } = "";
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string? AssemblyPos { get; set; }

    /// <summary>"update", "unchanged", "skip-non-empty", "not-found" or "invalid".</summary>
    public string Action { get; set; } = "";

    public List<UdaFieldChange> Changes { get; set; } = new List<UdaFieldChange>();
}

/// <summary>One field's before/after in a bulk UDA write.</summary>
public sealed class UdaFieldChange
{
    public string Name { get; set; } = "";

    /// <summary>Current value; null when the UDA is not set on the object.</summary>
    public string? From { get; set; }

    public string To { get; set; } = "";
}

/// <summary>Request for a streaming export of reference-model (IFC) objects to a file.</summary>
public sealed class ReferenceExportRequest
{
    /// <summary>Absolute path of the output file. Must sit under an allowed root.</summary>
    public string Path { get; set; } = "";

    /// <summary>"jsonl" (default) or "csv".</summary>
    public string? Format { get; set; }

    /// <summary>Restrict to one reference model by its Tekla integer ID.</summary>
    public int? ReferenceModelId { get; set; }

    /// <summary>Restrict to one reference model by its Tekla GUID.</summary>
    public string? ReferenceModelGuid { get; set; }

    /// <summary>
    /// Keep only these IFC entity types (e.g. IFCBEAM, IFCCOLUMN, IFCMEMBER, IFCPLATE), so bolts
    /// and welds do not dominate the file. Empty means every entity.
    /// </summary>
    public List<string> EntityFilter { get; set; } = new List<string>();

    /// <summary>Cap on source objects scanned in this call.</summary>
    public int? MaxObjects { get; set; }

    /// <summary>Continuation cursor from the previous page's NextCursor.</summary>
    public string? Cursor { get; set; }

    /// <summary>Append to an existing file instead of replacing it. Required when paging.</summary>
    public bool Append { get; set; }

    /// <summary>
    /// Read face polygons to derive an EXACT world AABB. OFF by default, deliberately:
    /// <c>ModelInternal.Operation.GetReferenceModelObjectFaces</c> is an internal, version-
    /// sensitive call, and on a live 2021 model (3155, 2026-08-13) running it across an IFC
    /// overlay wedged Tekla itself — one core pegged, UI unresponsive, and killing the MCP
    /// client did NOT release it. With this off the export uses IFC-file placement estimates,
    /// which every row labels via aabbSource. Turn it on only after probing a handful of
    /// objects with maxObjects=5 on that specific reference model.
    /// </summary>
    public bool IncludeFaceAabb { get; set; }

    /// <summary>
    /// Face budget per object when <see cref="IncludeFaceAabb"/> is on. An AABB built from a
    /// truncated face set would be too small, so rows that hit the cap are labelled
    /// aabbSource="tekla-faces-truncated" rather than passed off as exact.
    /// </summary>
    public int MaxFacesPerObject { get; set; } = 2000;

    /// <summary>
    /// Wall-clock budget for one call, in seconds. See <see cref="ExportRequest.MaxSeconds"/>
    /// for what it can and cannot bound.
    /// </summary>
    public double? MaxSeconds { get; set; }
}
