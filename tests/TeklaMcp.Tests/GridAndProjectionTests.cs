using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using TeklaMcp.Core.Rendering;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Grid semantics (verified live on Tekla 2023, model 3219) and the view convention shared by the
/// schematic and the live camera: a view is the direction the camera LOOKS along.
/// </summary>
public class GridAndProjectionTests
{
    // ---------------------------------------------------------------------- projection ---

    [Theory]
    [InlineData("top", 1, 0, 0, 0, 1, 0)]
    [InlineData("plan", 1, 0, 0, 0, 1, 0)]
    [InlineData("front", 1, 0, 0, 0, 0, 1)]
    [InlineData("back", -1, 0, 0, 0, 0, 1)]
    [InlineData("left", 0, -1, 0, 0, 0, 1)]
    [InlineData("right", 0, 1, 0, 0, 0, 1)]
    [InlineData("bottom", -1, 0, 0, 0, 1, 0)]
    public void Axis_views_have_the_expected_screen_axes(string view, double rx, double ry, double rz, double ux, double uy, double uz)
    {
        Assert.True(ViewProjection.TryCreate(view, out var p, out var error), error);
        AssertVector(rx, ry, rz, p.Right);
        AssertVector(ux, uy, uz, p.Up);
        Assert.NotNull(p.AxisAligned());
    }

    [Fact]
    public void Iso_looks_from_south_east_above()
    {
        Assert.True(ViewProjection.TryCreate("iso", out var p, out _));
        AssertVector(-0.57735, 0.57735, -0.57735, p.Forward);
        AssertVector(0.70711, 0.70711, 0, p.Right);
        AssertVector(-0.40825, 0.40825, 0.8165, p.Up);
        Assert.Null(p.AxisAligned());
    }

