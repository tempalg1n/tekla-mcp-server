using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.FileExchange;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// End-to-end exercise of the exchange tools against the mock backend: export → file → import.
/// The mock runs the SAME ExportRunner/UdaImportRunner as live Tekla, so these lock down the
/// paging contract, the preview/apply gate and the non-empty guard for both backends.
/// </summary>
public class FileExchangeWorkflowTests : IDisposable
{
    private readonly string _root;
    private readonly MockTeklaModelService _model;

    public FileExchangeWorkflowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "teklamcp-exchange-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _model = new MockTeklaModelService { FilePolicyOverride = new FilePathPolicy(new[] { _root }) };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    // ------------------------------------------------------------------- export ----

    [Fact]
    public void ExportWritesRowsToDiskAndKeepsThemOutOfTheResponse()
    {
        var path = PathFor("parts.jsonl");
        var result = _model.ExportObjectsToFile(
            new ObjectQuery(),
            new ExportRequest { Path = path, Fields = { "guid", "type", "solidAabb" } });

        Assert.Null(result.Message);
        Assert.Equal(path, result.Path);
        Assert.True(result.Written > 0);
        Assert.True(result.Bytes > 0);
        Assert.False(result.Truncated);
        Assert.Null(result.NextCursor);
        Assert.Equal(new[] { "guid", "type", "solidAabb" }, result.Fields);

        var lines = File.ReadAllLines(path);
        Assert.Equal(result.Written, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("{\"guid\":", line));
    }

    [Fact]
    public void ExportWritesAStatusSidecarSoATimedOutCallCanStillBeVerified()
    {
        var path = PathFor("parts.jsonl");
        var result = _model.ExportObjectsToFile(new ObjectQuery(), new ExportRequest { Path = path });

        Assert.Equal(FilePathPolicy.StatusSidecarPath(path), result.StatusPath);
        var status = File.ReadAllText(result.StatusPath!);
        Assert.Contains("\"truncated\":false", status);
        Assert.Contains("\"writtenThisCall\":" + result.Written, status);
        Assert.Contains("\"completedUtc\":", status);
    }

