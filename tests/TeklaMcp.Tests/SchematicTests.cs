using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core.Models;
using TeklaMcp.Core.Rendering;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// tekla_render_schematic and the mock tekla_capture_view: the picture must show what the legend
/// says where the mapping says, coverage must be honest, and the mock must never pass its
/// stand-in off as a Tekla screenshot.
/// </summary>
public class SchematicTests
{
    private static SchematicItem Line(string guid, bool focus, string cls, params (double X, double Y, double Z)[] points) =>
        new SchematicItem
        {
            Guid = guid,
            Id = guid.GetHashCode() & 0xFFFF,
            Type = "Beam",
            Class = cls,
            Profile = "HEA300",
            Focus = focus,
            Shape = "line",
            GeometrySource = "reference-line",
            Points = points.Select(p => new Point3D(p.X, p.Y, p.Z)).ToList(),
        };

    [Fact]
    public void Focus_is_drawn_in_its_class_colour_at_the_mapped_pixel()
    {
        var scene = new SchematicScene
        {
            Items =
            {
                Line("focus-1", true, "2", (0, 0, 0), (6000, 0, 0)),
                Line("ctx-1", false, "3", (0, 3000, 0), (6000, 3000, 0)),
            },
        };

        var r = SchematicRenderer.Render(scene, new SchematicRenderOptions { View = "top", ShowGrids = false });

        Assert.True(r.Rendered, r.Message);
        Assert.Equal(1, r.FocusDrawn);
        Assert.Equal(1, r.ContextDrawn);
        Assert.Equal("#D62728", r.Colors["class 2"]);
        Assert.Equal("#AEB3B9", r.Colors["context"]);
        var entry = Assert.Single(r.Legend);
        Assert.Equal("1", entry.Label);
        Assert.Equal("focus-1", entry.Guid);
        Assert.True(entry.LabelPlaced);

        // Invert "X = x0 + px*m; Y = y0 - py*m" for the midpoint of the focus beam (3000, 0): the
        // label's anchor must be there, and the pixel there must carry the beam's colour.
        var image = PngTestReader.Decode(r.Image!.Bytes);
        var (px, py) = ToPixel(r, 3000, 0);
        Assert.InRange(entry.X!.Value, px - 1, px + 1);
        Assert.InRange(entry.Y!.Value, py - 1, py + 1);
        Assert.Equal((0xD6, 0x27, 0x28), image.At(entry.X.Value, entry.Y.Value));
        Assert.Equal(r.Width, image.Width);
    }

    [Fact]
    public void Region_is_the_frame_even_when_members_run_through_it()
    {
        // Live 3219 slice: a 30 m beam crossing a 6 m region widened the frame to the whole beam.
        var scene = new SchematicScene
        {
            Items = { Line("long", false, "3", (0, 0, 0), (100000, 0, 0)), Line("short", false, "2", (42000, 0, 0), (42000, 0, 4000)) },
            Region = new Box3D(new Point3D(40000, -1000, -1000), new Point3D(46000, 1000, 5000)),
        };
        var r = SchematicRenderer.Render(scene, new SchematicRenderOptions { View = "front", ShowGrids = false });

        Assert.True(r.Rendered);
        Assert.Equal("beta", r.Stage);
        Assert.InRange(r.Framed!.Min.X, 40000, 46000);
        Assert.InRange(r.Framed.Max.X, 40000, 46000);
        Assert.True(r.MmPerPixel < 10, "the 6 m region should fill the picture, got " + r.MmPerPixel + " mm/px");
    }

    [Fact]
    public void Column_seen_end_on_is_still_visible()
    {
        var scene = new SchematicScene { Items = { Line("col", true, "2", (1000, 1000, 0), (1000, 1000, 4000)) } };
        var r = SchematicRenderer.Render(scene, new SchematicRenderOptions { View = "top", ShowGrids = false, Labels = "none" });

        Assert.Equal(1, r.FocusDrawn);
        var image = PngTestReader.Decode(r.Image!.Bytes);
        var (px, py) = ToPixel(r, 1000, 1000);
        Assert.Equal((0xD6, 0x27, 0x28), image.At(px, py));
        Assert.False(Assert.Single(r.Legend).LabelPlaced);
        Assert.Equal(0, r.LabelsOmitted);
    }

