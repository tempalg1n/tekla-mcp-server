using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

public class DrawingSheetTests
{
    [Fact]
    public void Active_sheet_reports_actual_and_layout_dimensions()
    {
        var model = new MockTeklaModelService();
        var active = model.GetDrawingStatus().ActiveDrawing;

        var sheet = model.GetDrawingSheet();

        Assert.True(sheet.Available);
        Assert.Equal("Mock", sheet.Backend);
        Assert.Equal(active?.Key, sheet.DrawingKey);
        Assert.Equal(841, sheet.Width);
        Assert.Equal(594, sheet.Height);
        Assert.Equal(841, sheet.LayoutSheetWidth);
        Assert.Equal(594, sheet.LayoutSheetHeight);
        Assert.Equal("SpecifiedSize", sheet.SizeDefinitionMode);
        Assert.NotNull(sheet.Origin);
        Assert.NotNull(sheet.FrameOrigin);
        Assert.Null(sheet.Message);
    }

    [Fact]
    public void Sheet_reports_absent_after_active_drawing_is_closed()
    {
        var model = new MockTeklaModelService();
        var closed = model.CloseActiveDrawing(save: true, apply: true);
        Assert.Equal(1, closed.ModifiedCount);

        var sheet = model.GetDrawingSheet();

        Assert.False(sheet.Available);
        Assert.Equal("Mock", sheet.Backend);
        Assert.Empty(sheet.DrawingKey);
        Assert.Null(sheet.Width);
        Assert.Contains("No active drawing", sheet.Message);
    }
}
