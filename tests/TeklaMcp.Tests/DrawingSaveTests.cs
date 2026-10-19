using System.Collections.Generic;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>Committed is not saved (issue #41): open-drawing writes say so until a save.</summary>
public class DrawingSaveTests
{
    [Fact]
    public void Open_drawing_writes_are_unsaved_until_a_save()
    {
        var model = new MockTeklaModelService();
        var text = new DrawingObjectSpec
        {
            Kind = "text", ViewId = 11001, ViewId2 = 0, Text = "NOTE",
            Points = new List<Point3D> { new Point3D(1, 2, 0) },
        };

        var preview = model.CreateDrawingObjects(new[] { text }, apply: false);
        Assert.Null(preview.DrawingSaved);

        var written = model.CreateDrawingObjects(new[] { text }, apply: true);
        Assert.False(written.DrawingSaved);
        Assert.Contains(DrawingSaveTracker.UnsavedWarning, written.Warnings);
        model.CreateDrawingObjects(new[] { text }, apply: true);
        Assert.Equal(2, model.GetDrawingStatus().UnsavedMcpWrites);

        var saved = model.SaveActiveDrawing(apply: true);
        Assert.True(saved.DrawingSaved);
        Assert.Equal(0, model.GetDrawingStatus().UnsavedMcpWrites);
    }

    [Fact]
    public void A_write_that_changed_nothing_is_not_counted()
    {
        var tracker = new DrawingSaveTracker();
        var result = new DrawingWriteResult { Applied = true };
        tracker.Committed(result);
        Assert.Equal(0, tracker.UnsavedWrites);
        Assert.Null(result.DrawingSaved);
    }
}