    [Fact]
    public void Label_cap_is_reported_not_hidden()
    {
        var scene = new SchematicScene
        {
            Items =
            {
                Line("a", true, "2", (0, 0, 0), (6000, 0, 0)),
                Line("b", true, "2", (0, 2000, 0), (6000, 2000, 0)),
                Line("c", true, "2", (0, 4000, 0), (6000, 4000, 0)),
            },
        };
        var r = SchematicRenderer.Render(scene, new SchematicRenderOptions { View = "top", MaxLabels = 1 });

        Assert.Equal(1, r.LabelsPlaced);
        Assert.Equal(2, r.LabelsOmitted);
        Assert.Equal(3, r.Legend.Count);
        Assert.Contains(r.Warnings, w => w.Contains("could not be labelled"));
    }

    [Fact]
    public void Highlight_colour_overrides_the_class()
    {
        var scene = new SchematicScene { Items = { Line("a", true, "3", (0, 0, 0), (6000, 0, 0)) } };
        var r = SchematicRenderer.Render(scene, new SchematicRenderOptions { View = "top", HighlightColor = "#E00000" });

        Assert.Equal("#E00000", r.Colors["focus"]);
        Assert.False(r.Colors.ContainsKey("class 3"));
    }

    [Fact]
    public void Bad_view_and_empty_scene_fail_with_a_reason()
    {
        var scene = new SchematicScene { Items = { Line("a", true, "2", (0, 0, 0), (1, 0, 0)) } };
        var badView = SchematicRenderer.Render(scene, new SchematicRenderOptions { View = "sideways" });
        Assert.False(badView.Rendered);
        Assert.Contains("Unknown view", badView.Message);

        var empty = SchematicRenderer.Render(new SchematicScene(), new SchematicRenderOptions());
        Assert.False(empty.Rendered);
        Assert.Null(empty.Image);
        Assert.Contains("Nothing to draw", empty.Message);
    }

    [Fact]
    public void Unknown_options_fall_back_with_a_warning()
    {
        var scene = new SchematicScene { Items = { Line("a", true, "2", (0, 0, 0), (6000, 0, 0)) } };
        var r = SchematicRenderer.Render(scene, new SchematicRenderOptions { Labels = "emoji", ColorBy = "mood" });

        Assert.True(r.Rendered);
        Assert.Equal("class", r.ColorBy);
        Assert.Contains(r.Warnings, w => w.StartsWith("labels 'emoji'"));
        Assert.Contains(r.Warnings, w => w.StartsWith("colorBy 'mood'"));
    }

    // ------------------------------------------------------------------------ mock scene ---

    [Fact]
    public void Mock_overview_draws_every_part_and_the_grid()
    {
        var mock = new MockTeklaModelService();
        var scene = mock.GetSchematicScene(new SchematicSceneRequest());

        Assert.DoesNotContain(scene.Items, i => i.Focus);
        Assert.DoesNotContain(scene.Items, i => i.Type == "Bolt");
        Assert.Equal(mock.GetAllObjects().Count(o => o.Type != "Bolt"), scene.Items.Count);
        var grid = Assert.Single(scene.Grids);
        Assert.Equal(new[] { "А", "Б", "Д" }, grid.LinesY.Select(l => l.Label));
        Assert.Equal(3, grid.Levels.Count);

        var r = SchematicRenderer.Render(scene, new SchematicRenderOptions());
        Assert.True(r.Rendered);
        Assert.Empty(r.Legend);
        Assert.True(r.GridLinesDrawn > 0);
    }

    [Fact]
    public void Mock_focus_by_filter_collects_context_around_it()
    {
        var mock = new MockTeklaModelService();
        var scene = mock.GetSchematicScene(new SchematicSceneRequest
        {
            Query = new ObjectQuery { NameContains = "BRACE" },
            ContextMarginMm = 500,
        });

        Assert.Equal(4, scene.Items.Count(i => i.Focus));
        Assert.Equal(4, scene.FocusMatched);
        Assert.NotNull(scene.Region);
        Assert.Contains(scene.Items, i => !i.Focus);
        Assert.All(scene.Items.Where(i => !i.Focus),
            i => Assert.True(SchematicSceneTools.Intersects(SchematicSceneTools.BoundsOf(i)!, scene.Region!)));

        var r = SchematicRenderer.Render(scene, new SchematicRenderOptions { View = "front" });
        Assert.Equal(new[] { "1", "2", "3", "4" }, r.Legend.Select(e => e.Label));
        Assert.StartsWith("X = ", r.PixelToModel);
        Assert.Contains("Z = ", r.PixelToModel);
    }

