using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.FileExchange;

/// <summary>Identity of a resolved object, for the preview sample.</summary>
public sealed class UdaImportIdentity
{
    public string Guid { get; set; } = "";
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string? AssemblyPos { get; set; }
}

/// <summary>
/// The model side of a bulk UDA import. Implemented by each backend; the runner drives it and
/// stays free of Tekla types. Handles are opaque on purpose — the live backend passes a
/// <c>ModelObject</c>, the mock passes its DTO, and Core never has to know.
/// </summary>
public interface IUdaImportTarget
{
    /// <summary>Resolve a GUID to an object handle, or null when the model has no such object.</summary>
    object? Resolve(string guid);

    /// <summary>Identity of a resolved handle, for the sample rows.</summary>
    UdaImportIdentity Describe(object handle);

    /// <summary>Current UDA value, or null when the attribute is not set on the object.</summary>
    string? ReadUda(object handle, string name);

    /// <summary>Write the given UDA values. Returns how many fields were actually written.</summary>
    int WriteUdas(object handle, IReadOnlyList<KeyValuePair<string, string>> values);

    /// <summary>Called once after an applied batch (the live backend commits the model here).</summary>
    void Commit();
}

/// <summary>
/// The file-reading half of <c>tekla_set_udas_from_file</c>: parsing, column selection, the
/// non-empty guard, preview/apply and cursor paging. Shared by both backends so preview
/// counters mean the same thing everywhere.
/// </summary>
public static class UdaImportRunner
{
    /// <summary>How many decisions come back for the user to eyeball.</summary>
    private const int SampleLimit = 10;

    /// <summary>Never throws — failures land in <see cref="UdaFileWriteResult.Message"/>.</summary>
    public static UdaFileWriteResult Run(
        FilePathPolicy policy,
        UdaFileWriteRequest request,
        string backendName,
        IUdaImportTarget target)
    {
        var result = new UdaFileWriteResult
        {
            Applied = request.Apply,
            Backend = backendName,
            // Every early return below happens before the first write.
            Outcome = request.Apply ? WriteOutcome.NotWritten : WriteOutcome.Planned,
        };

        if (!DataFileReader.TryResolveFormat(request.Format, request.Path, out var format, out var formatError))
        {
            result.Message = formatError;
            return result;
        }

        var failOnMissing = string.Equals((request.OnMissing ?? "").Trim(), "fail", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.OnMissing) && !failOnMissing &&
            !string.Equals(request.OnMissing!.Trim(), "skip", StringComparison.OrdinalIgnoreCase))
        {
            result.Message = $"Unsupported onMissing '{request.OnMissing}'. Use 'skip' (default) or 'fail'.";
            return result;
        }

        if (!Aggregation.TryParseCursor(request.Cursor, out var skip, out var cursorError))
        {
            result.Message = cursorError;
            return result;
        }

        if (!policy.TryResolveForRead(request.Path, out var fullPath, out var pathError))
        {
            result.Message = pathError;
            return result;
        }

