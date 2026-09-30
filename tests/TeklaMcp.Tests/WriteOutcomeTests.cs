using System;
using System.Collections.Generic;
using System.IO;
using TeklaMcp.Core;
using TeklaMcp.Core.FileExchange;
using TeklaMcp.Core.Models;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Backlog §4: every write result says what happened to the model. The rule that matters most:
/// a failure after the first write began is "unknown" — never "not written", never "rolled back".
/// </summary>
public class WriteOutcomeTests : IDisposable
{
    private static Exception LostConnection() =>
        new System.Runtime.Remoting.RemotingException("Failed to write to an IPC port");

    [Fact]
    public void Preview_is_planned_whatever_happened()
    {
        var progress = new WriteProgress();
        progress.BeginWrite();
        progress.Fail(LostConnection());
        Assert.Equal(WriteOutcome.Planned, progress.Outcome(apply: false, written: 0, failedItems: 0));
    }

    [Fact]
    public void Failure_before_the_first_write_is_not_written()
    {
        var progress = new WriteProgress();
        progress.Fail(LostConnection()); // e.g. the connection was already gone at GetConnectedModel
        Assert.Equal(WriteOutcome.NotWritten, progress.Outcome(true, 0, 0));
    }

    [Fact]
    public void Failure_between_first_write_and_commit_is_unknown_even_with_nothing_counted()
    {
        // The case FailIfNothingApplied used to call "no objects were written".
        var progress = new WriteProgress();
        progress.BeginWrite();
        progress.Fail(new InvalidOperationException("CommitChanges failed"));
        Assert.Equal(WriteOutcome.Unknown, progress.Outcome(true, written: 0, failedItems: 0));
    }

    [Fact]
    public void Failure_after_the_commit_leaves_the_write_standing()
    {
        // Reading the result back failed; the commit had already returned.
        var progress = new WriteProgress();
        progress.BeginWrite();
        progress.Complete();
        progress.Fail(LostConnection());
        Assert.Equal(WriteOutcome.Committed, progress.Outcome(true, written: 3, failedItems: 0));
    }

    [Fact]
    public void Lost_connection_on_an_item_makes_the_batch_unknown()
    {
        var progress = new WriteProgress();
        progress.BeginWrite();
        progress.ItemFailed(LostConnection());
        progress.Complete();
        Assert.Equal(WriteOutcome.Unknown, progress.Outcome(true, written: 2, failedItems: 1));
    }

    [Fact]
    public void Refused_items_next_to_written_ones_are_partial()
    {
        var progress = new WriteProgress();
        progress.BeginWrite();
        progress.ItemFailed(new InvalidOperationException("Tekla rejected Modify()"));
        progress.Complete();
        Assert.Equal(WriteOutcome.Partial, progress.Outcome(true, written: 4, failedItems: 1));
    }

    [Fact]
    public void Every_item_refused_is_not_written()
    {
        var progress = new WriteProgress();
        progress.BeginWrite();
        progress.ItemFailed(new InvalidOperationException("Insert returned false"));
        progress.Complete();
        Assert.Equal(WriteOutcome.NotWritten, progress.Outcome(true, written: 0, failedItems: 2));
    }

    [Fact]
    public void Clean_batch_is_committed()
    {
        var progress = new WriteProgress();
        progress.BeginWrite();
        progress.Complete();
        Assert.Equal(WriteOutcome.Committed, progress.Outcome(true, written: 5, failedItems: 0));
    }

    [Theory]
    [InlineData(false, 3, 0, WriteOutcome.Planned)]
    [InlineData(true, 0, 0, WriteOutcome.NotWritten)]
    [InlineData(true, 0, 2, WriteOutcome.NotWritten)]
    [InlineData(true, 2, 0, WriteOutcome.Committed)]
    [InlineData(true, 2, 1, WriteOutcome.Partial)]
    public void Counters_alone_never_claim_unknown(bool applied, long written, long failed, string expected)
    {
        Assert.Equal(expected, WriteOutcome.FromCounts(applied, written, failed));
    }

    // ------------------------------------------------------------ model paths ----

