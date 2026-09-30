using TeklaMcp.Core;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// tekla_save_model / tekla_open_model on the mock (backlog §5). The contract that matters: opening
/// another model never silently discards unsaved changes, and the switch is visible everywhere a
/// write target is read — so expectedModelPath protects every later write.
/// </summary>
public class ModelLifecycleTests
{
    [Fact]
    public void Save_preview_changes_nothing()
    {
        var mock = new MockTeklaModelService();
        mock.MarkUnsaved();
        var preview = mock.SaveModel("check", apply: false);

        Assert.Equal(WriteOutcome.Planned, preview.Outcome);
        Assert.False(preview.ModelSavedBefore);
        Assert.False(mock.SaveModel(null, apply: false).ModelSavedBefore);
    }

    [Fact]
    public void Save_apply_saves()
    {
        var mock = new MockTeklaModelService();
        mock.MarkUnsaved();
        var saved = mock.SaveModel("after plugin rebuild", apply: true);

        Assert.Equal(WriteOutcome.Committed, saved.Outcome);
        Assert.True(saved.ModelSavedAfter);
    }

    [Fact]
    public void Unsaved_changes_block_opening_another_model()
    {
        var mock = new MockTeklaModelService();
        mock.MarkUnsaved();
        var refused = mock.OpenModel("/virtual/mock/TestModel", false, discardUnsavedChanges: false, apply: true);

        Assert.Equal(WriteOutcome.NotWritten, refused.Outcome);
        Assert.Contains("unsaved changes", refused.Message);
        Assert.Equal("/virtual/mock/MockModel", mock.GetWriteTarget().ModelPath);
    }

    [Fact]
    public void Open_switches_the_write_target()
    {
        var mock = new MockTeklaModelService();
        var opened = mock.OpenModel("/virtual/mock/TestModel/", false, false, apply: true);

        Assert.Equal(WriteOutcome.Committed, opened.Outcome);
        Assert.Equal("/virtual/mock/TestModel", mock.GetWriteTarget().ModelPath);
        Assert.Equal("TestModel", mock.GetConnectionInfo().ModelName);
        Assert.True(ModelPaths.Same("/virtual/mock/TestModel/TestModel.db1", mock.GetWriteTarget().ModelPath));
    }

    [Fact]
    public void Discarding_is_explicit()
    {
        var mock = new MockTeklaModelService();
        mock.MarkUnsaved();
        var opened = mock.OpenModel("/virtual/mock/TestModel", false, discardUnsavedChanges: true, apply: true);
        Assert.Equal(WriteOutcome.Committed, opened.Outcome);
    }
}