        var allowedColumns = new HashSet<string>(
            (request.UdaColumns ?? new List<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var writtenFields = new List<string>();
        var writtenFieldSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();
        var applied = false;
        var progress = new WriteProgress();
        var refusedObjects = 0L;

        try
        {
            var reader = new DataFileReader(format, request.GuidColumn);
            using (var file = new StreamReader(fullPath, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                foreach (var row in reader.Read(file))
                {
                    if (row.RecordIndex < skip) continue;
                    if (request.MaxObjects is int cap && cap > 0 && result.RowsRead >= cap)
                    {
                        result.Truncated = true;
                        break;
                    }
                    result.RowsRead++;

                    if (row.Error != null)
                    {
                        result.InvalidRows++;
                        AddSample(result, new UdaFileWritePreview
                        {
                            Guid = row.Guid,
                            Action = "invalid",
                        }, $"line {row.LineNumber}: {row.Error}");
                        continue;
                    }

                    var requested = row.Values
                        .Where(v => allowedColumns.Count == 0 || allowedColumns.Contains(v.Key))
                        .ToList();

                    var handle = target.Resolve(row.Guid);
                    if (handle is null)
                    {
                        result.NotFound++;
                        AddSample(result, new UdaFileWritePreview { Guid = row.Guid, Action = "not-found" }, null);
                        if (failOnMissing)
                        {
                            result.Message = Aggregation.Append(result.Message,
                                $"Stopped at line {row.LineNumber}: GUID '{row.Guid}' is not in the model " +
                                "(onMissing='fail'). Nothing further was processed.");
                            break;
                        }
                        continue;
                    }

                    result.Matched++;
                    var identity = target.Describe(handle);
                    var preview = new UdaFileWritePreview
                    {
                        Guid = string.IsNullOrEmpty(identity.Guid) ? row.Guid : identity.Guid,
                        Id = identity.Id,
                        Type = identity.Type,
                        AssemblyPos = identity.AssemblyPos,
                    };

                    var changes = new List<KeyValuePair<string, string>>();
                    var skippedHere = 0;

                    foreach (var pair in requested)
                    {
                        var name = pair.Key.Trim();
                        if (name.Length == 0) continue;
                        if (writtenFieldSet.Add(name)) writtenFields.Add(name);

                        var newValue = pair.Value ?? "";
                        var current = target.ReadUda(handle, name);

                        // Already correct: not a write, not a skip — genuinely nothing to do.
                        if (string.Equals(current ?? "", newValue, StringComparison.Ordinal)) continue;

                        // A value a human already put there is not the agent's to overwrite.
                        if (!request.OverwriteNonEmpty && !string.IsNullOrWhiteSpace(current))
                        {
                            skippedHere++;
                            result.SkippedNonEmpty++;
                            continue;
                        }

                        changes.Add(new KeyValuePair<string, string>(name, newValue));
                        preview.Changes.Add(new UdaFieldChange { Name = name, From = current, To = newValue });
                    }

                    if (changes.Count == 0)
                    {
                        if (skippedHere > 0) preview.Action = "skip-non-empty";
                        else { result.Unchanged++; preview.Action = "unchanged"; }
                        AddSample(result, preview, null);
                        continue;
                    }

                    preview.Action = "update";
                    AddSample(result, preview, null);

                    if (!request.Apply)
                    {
                        result.Updated++;
                        result.UpdatedFields += changes.Count;
                        continue;
                    }

                    // Counted only once Tekla took it: a count bumped before the write used to
                    // claim the object that broke the run as updated.
                    progress.BeginWrite();
                    var written = target.WriteUdas(handle, changes);
                    applied = true;
                    if (written > 0)
                    {
                        result.Updated++;
                        result.UpdatedFields += written;
                    }
                    else
                    {
                        refusedObjects++;
                        preview.Action = "refused";
                    }
                }
            }

            if (applied) target.Commit();
            progress.Complete();
        }
        catch (Exception ex)
        {
            progress.Fail(ex);
            result.Outcome = progress.Outcome(request.Apply, result.Updated, refusedObjects);
            result.Seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
            result.Message = Aggregation.Append(result.Message, "Import failed: " + ErrorText.Flatten(ex));
            if (result.Outcome == WriteOutcome.Unknown)
                result.Message = Aggregation.Append(result.Message,
                    "The run broke off after writing began: the object being written when it failed may " +
                    "or may not hold the new values. Re-run this page with apply=false to see what is " +
                    "still missing before applying again.");
            return result;
        }

        result.Outcome = progress.Outcome(request.Apply, result.Updated, refusedObjects);
        if (refusedObjects > 0)
            result.Message = Aggregation.Append(result.Message,
                $"{refusedObjects} object(s) accepted none of their UDA values (action 'refused' in the " +
                "sample) — check the field names and types.");

        result.Seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        result.Fields = writtenFields;

        if (result.Truncated)
        {
            result.NextCursor = (skip + result.RowsRead).ToString(CultureInfo.InvariantCulture);
            result.Message = Aggregation.Append(result.Message,
                $"Partial page: stopped after {result.RowsRead} rows (maxObjects). Repeat with " +
                "cursor=nextCursor and the SAME arguments to continue through the file.");
        }
        else if (skip > 0 && result.RowsRead == 0)
        {
            result.Message = Aggregation.Append(result.Message,
                "Cursor points at or beyond the end of the file — no rows left to process.");
        }

        if (!request.Apply)
        {
            result.Message = Aggregation.Append(result.Message,
                "PREVIEW ONLY — nothing was written. Re-run with apply=true to commit these counts.");
        }

        return result;
    }

    private static void AddSample(UdaFileWriteResult result, UdaFileWritePreview preview, string? note)
    {
        if (note != null) result.Message = Aggregation.Append(result.Message, note);
        if (result.Sample.Count < SampleLimit) result.Sample.Add(preview);
    }
}