    [Theory]
    [InlineData(@"V:\3219\3219_Model", @"V:\3219\3219_Model")]
    [InlineData(@"v:\3219\3219_model\", @"V:\3219\3219_Model")]
    [InlineData("V:/3219/3219_Model", @"V:\3219\3219_Model\")]
    [InlineData(@"""V:\3219\3219_Model""", @"V:\3219\3219_Model")]
    [InlineData(@"V:\3219\3219_Model\3219_Model.db1", @"V:\3219\3219_Model")]
    public void Same_model_path_in_any_spelling(string expected, string actual)
    {
        Assert.True(ModelPaths.Same(expected, actual));
    }

    [Theory]
    [InlineData(@"V:\3219\3219_Model_playground", @"V:\3219\3219_Model")]
    [InlineData(@"V:\3219", @"V:\3219\3219_Model")]
    [InlineData(@"V:\3155\3155_Model_V3\3155_Model_V3.db1", @"V:\3219\3219_Model")]
    [InlineData("", @"V:\3219\3219_Model")]
    [InlineData(@"V:\3219\3219_Model", "")]
    public void Near_misses_are_different_models(string expected, string actual)
    {
        Assert.False(ModelPaths.Same(expected, actual));
    }

    // ------------------------------------------------- bulk UDA import runner ----

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "teklamcp-outcome-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private UdaFileWriteResult Import(FakeTarget target, bool apply)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "udas.jsonl");
        File.WriteAllLines(path, new[]
        {
            "{\"guid\":\"a\",\"USER_FIELD_1\":\"OK\"}",
            "{\"guid\":\"b\",\"USER_FIELD_1\":\"OK\"}",
            "{\"guid\":\"c\",\"USER_FIELD_1\":\"OK\"}",
        });
        return UdaImportRunner.Run(
            new FilePathPolicy(new[] { _root }),
            new UdaFileWriteRequest { Path = path, Apply = apply },
            "Test",
            target);
    }

    [Fact]
    public void Import_preview_is_planned()
    {
        var result = Import(new FakeTarget(), apply: false);
        Assert.Equal(WriteOutcome.Planned, result.Outcome);
        Assert.Equal(3, result.Updated);
    }

    [Fact]
    public void Import_that_breaks_mid_file_is_unknown_and_does_not_count_the_broken_object()
    {
        var result = Import(new FakeTarget { ThrowOn = "b" }, apply: true);

        Assert.Equal(WriteOutcome.Unknown, result.Outcome);
        Assert.Equal(1, result.Updated); // "a" only — "b" broke the run before it was confirmed
        Assert.Contains("apply=false", result.Message);
    }

    [Fact]
    public void Import_with_a_refused_object_is_partial()
    {
        var result = Import(new FakeTarget { RefuseOn = "c" }, apply: true);

        Assert.Equal(WriteOutcome.Partial, result.Outcome);
        Assert.Equal(2, result.Updated);
        Assert.Contains(result.Sample, s => s.Guid == "c" && s.Action == "refused");
    }

    [Fact]
    public void Clean_import_is_committed()
    {
        var target = new FakeTarget();
        var result = Import(target, apply: true);

        Assert.Equal(WriteOutcome.Committed, result.Outcome);
        Assert.Equal(3, result.Updated);
        Assert.True(target.Committed);
    }

    [Fact]
    public void Import_refused_before_reading_the_file_is_not_written()
    {
        var result = UdaImportRunner.Run(
            new FilePathPolicy(new[] { _root }),
            new UdaFileWriteRequest { Path = Path.Combine(_root, "x.jsonl"), Apply = true, OnMissing = "explode" },
            "Test",
            new FakeTarget());
        Assert.Equal(WriteOutcome.NotWritten, result.Outcome);
    }

    private sealed class FakeTarget : IUdaImportTarget
    {
        public string? ThrowOn { get; set; }
        public string? RefuseOn { get; set; }
        public bool Committed { get; private set; }

        public object? Resolve(string guid) => guid;

        public UdaImportIdentity Describe(object handle) => new UdaImportIdentity { Guid = (string)handle, Type = "Beam" };

        public string? ReadUda(object handle, string name) => null;

        public int WriteUdas(object handle, IReadOnlyList<KeyValuePair<string, string>> values)
        {
            if ((string)handle == ThrowOn) throw new System.Runtime.Remoting.RemotingException("IPC port gone");
            return (string)handle == RefuseOn ? 0 : values.Count;
        }

        public void Commit() => Committed = true;
    }
}