    [Fact]
    public void PagingCoversDisjointSlicesAndAppendsIntoOneFile()
    {
        var path = PathFor("paged.jsonl");
        var whole = _model.ExportObjectsToFile(new ObjectQuery(), new ExportRequest { Path = PathFor("whole.jsonl") });

        var page = _model.ExportObjectsToFile(
            new ObjectQuery(), new ExportRequest { Path = path, MaxObjects = 2 });
        Assert.True(page.Truncated);
        Assert.Equal("2", page.NextCursor);
        Assert.Contains("cursor=nextCursor", page.Message);

        var written = page.Written;
        var cursor = page.NextCursor;
        var guard = 0;
        while (cursor != null && guard++ < 50)
        {
            var next = _model.ExportObjectsToFile(
                new ObjectQuery(),
                new ExportRequest { Path = path, MaxObjects = 2, Cursor = cursor, Append = true });
            written += next.Written;
            cursor = next.NextCursor;
        }

        Assert.Equal(whole.Written, written);
        Assert.Equal(whole.Written, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void CsvExportWritesExactlyOneHeaderAcrossAppendedPages()
    {
        var path = PathFor("paged.csv");
        var first = _model.ExportObjectsToFile(
            new ObjectQuery(),
            new ExportRequest { Path = path, Format = "csv", MaxObjects = 2, Fields = { "guid", "type" } });
        Assert.True(first.Truncated);

        _model.ExportObjectsToFile(
            new ObjectQuery(),
            new ExportRequest
            {
                Path = path, Format = "csv", MaxObjects = 2,
                Cursor = first.NextCursor, Append = true, Fields = { "guid", "type" },
            });

        var lines = File.ReadAllLines(path);
        Assert.Equal("guid,type", lines[0]);
        Assert.Single(lines, l => l == "guid,type");
    }

    [Fact]
    public void UdaIsEmptySelectsTheNotYetProcessedObjects()
    {
        var path = PathFor("todo.jsonl");
        var result = _model.ExportObjectsToFile(
            new ObjectQuery { UdaName = "USER_FIELD_1", UdaIsEmpty = true },
            new ExportRequest { Path = path, Fields = { "guid", "uda:USER_FIELD_1" } });

        Assert.True(result.Written > 0);
        Assert.All(File.ReadAllLines(path), line => Assert.Contains("\"USER_FIELD_1\":null", line));

        // The complement must be non-empty too, or the filter proves nothing.
        var approved = _model.ExportObjectsToFile(
            new ObjectQuery { UdaName = "USER_FIELD_1", UdaEquals = "Approved" },
            new ExportRequest { Path = PathFor("approved.jsonl"), Fields = { "guid" } });
        Assert.True(approved.Written > 0);
    }

    [Fact]
    public void PathOutsideTheAllowedRootsIsRefusedAndNothingIsWritten()
    {
        var outside = Path.Combine(Path.GetTempPath(), "teklamcp-outside-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var result = _model.ExportObjectsToFile(new ObjectQuery(), new ExportRequest { Path = outside });

        Assert.Contains("outside the allowed roots", result.Message);
        Assert.Equal(0, result.Written);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void TimeBudgetStopsThePageBetweenObjectsAndReportsACursor()
    {
        // Guards a page whose per-object cost was guessed wrong. It stops BETWEEN objects — it
        // cannot interrupt one Tekla call that never returns, which is why the reference
        // export's face query is off by default rather than merely time-boxed.
        var path = PathFor("slow.jsonl");
        var prototype = new DataRow();
        prototype.Add("guid", RowValue.Text(null));

        var result = ExportRunner.Run(
            new FilePathPolicy(new[] { _root }),
            path, "jsonl", append: false, cursor: null, maxObjects: null,
            backendName: "Test", fieldNames: new[] { "guid" }, prototype: prototype,
            rows: (skip, state) => SlowRows(state),
            maxSeconds: 0.15);

        Assert.True(result.Truncated);
        Assert.NotNull(result.NextCursor);
        Assert.Contains("maxSeconds", result.Message);
        Assert.True(result.Written > 0, "at least one row should have made it to the file");
        Assert.True(result.Written < 500, $"the budget should have stopped the scan, wrote {result.Written}");
        Assert.Equal(result.Written, File.ReadAllLines(path).Length);
    }

    private static IEnumerable<DataRow> SlowRows(ExportScanState state)
    {
        var row = new DataRow();
        for (var i = 0; i < 500; i++)
        {
            state.ScannedObjects++;
            System.Threading.Thread.Sleep(5);
            row.Clear();
            row.Add("guid", RowValue.Text("g" + i));
            yield return row;
        }
    }

    [Fact]
    public void UnknownFieldFailsBeforeTouchingTheDisk()
    {
        var path = PathFor("bad.jsonl");
        var result = _model.ExportObjectsToFile(
            new ObjectQuery(), new ExportRequest { Path = path, Fields = { "nonsense" } });

        Assert.Contains("Unknown field", result.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ReferenceExportHonorsTheEntityFilter()
    {
        var path = PathFor("kmd.jsonl");
        var all = _model.ExportReferenceObjectsToFile(new ReferenceExportRequest { Path = path });
        Assert.True(all.Written > 0);

        var filtered = _model.ExportReferenceObjectsToFile(new ReferenceExportRequest
        {
            Path = PathFor("kmd-beams.jsonl"),
            EntityFilter = { "IFCBEAM", "IFCCOLUMN" },
        });

        Assert.True(filtered.Written < all.Written);
        Assert.All(
            File.ReadAllLines(PathFor("kmd-beams.jsonl")),
            line => Assert.True(line.Contains("\"IFCBEAM\"") || line.Contains("\"IFCCOLUMN\"")));
        Assert.Contains("placementOrigin", File.ReadAllText(PathFor("kmd-beams.jsonl")));
    }

    // ------------------------------------------------------------------- import ----

    [Fact]
    public void PreviewCountsTheWorkWithoutChangingTheModel()
    {
        var target = FirstObjectWithoutStatus();
        var path = WriteImportFile("in.jsonl", (target.Guid, "Согласованно"));

        var preview = _model.SetUdasFromFile(new UdaFileWriteRequest { Path = path });

        Assert.False(preview.Applied);
        Assert.Equal(1, preview.RowsRead);
        Assert.Equal(1, preview.Matched);
        Assert.Equal(1, preview.Updated);
        Assert.Equal(1, preview.UpdatedFields);
        Assert.Contains("PREVIEW ONLY", preview.Message);
        Assert.Equal(new[] { "USER_FIELD_1" }, preview.Fields);
        Assert.Equal("update", Assert.Single(preview.Sample).Action);
        Assert.Null(ReadStatus(target.Guid));
    }

    [Fact]
    public void ApplyWritesTheValuesAndIsIdempotent()
    {
        var target = FirstObjectWithoutStatus();
        var path = WriteImportFile("in.jsonl", (target.Guid, "Согласованно"));

        var applied = _model.SetUdasFromFile(new UdaFileWriteRequest { Path = path, Apply = true });
        Assert.True(applied.Applied);
        Assert.Equal(1, applied.Updated);
        Assert.Equal("Согласованно", ReadStatus(target.Guid));

        // Second run: the value already matches, so it is neither an update nor a skip.
        var again = _model.SetUdasFromFile(new UdaFileWriteRequest { Path = path, Apply = true });
        Assert.Equal(0, again.Updated);
        Assert.Equal(1, again.Unchanged);
        Assert.Equal(0, again.SkippedNonEmpty);
    }

    [Fact]
    public void AValueAHumanAlreadySetIsNotOverwrittenByDefault()
    {
        var target = FirstObjectWithStatus();
        var original = ReadStatus(target.Guid);
        var path = WriteImportFile("in.jsonl", (target.Guid, "KXM_AI"));

        var guarded = _model.SetUdasFromFile(new UdaFileWriteRequest { Path = path, Apply = true });
        Assert.Equal(0, guarded.Updated);
        Assert.Equal(1, guarded.SkippedNonEmpty);
        Assert.Equal("skip-non-empty", Assert.Single(guarded.Sample).Action);
        Assert.Equal(original, ReadStatus(target.Guid));

        var forced = _model.SetUdasFromFile(
            new UdaFileWriteRequest { Path = path, Apply = true, OverwriteNonEmpty = true });
        Assert.Equal(1, forced.Updated);
        Assert.Equal("KXM_AI", ReadStatus(target.Guid));
    }

    [Fact]
    public void MissingGuidsAreSkippedOrFailTheRunOnRequest()
    {
        var target = FirstObjectWithoutStatus();
        var path = WriteImportFile(
            "in.jsonl",
            ("11111111-2222-3333-4444-555555555555", "x"),
            (target.Guid, "Согласованно"));

        var skipped = _model.SetUdasFromFile(new UdaFileWriteRequest { Path = path });
        Assert.Equal(1, skipped.NotFound);
        Assert.Equal(1, skipped.Matched);

        var failed = _model.SetUdasFromFile(new UdaFileWriteRequest { Path = path, OnMissing = "fail" });
        Assert.Equal(1, failed.NotFound);
        Assert.Equal(0, failed.Matched);
        Assert.Contains("onMissing='fail'", failed.Message);
    }

    [Fact]
    public void MalformedRowsAreCountedAndLocatedNotSilentlyDropped()
    {
        var target = FirstObjectWithoutStatus();
        var path = PathFor("in.jsonl");
        File.WriteAllText(path, "not json\n{\"guid\":\"" + target.Guid + "\",\"USER_FIELD_1\":\"ok\"}\n");

        var result = _model.SetUdasFromFile(new UdaFileWriteRequest { Path = path });

        Assert.Equal(1, result.InvalidRows);
        Assert.Equal(1, result.Updated);
        Assert.Contains("line 1", result.Message);
    }

    [Fact]
    public void ImportPagesThroughTheFileWithACursor()
    {
        var targets = _model.GetAllObjects().Take(4).Select(o => (o.Guid, "KXM_AI")).ToArray();
        var path = WriteImportFile("in.jsonl", targets);

        var first = _model.SetUdasFromFile(new UdaFileWriteRequest { Path = path, MaxObjects = 2 });
        Assert.True(first.Truncated);
        Assert.Equal("2", first.NextCursor);
        Assert.Equal(2, first.RowsRead);

        var second = _model.SetUdasFromFile(
            new UdaFileWriteRequest { Path = path, MaxObjects = 2, Cursor = first.NextCursor });
        Assert.Equal(2, second.RowsRead);
        Assert.False(second.Truncated);
    }

    [Fact]
    public void UdaColumnsNarrowsWhatGetsWritten()
    {
        var target = FirstObjectWithoutStatus();
        var path = PathFor("in.jsonl");
        File.WriteAllText(
            path,
            "{\"guid\":\"" + target.Guid + "\",\"USER_FIELD_1\":\"ok\",\"USER_FIELD_2\":\"noise\"}\n");

        var result = _model.SetUdasFromFile(new UdaFileWriteRequest
        {
            Path = path,
            Apply = true,
            UdaColumns = { "USER_FIELD_1" },
        });

        Assert.Equal(new[] { "USER_FIELD_1" }, result.Fields);
        Assert.Equal("ok", ReadStatus(target.Guid));
        Assert.Empty(_model.GetObjectUdas(target.Guid, new[] { "USER_FIELD_2" }).Udas);
    }

    [Fact]
    public void ExportedFileFeedsStraightBackIntoTheImport()
    {
        // The round trip the reconciliation actually performs: export candidates, decide
        // offline, write the decision back.
        var exportPath = PathFor("todo.jsonl");
        var export = _model.ExportObjectsToFile(
            new ObjectQuery { UdaName = "USER_FIELD_1", UdaIsEmpty = true },
            new ExportRequest { Path = exportPath, Fields = { "guid", "solidAabb" } });
        Assert.True(export.Written > 0);

        var decisions = ReadGuids(exportPath).Select(guid => (guid, "Согласованно")).ToArray();
        var importPath = WriteImportFile("decided.jsonl", decisions);

        var applied = _model.SetUdasFromFile(new UdaFileWriteRequest { Path = importPath, Apply = true });

        Assert.Equal(export.Written, applied.Matched);
        Assert.Equal(export.Written, applied.Updated);
        Assert.Equal(0, applied.NotFound);
        Assert.Equal(0, applied.InvalidRows);
    }

    // ------------------------------------------------------------------ helpers ----

    private ModelObjectInfo FirstObjectWithoutStatus() =>
        _model.GetAllObjects().First(o => string.IsNullOrEmpty(ReadStatus(o.Guid)));

    private ModelObjectInfo FirstObjectWithStatus() =>
        _model.GetAllObjects().First(o => !string.IsNullOrEmpty(ReadStatus(o.Guid)));

    private string? ReadStatus(string guid) =>
        _model.GetObjectUdas(guid, new[] { "USER_FIELD_1" }).Udas.TryGetValue("USER_FIELD_1", out var value)
            ? value
            : null;

    private string WriteImportFile(string name, params (string Guid, string Status)[] rows)
    {
        var path = PathFor(name);
        var lines = rows.Select(r =>
            "{\"guid\":\"" + r.Guid + "\",\"USER_FIELD_1\":\"" + r.Status + "\"}");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>Read an exported file back through the shared reader, as the offline step would.</summary>
    private static List<string> ReadGuids(string path)
    {
        using var reader = new StreamReader(path);
        return new DataFileReader(DataFileWriters.Jsonl, "guid")
            .Read(reader)
            .Select(row =>
            {
                Assert.Null(row.Error);
                return row.Guid;
            })
            .ToList();
    }
}
