using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.FileExchange;

/// <summary>Mutable scan bookkeeping the row source updates as it walks the model.</summary>
public sealed class ExportScanState
{
    /// <summary>Source objects consumed so far in this call (cursor skip excluded).</summary>
    public long ScannedObjects { get; set; }

    /// <summary>Set by the source when it stopped on the maxObjects cap rather than the end.</summary>
    public bool StoppedEarly { get; set; }

    /// <summary>Non-fatal note from the scan (e.g. a partially readable object).</summary>
    public string? Message { get; set; }
}

/// <summary>
/// The file-writing half of the export tools: path/format/field validation, the output stream,
/// cursor paging, the status sidecar and the result DTO. Both backends run through this, so a
/// Mock export and a live export are the same file layout and the same contract.
///
/// The row source stays with the backend, because only it knows how to walk a model.
/// </summary>
public static class ExportRunner
{
    /// <summary>Rows written between flushes. Bounds data loss if the host dies mid-export.</summary>
    private const int FlushEvery = 2000;

    /// <summary>
    /// Run an export. <paramref name="rows"/> receives the parsed cursor offset and a state
    /// object it must update (<see cref="ExportScanState.ScannedObjects"/>, and
    /// <see cref="ExportScanState.StoppedEarly"/> when it hit <paramref name="maxObjects"/>).
    /// Never throws — failures land in <see cref="ExportResult.Message"/>.
    /// </summary>
    public static ExportResult Run(
        FilePathPolicy policy,
        string? path,
        string? format,
        bool append,
        string? cursor,
        int? maxObjects,
        string backendName,
        IReadOnlyList<string> fieldNames,
        DataRow prototype,
        Func<long, ExportScanState, IEnumerable<DataRow>> rows,
        double? maxSeconds = null)
    {
        var result = new ExportResult
        {
            Backend = backendName,
            Appended = append,
            Path = (path ?? "").Trim(),
        };

        foreach (var name in fieldNames) result.Fields.Add(name);

        if (!DataFileWriters.TryNormalizeFormat(format, out var normalizedFormat, out var formatError))
        {
            result.Message = formatError;
            return result;
        }
        result.Format = normalizedFormat;

        if (!Aggregation.TryParseCursor(cursor, out var skip, out var cursorError))
        {
            result.Message = cursorError;
            return result;
        }

        if (!policy.TryResolveForWrite(path, out var fullPath, out var pathError))
        {
            result.Message = pathError;
            return result;
        }
        result.Path = fullPath;

        // A CSV header belongs at the top of the file only — never again mid-append.
        var existingLength = SafeFileLength(fullPath);
        var writeHeader = normalizedFormat == DataFileWriters.Csv && !(append && existingLength > 0);

        var stopwatch = Stopwatch.StartNew();
        var state = new ExportScanState();
        var outOfTime = false;

        try
        {
            // CSV gets a BOM so Excel reads Cyrillic correctly; JSONL stays plain UTF-8 for the
            // offline tooling. .NET emits the preamble only at stream position 0, so appending
            // never injects one mid-file.
            var encoding = normalizedFormat == DataFileWriters.Csv
                ? new UTF8Encoding(true)
                : new UTF8Encoding(false);

            using (var stream = new FileStream(
                       fullPath,
                       append ? FileMode.Append : FileMode.Create,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 1 << 16))
            using (var textWriter = new StreamWriter(stream, encoding))
            {
                textWriter.NewLine = "\n";
                using (var writer = DataFileWriters.Create(textWriter, normalizedFormat, prototype, writeHeader))
                {
                    foreach (var row in rows(skip, state))
                    {
                        writer.Write(row);
                        result.Written++;
                        if (result.Written % FlushEvery == 0) writer.Flush();

                        // Time budget: stop BETWEEN objects when the page is running longer than
                        // the caller allowed. This bounds a page whose per-object cost was
                        // guessed wrong — it cannot bound a single Open API call that never
                        // returns, because that never gives control back to this loop.
                        if (maxSeconds is double budget && budget > 0 &&
                            stopwatch.Elapsed.TotalSeconds >= budget)
                        {
                            state.StoppedEarly = true;
                            outOfTime = true;
                            break;
                        }
                    }
                    writer.Flush();
                }
            }
        }
        catch (Exception ex)
        {
            result.Seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
            result.ScannedObjects = state.ScannedObjects;
            result.Bytes = SafeFileLength(fullPath);
            // Truncated stays FALSE on purpose: a failure must stop the agent's paging loop and
            // reach the user, not be retried as if it were a normal partial page. The resume
            // point is still reported, so the work done so far need not be thrown away.
            result.NextCursor = (skip + state.ScannedObjects).ToString(CultureInfo.InvariantCulture);
            result.Message = Aggregation.Append(state.Message,
                "Export failed after " + result.Written + " rows: " + ErrorText.Flatten(ex) +
                " Rows written before the failure are in the file; nextCursor is where it stopped.");
            result.StatusPath = TryWriteStatusSidecar(fullPath, result);
            return result;
        }

        result.Seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        result.ScannedObjects = state.ScannedObjects;
        result.Bytes = SafeFileLength(fullPath);
        result.Truncated = state.StoppedEarly;
        result.Message = state.Message;

        if (state.StoppedEarly)
        {
            result.NextCursor = (skip + state.ScannedObjects).ToString(CultureInfo.InvariantCulture);
            var reason = outOfTime
                ? $"the {maxSeconds:0.#} s time budget (maxSeconds)"
                : "maxObjects";
            result.Message = Aggregation.Append(result.Message,
                $"Partial page: stopped after {state.ScannedObjects} source objects on {reason}. " +
                "Repeat with cursor=nextCursor, append=true and the SAME filters/fields to continue " +
                "into the same file.");
        }
        else if (skip > 0 && state.ScannedObjects == 0)
        {
            result.Message = Aggregation.Append(result.Message,
                "Cursor points at or beyond the end of the source — nothing left to export.");
        }

        result.StatusPath = TryWriteStatusSidecar(fullPath, result);
        return result;
    }