    [Fact]
    public void Mock_region_limits_the_focus()
    {
        var mock = new MockTeklaModelService();
        var scene = mock.GetSchematicScene(new SchematicSceneRequest
        {
            Query = new ObjectQuery { NameContains = "BRACE" },
            Region = new Box3D(new Point3D(-500, -500, -100), new Point3D(500, 6500, 4100)),
        });

        // BR4 runs along x = 6000 and never enters the region.
        Assert.Equal(3, scene.FocusMatched);
        Assert.Equal(3, scene.Items.Count(i => i.Focus));
        Assert.DoesNotContain(scene.Items, i => i.Focus && i.Points.All(p => p.X > 5000));
    }

    [Fact]
    public void Mock_reports_missing_guids_caps_and_bolts()
    {
        var mock = new MockTeklaModelService();
        var existing = mock.GetAllObjects().First(o => o.Type == "Beam").Guid;
        var scene = mock.GetSchematicScene(new SchematicSceneRequest
        {
            Query = new ObjectQuery { GuidIn = new List<string> { existing, "11111111-2222-3333-4444-555555555555" } },
        });
        Assert.Equal(new[] { "11111111-2222-3333-4444-555555555555" }, scene.MissingGuids);
        Assert.Single(scene.Items, i => i.Focus);

        var capped = mock.GetSchematicScene(new SchematicSceneRequest
        {
            Query = new ObjectQuery { Type = "Beam" },
            MaxFocusObjects = 2,
            IncludeContext = false,
        });
        Assert.True(capped.FocusTruncated);
        Assert.Equal(2, capped.Items.Count);

        var withBolts = mock.GetSchematicScene(new SchematicSceneRequest { IncludeBolts = true });
        Assert.Contains(withBolts.Items, i => i.Shape == "points" && i.GeometrySource == "bolt-positions");
    }

    [Fact]
    public void Mock_arch_is_drawn_through_its_apex()
    {
        var mock = new MockTeklaModelService();
        var scene = mock.GetSchematicScene(new SchematicSceneRequest { Query = new ObjectQuery { Type = "PolyBeam" } });
        var arch = Assert.Single(scene.Items, i => i.Focus);

        Assert.Equal("centerline", arch.GeometrySource);
        Assert.Contains(arch.Points, p => System.Math.Abs(p.X - 3000) < 1 && System.Math.Abs(p.Z - 5000) < 1);
    }

    [Fact]
    public void Mock_capture_is_a_labelled_stand_in()
    {
        var mock = new MockTeklaModelService();
        var guids = mock.GetAllObjects().Where(o => o.Name == "COLUMN").Take(2).Select(o => o.Guid).ToList();

        var result = mock.CaptureView(new ViewCaptureRequest { Guids = guids, Labels = true, Format = "jpeg" });

        Assert.True(result.Captured);
        Assert.Equal("mock-schematic", result.Source);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(2, result.TargetsFound);
        Assert.Equal(guids, result.Legend.Select(l => l.Guid));
        Assert.False(result.HighlightApplied);
        Assert.False(result.CameraChanged);
        Assert.Contains(result.Warnings, w => w.Contains("not a Tekla viewport"));
        Assert.Contains(result.Warnings, w => w.Contains("PNG"));
        Assert.Equal(result.Width, PngTestReader.Decode(result.Image!.Bytes).Width);
    }

    [Fact]
    public void Mock_capture_of_an_unknown_view_lists_the_open_ones()
    {
        var result = new MockTeklaModelService().CaptureView(new ViewCaptureRequest { ViewName = "Section A-A" });

        Assert.False(result.Captured);
        Assert.Null(result.Image);
        Assert.Contains("'Section A-A' is not open", result.Message);
        Assert.Contains("3D", result.Message);
    }

    [Fact]
    public void Mock_grid_resolution_is_unchanged()
    {
        var p = new MockTeklaModelService().ResolvePoint("2", "Б", 4000);
        Assert.True(p.Resolved);
        Assert.Equal(6000, p.X);
        Assert.Equal(6000, p.Y);
        Assert.Equal(4000, p.Z);
    }

    /// <summary>Invert the axis-view formula "H = h0 + px*m; V = v0 - py*m" from the result.</summary>
    private static (int X, int Y) ToPixel(SchematicRenderResult r, double h, double v)
    {
        var parts = r.PixelToModel.Split(';');
        double Number(string s, int index) =>
            double.Parse(s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)[index], CultureInfo.InvariantCulture);
        var h0 = Number(parts[0], 2);
        var v0 = Number(parts[1], 2);
        var m = r.MmPerPixel;
        return ((int)System.Math.Floor((h - h0) / m), (int)System.Math.Floor((v0 - v) / m));
    }
}