    [Fact]
    public void Custom_direction_is_the_viewing_direction_like_tekla_camera()
    {
        Assert.True(ViewProjection.TryCreate("0,0,-1", out var custom, out _));
        Assert.True(ViewProjection.TryCreate("top", out var top, out _));
        Assert.Equal("custom", custom.Name);
        AssertVector(top.Right.X, top.Right.Y, top.Right.Z, custom.Right);
        AssertVector(top.Up.X, top.Up.Y, top.Up.Z, custom.Up);

        // The live "3D" view camera (Tekla 2023, 3219) looked along (-0.3662,-0.9297,0.0393): the
        // derived up vector must point up, as Tekla's own (0.0144,0.0365,0.9992) does.
        Assert.True(ViewProjection.TryCreate("-0.3662,-0.9297,0.0393", out var live, out _));
        Assert.True(live.Up.Z > 0.99);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("0,0,0")]
    [InlineData("1,2")]
    public void Bad_views_are_rejected_with_the_options(string view)
    {
        Assert.False(ViewProjection.TryCreate(view, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ------------------------------------------------------------------------- parsing ---

    [Fact]
    public void Xy_coordinates_are_spacings_with_repeats()
    {
        // Model 3219 main grid: "0.00 18*6000.00" = axes 1..19 at 0 … 108000.
        var xs = GridMath.ParsePositions("0.00 18*6000.00", relative: true);
        Assert.Equal(19, xs.Count);
        Assert.Equal(108000, xs.Last());

        Assert.Equal(new double[] { 0, 6000, 12000, 15000 }, GridMath.ParsePositions("0 6000 6000 3000", relative: true));
        Assert.Equal(new double[] { 0, 6000, 12000, 18000, 24000 }, GridMath.ParsePositions("4*6000", relative: true));
        Assert.Equal(new double[] { 0, 5500 }, GridMath.ParsePositions("0,00 5500,00", relative: true));
    }

    [Fact]
    public void Z_coordinates_are_absolute_levels()
    {
        // Model 3219: "0.00 200.00 300.00 …" is labelled "0.000 +0.200 +0.300 …".
        Assert.Equal(new double[] { 0, 200, 300, 400 }, GridMath.ParsePositions("0.00 200.00 300.00 400.00", relative: false));
        Assert.Equal(new double[] { -300, 0, 6000 }, GridMath.ParsePositions("-300.00 0.00 6000.00", relative: false));
    }

    [Fact]
    public void Labels_come_from_tekla_and_fall_back_only_when_missing()
    {
        Assert.Equal(new[] { "А", "Б" }, GridMath.SplitLabels("А Б "));
        var axis = GridMath.BuildAxis(new double[] { 0, 6000, 12000 }, new[] { "A1", "A2" }, "Y");
        Assert.Equal(new[] { "A1", "A2", "В" }, axis.Select(a => a.Label));
        Assert.Equal("+3.300", GridMath.FallbackLabel("Z", 0, 3300));
    }

    // ------------------------------------------------------------------------- flatten ---

    private static SchematicGrid Grid(int id, Point3D origin, string xs, string xl, string ys, string yl,
        Point3D? axisX = null, Point3D? axisY = null) =>
        new SchematicGrid
        {
            Id = id,
            Origin = origin,
            AxisX = axisX ?? new Point3D(1, 0, 0),
            AxisY = axisY ?? new Point3D(0, 1, 0),
            LinesX = GridMath.BuildAxis(GridMath.ParsePositions(xs, true), GridMath.SplitLabels(xl), "X"),
            LinesY = GridMath.BuildAxis(GridMath.ParsePositions(ys, true), GridMath.SplitLabels(yl), "Y"),
        };

    [Fact]
    public void Grid_origin_moves_its_lines()
    {
        // Model 3219's second grid sits at (-4950, -7250, -260).
        var grid = Grid(2, new Point3D(-4950, -7250, -260), "0.00 2*4750.00", "1 2 3", "0.00 5500.00", "А Б");
        grid.Levels = GridMath.BuildAxis(GridMath.ParsePositions("-300.00 0.00 6000.00", false),
            GridMath.SplitLabels("-0,300 0,000 +6,000"), "Z");

        var lines = GridMath.Flatten(new[] { grid });

        Assert.Equal(new double[] { -4950, -200, 4550 }, lines.Where(l => l.Axis == "X").Select(l => l.Coordinate));
        Assert.Equal(new double[] { -7250, -1750 }, lines.Where(l => l.Axis == "Y").Select(l => l.Coordinate));
        Assert.Equal(new double[] { -560, -260, 5740 }, lines.Where(l => l.Axis == "Z").Select(l => l.Coordinate));
        Assert.All(lines, l => Assert.Equal(2, l.GridId));
        var first = lines.First(l => l.Axis == "X");
        Assert.Equal(-7250, first.Start!.Y);
        Assert.Equal(-1750, first.End!.Y);
        Assert.False(first.Rotated);
    }

    [Fact]
    public void Resolve_prefers_one_grid_and_says_when_labels_repeat()
    {
        var main = Grid(1, new Point3D(0, 0, 0), "0 18*6000", "1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19", "0 4*6000", "А Б В Г Д");
        var small = Grid(2, new Point3D(-4950, -7250, -260), "0 2*4750", "1 2 3", "0 5500", "А Б");
        var lines = GridMath.Flatten(new[] { main, small });

        var p = GridMath.Resolve(lines, "19", "Д", 4200);
        Assert.True(p.Resolved);
        Assert.Equal(108000, p.X);
        Assert.Equal(24000, p.Y);
        Assert.Null(p.Message);

        var ambiguous = GridMath.Resolve(lines, "2", "Б", 0);
        Assert.True(ambiguous.Resolved);
        Assert.Equal(6000, ambiguous.X);
        Assert.Equal(6000, ambiguous.Y);
        Assert.Contains("several grids", ambiguous.Message);

        var missing = GridMath.Resolve(lines, "99", "Б", 0);
        Assert.False(missing.Resolved);
        Assert.Equal("Grid label not found (X='99': False, Y='Б': True).", missing.Message);
    }

    [Fact]
    public void Rotated_grid_resolves_by_intersecting_its_lines()
    {
        // Local X along global +Y, local Y along global −X.
        var grid = Grid(7, new Point3D(1000, 2000, 0), "0 5000", "1 2", "0 3000", "A B",
            new Point3D(0, 1, 0), new Point3D(-1, 0, 0));
        var lines = GridMath.Flatten(new[] { grid });
        Assert.All(lines, l => Assert.True(l.Rotated));

        var p = GridMath.Resolve(lines, "2", "B", 0);
        Assert.True(p.Resolved);
        Assert.Equal(1000 - 3000, p.X, 3);
        Assert.Equal(2000 + 5000, p.Y, 3);
    }

    private static void AssertVector(double x, double y, double z, Vec3 actual)
    {
        Assert.Equal(x, actual.X, 4);
        Assert.Equal(y, actual.Y, 4);
        Assert.Equal(z, actual.Z, 4);
    }
}