    /// <summary>
    /// Mirror the counters into "&lt;path&gt;.status.json". MCP clients abort a request after
    /// ~60 s and the reply is lost even when the server finished the work — the sidecar is how
    /// the user (or the offline matching step) can still tell a completed export from a
    /// half-written one.
    /// </summary>
    private static string? TryWriteStatusSidecar(string exportPath, ExportResult result)
    {
        var statusPath = FilePathPolicy.StatusSidecarPath(exportPath);
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\"path\":");
            AppendJsonString(sb, exportPath);
            sb.Append(",\"format\":");
            AppendJsonString(sb, result.Format);
            sb.Append(",\"writtenThisCall\":").Append(result.Written.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"scannedObjects\":").Append(result.ScannedObjects.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"bytes\":").Append(result.Bytes.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"truncated\":").Append(result.Truncated ? "true" : "false");
            sb.Append(",\"appended\":").Append(result.Appended ? "true" : "false");
            sb.Append(",\"nextCursor\":");
            if (result.NextCursor is null) sb.Append("null"); else AppendJsonString(sb, result.NextCursor);
            sb.Append(",\"seconds\":")
              .Append(result.Seconds.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",\"backend\":");
            AppendJsonString(sb, result.Backend);
            sb.Append(",\"message\":");
            if (result.Message is null) sb.Append("null"); else AppendJsonString(sb, result.Message);
            sb.Append(",\"completedUtc\":");
            AppendJsonString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"fields\":[");
            for (var i = 0; i < result.Fields.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendJsonString(sb, result.Fields[i]);
            }
            sb.Append("]}");

            File.WriteAllText(statusPath, sb.ToString(), new UTF8Encoding(false));
            return statusPath;
        }
        catch
        {
            // The sidecar is a convenience, never a reason to fail an export that succeeded.
            return null;
        }
    }

    private static void AppendJsonString(StringBuilder sb, string? value)
    {
        sb.Append('"');
        foreach (var c in value ?? "")
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        sb.Append('"');
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}
