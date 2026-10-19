using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>Section-view semantics measured on Tekla 2021 (issue #34).</summary>
public class SectionViewsTests
{
    [Fact]
    public void A_cut_drawn_upwards_looks_toward_minus_x()
    {
        var up = SectionViews.LookDirection(new Point3D(0, 0, 0), new Point3D(0, 500, 0));
        Assert.Equal((-1.0, 0.0), (up!.X, up.Y));
        var down = SectionViews.LookDirection(new Point3D(0, 500, 0), new Point3D(0, 0, 0));
        Assert.Equal((1.0, 0.0), (down!.X, System.Math.Abs(down.Y)));
        Assert.Null(SectionViews.LookDirection(new Point3D(1, 1, 0), new Point3D(1, 1, 0)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, 10.0)]
    [InlineData(200.0, 0.0)]
    [InlineData(200.0, -5.0)]
    public void Zero_or_missing_depths_are_refused(double? up, double? down) =>
        Assert.Contains("BEHIND the cut plane", SectionViews.CheckDepths(up, down));

    [Fact]
    public void Positive_depths_pass_and_are_described()
    {
        Assert.Null(SectionViews.CheckDepths(200, 10));
        var text = SectionViews.Describe(new Point3D(0, 0, 0), new Point3D(0, 500, 0), 200, 10);
        Assert.Contains("z ∈ [-200, 10]", text);
        Assert.Contains("(-1, 0)", text);
    }
}
