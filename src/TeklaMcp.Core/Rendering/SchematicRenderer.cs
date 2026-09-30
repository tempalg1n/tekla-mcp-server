using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Rendering;

/// <summary>
/// Draws a <see cref="SchematicScene"/> into a PNG an agent can look at: an orthographic
/// projection with focus objects in colour and numbered, context in grey, grid axes with their
/// real labels, levels in elevation views, coordinate ticks for axis views, a scale bar and an
/// axis triad. The JSON half (<see cref="SchematicRenderResult"/>) maps every number back to a
/// GUID and every pixel back to model millimetres.
///
/// Geometry is deliberately simple — reference lines, contours, bolt points, boxes — so the
/// picture answers "where is what and what touches what", not "what does the detail look like"
/// (that is tekla_capture_view).
/// </summary>
public static class SchematicRenderer
{
    private const int TitleHeight = 28;
    private const int BannerHeight = 24;
    private const int FooterHeight = 48;
    private const int SideMargin = 28;
    private const int LabelScale = 2;

    private static readonly Rgb Background = Rgb.FromHex(0xFFFFFF);
    private static readonly Rgb Band = Rgb.FromHex(0xF1F3F5);
    private static readonly Rgb BandLine = Rgb.FromHex(0xD0D5DA);
    private static readonly Rgb TextDark = Rgb.FromHex(0x1B1F23);
    private static readonly Rgb TextMuted = Rgb.FromHex(0x55606B);
    private static readonly Rgb GridColor = Rgb.FromHex(0x5B84B8);
    private static readonly Rgb LevelColor = Rgb.FromHex(0x5E8F5E);
    private static readonly Rgb ContextColor = Rgb.FromHex(0xAEB3B9);
    private static readonly Rgb BannerColor = Rgb.FromHex(0xC62828);
    private static readonly Rgb HandleStart = Rgb.FromHex(0xE0B400);
    private static readonly Rgb HandleEnd = Rgb.FromHex(0xC000C0);
    private static readonly Rgb White = Rgb.FromHex(0xFFFFFF);
    private static readonly Rgb SingleColor = Rgb.FromHex(0x1F5FD6);

    // Tekla's default colours for classes 1–7, darkened where pure yellow/cyan vanish on white.
    private static readonly Dictionary<string, Rgb> ClassColors = new Dictionary<string, Rgb>
    {
        ["1"] = Rgb.FromHex(0x8A8A8A), // grey
        ["2"] = Rgb.FromHex(0xD62728), // red
        ["3"] = Rgb.FromHex(0x2CA02C), // green
        ["4"] = Rgb.FromHex(0x1F5FD6), // blue
        ["5"] = Rgb.FromHex(0x0FA3B5), // cyan
        ["6"] = Rgb.FromHex(0xC9A100), // yellow
        ["7"] = Rgb.FromHex(0xB02DB0), // magenta
    };

    // For everything else: distinct from the class 1–7 colours above, so "red" always means class 2.
    private static readonly Rgb[] Qualitative =
    {
        Rgb.FromHex(0xFF7F0E), Rgb.FromHex(0x9467BD), Rgb.FromHex(0x8C564B), Rgb.FromHex(0xE377C2),
        Rgb.FromHex(0x17BECF), Rgb.FromHex(0xBCBD22), Rgb.FromHex(0x393B79), Rgb.FromHex(0x843C39),
        Rgb.FromHex(0x637939), Rgb.FromHex(0x5254A3), Rgb.FromHex(0xE6550D), Rgb.FromHex(0x7B4173),
    };

    private static readonly string[] LabelModes = { "number", "id", "mark", "profile", "name", "none" };
    private static readonly string[] ColorModes = { "class", "type", "profile", "material", "none" };

    private sealed class Drawable
    {
        public SchematicItem Item = null!;
        public string Shape = "line";
        public List<List<(double U, double V)>> Paths = new List<List<(double U, double V)>>();
        public List<bool> Closed = new List<bool>();
        public List<(double U, double V)> Markers = new List<(double U, double V)>();
        public List<Vec3> World = new List<Vec3>();
        public double Depth;
        public Rgb Color;
        public string Category = "";
    }

    private sealed class Frame
    {
        public double Scale; // px per mm
        public double U0, V0, Cx, Cy;
        public int Left, Top, Right, Bottom;

        public (double X, double Y) ToPx(double u, double v) => (Cx + (u - U0) * Scale, Cy - (v - V0) * Scale);

        public bool Inside(double x, double y, double pad = 0) =>
            x >= Left - pad && x <= Right + pad && y >= Top - pad && y <= Bottom + pad;
    }

    public static SchematicRenderResult Render(SchematicScene scene, SchematicRenderOptions? options = null)
    {
        scene = scene ?? new SchematicScene();
        options = options ?? new SchematicRenderOptions();
        var result = new SchematicRenderResult
        {
            Backend = scene.Backend,
            FocusMatched = scene.FocusMatched,
            FocusTruncated = scene.FocusTruncated,
            ContextTruncated = scene.ContextTruncated,
            ScannedObjects = scene.ScannedObjects,
            ScanTruncated = scene.ScanTruncated,
            SkippedNoGeometry = scene.SkippedNoGeometry,
            MissingGuids = new List<string>(scene.MissingGuids),
            Warnings = new List<string>(scene.Warnings),
            Message = scene.Message,
        };

        var width = Clamp(options.Width, 320, 2400);
        var height = Clamp(options.Height, 240, 2400);
        var labelMode = Normalize(options.Labels, LabelModes, "number", result, "labels");
        var colorBy = Normalize(options.ColorBy, ColorModes, "class", result, "colorBy");
        result.ColorBy = colorBy;

        if (!ViewProjection.TryCreate(options.View, out var projection, out var viewError))
        {
            result.Message = viewError;
            return result;
        }
        result.View = projection.Name;

        // --- project -------------------------------------------------------------------------
        var drawables = new List<Drawable>();
        foreach (var item in scene.Items)
        {
            var d = Prepare(item, projection);
            if (d != null) drawables.Add(d);
        }

        var focus = drawables.Where(d => d.Item.Focus).ToList();
        var framingFocus = focus.Count > 0 && options.FitToFocus;

        List<Vec3> framePoints;
        if (framingFocus)
            framePoints = focus.SelectMany(d => d.World).ToList();
        else if (focus.Count == 0 && scene.Region != null)
            // An explicit region is the frame — long members crossing it must not widen it.
            framePoints = RegionFrame(scene.Region, drawables);
        else
            framePoints = drawables.SelectMany(d => d.World).ToList();
        if (framePoints.Count == 0 && options.ShowGrids)
            framePoints = GridFramePoints(scene.Grids).ToList();
        if (framePoints.Count == 0)
        {
            result.Message = AppendSentence(result.Message,
                "Nothing to draw: no objects with geometry matched and no grids were found.");
            return result;
        }

        // --- frame ---------------------------------------------------------------------------
        var top = TitleHeight + (string.IsNullOrWhiteSpace(options.Banner) ? 0 : BannerHeight);
        var frame = BuildFrame(framePoints, projection, width, height, top, framingFocus);
        var mmPerPixel = 1 / frame.Scale;

        // --- colours -------------------------------------------------------------------------
        var hasFocus = focus.Count > 0;
        Rgb? highlight = null;
        if (!string.IsNullOrWhiteSpace(options.HighlightColor))
        {
            if (Rgb.TryParse(options.HighlightColor, out var hc)) highlight = hc;
            else result.Warnings.Add("highlightColor '" + options.HighlightColor + "' is not #RRGGBB; ignored.");
        }
        AssignColors(drawables, colorBy, hasFocus, options.ContextColored, highlight, result);

        // --- draw ----------------------------------------------------------------------------
        var canvas = new RasterCanvas(width, height, Background);
        var occupied = new List<(double X0, double Y0, double X1, double Y1)>(); // grid bubbles + level texts
        if (options.ShowGrids) result.GridLinesDrawn = DrawGrids(canvas, scene.Grids, projection, frame, occupied);

        var context = drawables.Where(d => !d.Item.Focus).OrderByDescending(d => d.Depth).ToList();
        foreach (var d in context)
            if (DrawItem(canvas, d, frame, focusStyle: false)) result.ContextDrawn++;
        foreach (var d in focus.OrderByDescending(d => d.Depth))
        {
            if (DrawItem(canvas, d, frame, focusStyle: true)) result.FocusDrawn++;
            if (options.ShowHandles) DrawHandles(canvas, d, frame);
        }

        PlaceLabels(canvas, focus, context, frame, labelMode, options, result);

        DrawAxisTicks(canvas, projection, frame, width, occupied);
        DrawTitle(canvas, projection, mmPerPixel, result, options, width);
        DrawFooter(canvas, projection, frame, width, height, mmPerPixel);

        // --- describe ------------------------------------------------------------------------
        FillMapping(result, projection, frame, framePoints, width, height);
        result.Framed = BoxOf(framePoints);
        result.Width = width;
        result.Height = height;
        result.MmPerPixel = Math.Round(mmPerPixel, 3);
        result.Image = new RenderedImage
        {
            Bytes = PngEncoder.Encode(canvas),
            MimeType = "image/png",
            Width = width,
            Height = height,
        };
        result.Rendered = true;

        if (result.ContextTruncated)
            result.Warnings.Add("Context was capped (maxContextObjects): some surrounding parts are not drawn.");
        if (result.FocusTruncated)
            result.Warnings.Add("Focus was capped (maxObjects): not every matching object is drawn.");
        if (result.SkippedNoGeometry > 0)
            result.Warnings.Add(result.SkippedNoGeometry + " matched object(s) had no drawable geometry.");
        return result;
    }

    // ------------------------------------------------------------------------------ prepare ---

    private static Drawable? Prepare(SchematicItem item, ViewProjection projection)
    {
        var shape = (item.Shape ?? "line").Trim().ToLowerInvariant();
        var d = new Drawable { Item = item, Shape = shape };

        if (shape == "box" && item.Box != null)
        {
            var mn = item.Box.Min;
            var mx = item.Box.Max;
            if (!Finite(mn) || !Finite(mx)) return null;
            var c = new[]
            {
                new Vec3(mn.X, mn.Y, mn.Z), new Vec3(mx.X, mn.Y, mn.Z), new Vec3(mx.X, mx.Y, mn.Z), new Vec3(mn.X, mx.Y, mn.Z),
                new Vec3(mn.X, mn.Y, mx.Z), new Vec3(mx.X, mn.Y, mx.Z), new Vec3(mx.X, mx.Y, mx.Z), new Vec3(mn.X, mx.Y, mx.Z),
            };
            d.World.AddRange(c);
            d.Paths.Add(new List<(double, double)> { P(c[0]), P(c[1]), P(c[2]), P(c[3]) });
            d.Closed.Add(true);
            d.Paths.Add(new List<(double, double)> { P(c[4]), P(c[5]), P(c[6]), P(c[7]) });
            d.Closed.Add(true);
            for (var i = 0; i < 4; i++)
            {
                d.Paths.Add(new List<(double, double)> { P(c[i]), P(c[i + 4]) });
                d.Closed.Add(false);
            }
        }
        else
        {
            var pts = (item.Points ?? new List<Point3D>()).Where(Finite).Select(Vec3.FromPoint).ToList();
            if (pts.Count == 0) return null;
            d.World.AddRange(pts);
            if (shape == "points")
            {
                d.Markers.AddRange(pts.Select(P));
            }
            else
            {
                d.Paths.Add(pts.Select(P).ToList());
                d.Closed.Add(shape == "polygon" && pts.Count >= 3);
            }
        }

        d.Depth = d.World.Average(w => Vec3.Dot(w, projection.Forward));
        return d;

        (double U, double V) P(Vec3 w) => (Vec3.Dot(w, projection.Right), Vec3.Dot(w, projection.Up));
    }

    /// <summary>Corners of the region, shrunk to the drawn content when that is smaller.</summary>
    private static List<Vec3> RegionFrame(Box3D region, List<Drawable> drawables)
    {
        double x0 = region.Min.X, y0 = region.Min.Y, z0 = region.Min.Z;
        double x1 = region.Max.X, y1 = region.Max.Y, z1 = region.Max.Z;
        var world = drawables.SelectMany(d => d.World).ToList();
        if (world.Count > 0)
        {
            x0 = Math.Max(x0, world.Min(p => p.X));
            y0 = Math.Max(y0, world.Min(p => p.Y));
            z0 = Math.Max(z0, world.Min(p => p.Z));
            x1 = Math.Min(x1, world.Max(p => p.X));
            y1 = Math.Min(y1, world.Max(p => p.Y));
            z1 = Math.Min(z1, world.Max(p => p.Z));
            if (x0 > x1 || y0 > y1 || z0 > z1)
            {
                x0 = region.Min.X; y0 = region.Min.Y; z0 = region.Min.Z;
                x1 = region.Max.X; y1 = region.Max.Y; z1 = region.Max.Z;
            }
        }
        return new List<Vec3>
        {
            new Vec3(x0, y0, z0), new Vec3(x1, y0, z0), new Vec3(x1, y1, z0), new Vec3(x0, y1, z0),
            new Vec3(x0, y0, z1), new Vec3(x1, y0, z1), new Vec3(x1, y1, z1), new Vec3(x0, y1, z1),
        };
    }

    private static IEnumerable<Vec3> GridFramePoints(IEnumerable<SchematicGrid> grids)
    {
        foreach (var line in GridMath.Flatten(grids))
        {
            if (line.Start != null) yield return Vec3.FromPoint(line.Start);
            if (line.End != null) yield return Vec3.FromPoint(line.End);
        }
    }

    private static Frame BuildFrame(List<Vec3> points, ViewProjection projection, int width, int height, int top, bool focusFraming)
    {
        double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
        foreach (var p in points)
        {
            var u = Vec3.Dot(p, projection.Right);
            var v = Vec3.Dot(p, projection.Up);
            uMin = Math.Min(uMin, u);
            uMax = Math.Max(uMax, u);
            vMin = Math.Min(vMin, v);
            vMax = Math.Max(vMax, v);
        }

        var size = Math.Max(uMax - uMin, vMax - vMin);
        var pad = focusFraming ? Math.Max(size * 0.12, 600) : size * 0.03 + 200;
        uMin -= pad;
        uMax += pad;
        vMin -= pad;
        vMax += pad;
        var minExtent = focusFraming ? 2500.0 : 1000.0;
        Grow(ref uMin, ref uMax, minExtent);
        Grow(ref vMin, ref vMax, minExtent);

        var frame = new Frame
        {
            Left = SideMargin,
            Right = width - SideMargin,
            Top = top + SideMargin,
            Bottom = height - FooterHeight - 6,
        };
        var availW = Math.Max(10, frame.Right - frame.Left);
        var availH = Math.Max(10, frame.Bottom - frame.Top);
        frame.Scale = Math.Min(availW / (uMax - uMin), availH / (vMax - vMin));
        frame.U0 = (uMin + uMax) / 2;
        frame.V0 = (vMin + vMax) / 2;
        frame.Cx = (frame.Left + frame.Right) / 2.0;
        frame.Cy = (frame.Top + frame.Bottom) / 2.0;
        return frame;

        static void Grow(ref double min, ref double max, double extent)
        {
            if (max - min >= extent) return;
            var mid = (min + max) / 2;
            min = mid - extent / 2;
            max = mid + extent / 2;
        }
    }

    // ------------------------------------------------------------------------------ colours ---

    private static void AssignColors(
        List<Drawable> drawables, string colorBy, bool hasFocus, bool contextColored, Rgb? highlight,
        SchematicRenderResult result)
    {
        string CategoryOf(SchematicItem i)
        {
            switch (colorBy)
            {
                case "class": return "class " + (string.IsNullOrWhiteSpace(i.Class) ? "(none)" : i.Class.Trim());
                case "type": return string.IsNullOrWhiteSpace(i.Type) ? "(none)" : i.Type;
                case "profile": return string.IsNullOrWhiteSpace(i.Profile) ? "(none)" : i.Profile;
                case "material": return string.IsNullOrWhiteSpace(i.Material) ? "(none)" : i.Material;
                default: return "all";
            }
        }

        var colored = drawables.Where(d => d.Item.Focus || !hasFocus || contextColored).ToList();
        var categories = colored.Select(d => CategoryOf(d.Item)).Distinct()
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        var palette = new Dictionary<string, Rgb>(StringComparer.OrdinalIgnoreCase);
        var next = 0;
        foreach (var category in categories)
        {
            if (colorBy == "none") palette[category] = SingleColor;
            else if (colorBy == "class" && ClassColors.TryGetValue(category.Substring("class ".Length), out var tekla))
                palette[category] = tekla;
            else palette[category] = Qualitative[next++ % Qualitative.Length];
        }
        if (next > Qualitative.Length)
            result.Warnings.Add(next + " " + colorBy + " values share " + Qualitative.Length +
                                " colours; use 'colors' to tell them apart or narrow the scope.");

        var usedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drawables)
        {
            d.Category = CategoryOf(d.Item);
            if (d.Item.Focus && highlight.HasValue)
            {
                d.Color = highlight.Value;
            }
            else if (d.Item.Focus || !hasFocus || contextColored)
            {
                d.Color = palette[d.Category];
                usedCategories.Add(d.Category);
            }
            else
            {
                d.Color = ContextColor;
            }
        }

        if (highlight.HasValue && hasFocus) result.Colors["focus"] = highlight.Value.ToHex();
        foreach (var category in categories.Where(usedCategories.Contains))
            result.Colors[category] = palette[category].ToHex();
        if (hasFocus && !contextColored && drawables.Any(d => !d.Item.Focus))
            result.Colors["context"] = ContextColor.ToHex();
    }

    // ------------------------------------------------------------------------------ drawing ---

    private static bool DrawItem(RasterCanvas canvas, Drawable d, Frame frame, bool focusStyle)
    {
        var lineWidth = focusStyle ? 3.0 : 1.4;
        var any = false;

        for (var p = 0; p < d.Paths.Count; p++)
        {
            var path = d.Paths[p].Select(q => frame.ToPx(q.U, q.V)).ToList();
            if (path.Count == 0) continue;

            if (PixelLength(path, d.Closed[p]) < 2.5 && d.Paths.Count == 1)
            {
                // Seen end-on (a column in plan): a square instead of an invisible dot.
                var (x, y) = path[0];
                var half = focusStyle ? 5.5 : 3.5;
                if (!frame.Inside(x, y, 40)) continue;
                canvas.FillRect(x - half, y - half, x + half, y + half, d.Color, focusStyle ? 1 : 0.8);
                if (focusStyle) canvas.StrokeRect(x - half - 1, y - half - 1, x + half + 1, y + half + 1, TextDark, 0.8);
                any = true;
                continue;
            }

            if (d.Closed[p] && d.Shape == "polygon")
                canvas.FillPolygon(path, d.Color, focusStyle ? 0.28 : 0.22);

            var segments = d.Closed[p] ? path.Count : path.Count - 1;
            for (var i = 0; i < segments; i++)
            {
                var a = path[i];
                var b = path[(i + 1) % path.Count];
                canvas.DrawLine(a.X, a.Y, b.X, b.Y, d.Color, d.Shape == "box" ? lineWidth * 0.7 : lineWidth);
            }
            any = true;
        }

        foreach (var m in d.Markers)
        {
            var (x, y) = frame.ToPx(m.U, m.V);
            var r = focusStyle ? 4.5 : 2.5;
            canvas.DrawLine(x - r, y - r, x + r, y + r, d.Color, focusStyle ? 2 : 1.2);
            canvas.DrawLine(x - r, y + r, x + r, y - r, d.Color, focusStyle ? 2 : 1.2);
            any = true;
        }
        return any;
    }

    private static void DrawHandles(RasterCanvas canvas, Drawable d, Frame frame)
    {
        if (d.Shape != "line" || d.Paths.Count == 0 || d.Paths[0].Count < 2) return;
        var first = d.Paths[0][0];
        var last = d.Paths[0][d.Paths[0].Count - 1];
        var (sx, sy) = frame.ToPx(first.U, first.V);
        var (ex, ey) = frame.ToPx(last.U, last.V);
        canvas.FillCircle(sx, sy, 4.5, HandleStart);
        canvas.StrokeCircle(sx, sy, 4.5, TextDark, 1, 0.6);
        canvas.FillCircle(ex, ey, 4.5, HandleEnd);
        canvas.StrokeCircle(ex, ey, 4.5, TextDark, 1, 0.6);
    }

    private static double PixelLength(List<(double X, double Y)> path, bool closed)
    {
        var len = 0.0;
        var n = closed ? path.Count : path.Count - 1;
        for (var i = 0; i < n; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % path.Count];
            len += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        }
        return len;
    }

    // --------------------------------------------------------------------------------- grids ---

    private static int DrawGrids(
        RasterCanvas canvas, List<SchematicGrid> grids, ViewProjection projection, Frame frame,
        List<(double X0, double Y0, double X1, double Y1)> occupied)
    {
        var drawn = 0;
        var bubbles = new List<(double X, double Y, double R)>();
        var levelsDone = new HashSet<string>();
        var f = projection.Forward;

        foreach (var grid in grids)
        {
            var o = Vec3.FromPoint(grid.Origin);
            var ax = Vec3.FromPoint(GridMath.Normalize(grid.AxisX, new Point3D(1, 0, 0)));
            var ay = Vec3.FromPoint(GridMath.Normalize(grid.AxisY, new Point3D(0, 1, 0)));
            var az = Vec3.Cross(ax, ay).Normalized();

            var xs = grid.LinesX.Select(l => l.Position).ToList();
            var ys = grid.LinesY.Select(l => l.Position).ToList();
            var zs = grid.Levels.Select(l => l.Position).ToList();
            double xMin = xs.Count > 0 ? xs.Min() : 0, xMax = xs.Count > 0 ? xs.Max() : 0;
            double yMin = ys.Count > 0 ? ys.Min() : 0, yMax = ys.Count > 0 ? ys.Max() : 0;
            double zMin = zs.Count > 0 ? zs.Min() : 0, zMax = zs.Count > 0 ? zs.Max() : 0;
            var ext = Math.Max(1500, 0.05 * Math.Max(xMax - xMin, yMax - yMin));
            var extZ = Math.Max(1000, 0.1 * (zMax - zMin)); // bubbles above the top level in elevations

            Vec3 At(double x, double y, double z) => o + ax * x + ay * y + az * z;

            foreach (var line in grid.LinesX)
            {
                var facing = Math.Abs(Vec3.Dot(ax, f));
                if (facing > 0.9) continue; // plane parallel to the screen: nothing to see
                var edgeOn = facing < 0.03;
                var corners = edgeOn
                    ? new[]
                    {
                        At(line.Position, yMin - ext, zMin - extZ), At(line.Position, yMax + ext, zMin - extZ),
                        At(line.Position, yMin - ext, zMax + extZ), At(line.Position, yMax + ext, zMax + extZ),
                    }
                    : new[] { At(line.Position, yMin - ext, zMin), At(line.Position, yMax + ext, zMin) };
                if (DrawGridLine(canvas, projection, frame, corners, edgeOn, line.Label, bubbles)) drawn++;
            }
            foreach (var line in grid.LinesY)
            {
                var facing = Math.Abs(Vec3.Dot(ay, f));
                if (facing > 0.9) continue;
                var edgeOn = facing < 0.03;
                var corners = edgeOn
                    ? new[]
                    {
                        At(xMin - ext, line.Position, zMin - extZ), At(xMax + ext, line.Position, zMin - extZ),
                        At(xMin - ext, line.Position, zMax + extZ), At(xMax + ext, line.Position, zMax + extZ),
                    }
                    : new[] { At(xMin - ext, line.Position, zMin), At(xMax + ext, line.Position, zMin) };
                if (DrawGridLine(canvas, projection, frame, corners, edgeOn, line.Label, bubbles)) drawn++;
            }

            // Levels read only in elevations, where their planes are seen edge-on.
            if (Math.Abs(Vec3.Dot(az, f)) >= 0.03) continue;
            foreach (var level in grid.Levels)
            {
                var key = Math.Round(Vec3.Dot(At(0, 0, level.Position), new Vec3(0, 0, 1))).ToString(CultureInfo.InvariantCulture) + "|" + level.Label;
                if (!levelsDone.Add(key)) continue;
                var corners = new[]
                {
                    At(xMin - ext, yMin - ext, level.Position), At(xMax + ext, yMin - ext, level.Position),
                    At(xMin - ext, yMax + ext, level.Position), At(xMax + ext, yMax + ext, level.Position),
                };
                if (DrawLevel(canvas, projection, frame, corners, level.Label, occupied)) drawn++;
            }
        }
        occupied.AddRange(bubbles.Select(b => (b.X - b.R, b.Y - b.R, b.X + b.R, b.Y + b.R)));
        return drawn;
    }

    /// <summary>
    /// A grid line is a vertical plane patch. Seen edge-on it projects to a segment (plan and
    /// elevations: <paramref name="corners"/> are the patch corners); in oblique views the trace
    /// at the lowest level is drawn (<paramref name="corners"/> are its two ends).
    /// </summary>
    private static bool DrawGridLine(
        RasterCanvas canvas, ViewProjection projection, Frame frame, Vec3[] corners, bool edgeOn, string label,
        List<(double X, double Y, double R)> bubbles)
    {
        var px = corners.Select(c => frame.ToPx(Vec3.Dot(c, projection.Right), Vec3.Dot(c, projection.Up))).ToArray();
        (double X, double Y) a, b;
        if (edgeOn) (a, b) = FarthestPair(px);
        else (a, b) = (px[0], px[1]);

        // Elevations: an axis is a vertical plane, so its line runs the full height of the frame
        // and the bubble sits above the structure, as on a drawing, not inside a truss.
        if (edgeOn && Math.Abs(projection.Forward.Z) < 0.03 && Math.Abs(b.Y - a.Y) > Math.Abs(b.X - a.X) * 4)
        {
            var x = (a.X + b.X) / 2;
            a = (x, frame.Top - SideMargin);
            b = (x, frame.Bottom + 20);
        }

        double x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;
        if (!RasterCanvas.ClipSegment(ref x0, ref y0, ref x1, ref y1, frame.Left - 20, frame.Top - SideMargin, frame.Right + 20, frame.Bottom + 20))
            return false;
        canvas.DrawDashedLine(x0, y0, x1, y1, GridColor, 1.2, 12, 6, 0.85);

        // Bubble at the upper end (left end for near-horizontal lines), pulled inside the frame.
        var horizontal = Math.Abs(y1 - y0) < Math.Abs(x1 - x0) * 0.25;
        var startIsBubble = horizontal ? x0 <= x1 : y0 <= y1;
        double bx = startIsBubble ? x0 : x1, by = startIsBubble ? y0 : y1;
        double ox = startIsBubble ? x1 : x0, oy = startIsBubble ? y1 : y0;
        var (tw, th) = RasterCanvas.MeasureText(label, LabelScale);
        var r = Math.Max(12, tw / 2.0 + 6);
        var len = Math.Sqrt((ox - bx) * (ox - bx) + (oy - by) * (oy - by));
        if (len > 1e-6)
        {
            bx += (ox - bx) / len * Math.Min(r, len / 2);
            by += (oy - by) / len * Math.Min(r, len / 2);
        }
        bx = Math.Max(r + 1, Math.Min(canvas.Width - r - 1, bx));
        by = Math.Max(frame.Top - SideMargin + r + 1, Math.Min(frame.Bottom - r, by));
        if (bubbles.Any(q => Math.Sqrt((q.X - bx) * (q.X - bx) + (q.Y - by) * (q.Y - by)) < q.R + r)) return true;

        bubbles.Add((bx, by, r));
        canvas.FillCircle(bx, by, r, White);
        canvas.StrokeCircle(bx, by, r, GridColor, 1.6);
        canvas.DrawText((int)Math.Round(bx - tw / 2.0), (int)Math.Round(by - th / 2.0), label, GridColor, LabelScale);
        return true;
    }

    private static bool DrawLevel(
        RasterCanvas canvas, ViewProjection projection, Frame frame, Vec3[] corners, string label,
        List<(double X0, double Y0, double X1, double Y1)> occupied)
    {
        var px = corners.Select(c => frame.ToPx(Vec3.Dot(c, projection.Right), Vec3.Dot(c, projection.Up))).ToArray();
        var (a, b) = FarthestPair(px);
        double x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;
        if (!RasterCanvas.ClipSegment(ref x0, ref y0, ref x1, ref y1, frame.Left - 20, frame.Top, frame.Right + 20, frame.Bottom))
            return false;
        canvas.DrawDashedLine(x0, y0, x1, y1, LevelColor, 1.0, 5, 5, 0.8);

        // Close levels (+0.200, +0.300, +0.400 …) would print on top of each other: the line
        // stays, the label only when it has room.
        var lx = Math.Min(x0, x1);
        var ly = x0 <= x1 ? y0 : y1;
        var (tw, th) = RasterCanvas.MeasureText(label, LabelScale);
        var tx = (int)Math.Max(2, lx + 2);
        var ty = (int)(ly - th - 3);
        var box = (tx - 2.0, ty - 1.0, tx + tw + 2.0, ty + th + 1.0);
        if (occupied.Any(o => Overlaps(o, box, 1))) return true;
        occupied.Add(box);
        canvas.FillRect(box.Item1, box.Item2, box.Item3, box.Item4, White, 0.85);
        canvas.DrawText(tx, ty, label, LevelColor, LabelScale);
        return true;
    }

    private static ((double X, double Y), (double X, double Y)) FarthestPair((double X, double Y)[] pts)
    {
        var best = 0.0;
        var pair = (pts[0], pts[pts.Length - 1]);
        for (var i = 0; i < pts.Length; i++)
            for (var j = i + 1; j < pts.Length; j++)
            {
                var d = (pts[i].X - pts[j].X) * (pts[i].X - pts[j].X) + (pts[i].Y - pts[j].Y) * (pts[i].Y - pts[j].Y);
                if (d > best)
                {
                    best = d;
                    pair = (pts[i], pts[j]);
                }
            }
        return pair;
    }

    // -------------------------------------------------------------------------------- labels ---

    private static void PlaceLabels(
        RasterCanvas canvas, List<Drawable> focus, List<Drawable> context, Frame frame, string mode,
        SchematicRenderOptions options, SchematicRenderResult result)
    {
        var placed = new List<(double X0, double Y0, double X1, double Y1)>();
        var maxLabels = Math.Max(0, options.MaxLabels);
        var candidates = focus.Concat(options.LabelContext ? context : Enumerable.Empty<Drawable>()).ToList();
        var number = 0;

        foreach (var d in candidates)
        {
            number++;
            var entry = new SchematicLegendEntry
            {
                Guid = d.Item.Guid,
                Id = d.Item.Id,
                Type = d.Item.Type,
                Name = d.Item.Name,
                Profile = d.Item.Profile,
                Class = d.Item.Class,
                AssemblyPos = d.Item.AssemblyPos,
                Focus = d.Item.Focus,
                GeometrySource = d.Item.GeometrySource,
                Label = LabelText(d.Item, mode, number),
            };

            var anchor = Anchor(d, frame);
            if (anchor.HasValue)
            {
                entry.X = (int)Math.Round(anchor.Value.X);
                entry.Y = (int)Math.Round(anchor.Value.Y);
            }

            if (mode != "none" && anchor.HasValue && placed.Count < maxLabels && frame.Inside(anchor.Value.X, anchor.Value.Y, 2))
            {
                var box = FindLabelSpot(anchor.Value, entry.Label, placed, frame, canvas);
                if (box.HasValue)
                {
                    DrawLabel(canvas, anchor.Value, box.Value, entry.Label, d.Color);
                    placed.Add(box.Value);
                    entry.LabelPlaced = true;
                    result.LabelsPlaced++;
                }
            }
            if (mode != "none" && !entry.LabelPlaced) result.LabelsOmitted++;

            // Context entries only matter when they carry a label.
            if (d.Item.Focus || entry.LabelPlaced) result.Legend.Add(entry);
        }

        if (result.LabelsOmitted > 0)
            result.Warnings.Add(result.LabelsOmitted + " object(s) could not be labelled (no free space, outside the frame, " +
                                "or the maxLabels cap); they are still drawn and listed in the legend with labelPlaced=false.");
    }

    private static string LabelText(SchematicItem item, string mode, int number)
    {
        switch (mode)
        {
            case "id": return item.Id.ToString(CultureInfo.InvariantCulture);
            case "mark": return string.IsNullOrWhiteSpace(item.AssemblyPos) ? "?" : item.AssemblyPos!;
            case "profile": return string.IsNullOrWhiteSpace(item.Profile) ? "?" : item.Profile;
            case "name": return string.IsNullOrWhiteSpace(item.Name) ? "?" : item.Name;
            case "none": return "";
            default: return number.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static (double X, double Y)? Anchor(Drawable d, Frame frame)
    {
        if (d.Markers.Count > 0) return frame.ToPx(d.Markers[0].U, d.Markers[0].V);
        if (d.Paths.Count == 0) return null;
        if (d.Shape == "box" || d.Shape == "polygon")
        {
            var all = d.Paths.SelectMany(p => p).ToList();
            return frame.ToPx(all.Average(p => p.U), all.Average(p => p.V));
        }

        // Midpoint of the longest visible segment keeps the label on the member, not at a joint.
        var path = d.Paths[0].Select(q => frame.ToPx(q.U, q.V)).ToList();
        if (path.Count == 1) return path[0];
        var best = -1.0;
        (double X, double Y) mid = path[0];
        for (var i = 0; i + 1 < path.Count; i++)
        {
            double x0 = path[i].X, y0 = path[i].Y, x1 = path[i + 1].X, y1 = path[i + 1].Y;
            if (!RasterCanvas.ClipSegment(ref x0, ref y0, ref x1, ref y1, frame.Left, frame.Top, frame.Right, frame.Bottom))
                continue;
            var len = (x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0);
            if (len > best)
            {
                best = len;
                mid = ((x0 + x1) / 2, (y0 + y1) / 2);
            }
        }
        return mid;
    }

    private static (double X0, double Y0, double X1, double Y1)? FindLabelSpot(
        (double X, double Y) anchor, string text, List<(double X0, double Y0, double X1, double Y1)> placed, Frame frame,
        RasterCanvas canvas)
    {
        var (tw, th) = RasterCanvas.MeasureText(text, LabelScale);
        double bw = tw + 6, bh = th + 6;
        foreach (var r in new[] { 7.0, 18.0, 32.0, 50.0, 72.0 })
        {
            var spots = new (double X, double Y)[]
            {
                (anchor.X + r, anchor.Y - r - bh), (anchor.X + r, anchor.Y + r),
                (anchor.X - r - bw, anchor.Y - r - bh), (anchor.X - r - bw, anchor.Y + r),
                (anchor.X + r, anchor.Y - bh / 2), (anchor.X - r - bw, anchor.Y - bh / 2),
                (anchor.X - bw / 2, anchor.Y - r - bh), (anchor.X - bw / 2, anchor.Y + r),
            };
            foreach (var s in spots)
            {
                var box = (s.X, s.Y, s.X + bw, s.Y + bh);
                if (box.Item1 < 2 || box.Item3 > canvas.Width - 2 ||
                    box.Item2 < frame.Top - SideMargin + 2 || box.Item4 > frame.Bottom + 4)
                    continue;
                if (placed.Any(p => Overlaps(p, box, 2))) continue;
                return box;
            }
        }
        return null;
    }

    private static bool Overlaps(
        (double X0, double Y0, double X1, double Y1) a, (double X0, double Y0, double X1, double Y1) b, double gap) =>
        a.X0 - gap < b.X1 && b.X0 - gap < a.X1 && a.Y0 - gap < b.Y1 && b.Y0 - gap < a.Y1;

    private static void DrawLabel(
        RasterCanvas canvas, (double X, double Y) anchor, (double X0, double Y0, double X1, double Y1) box, string text, Rgb color)
    {
        var cx = Math.Max(box.X0, Math.Min(box.X1, anchor.X));
        var cy = Math.Max(box.Y0, Math.Min(box.Y1, anchor.Y));
        if (Math.Sqrt((cx - anchor.X) * (cx - anchor.X) + (cy - anchor.Y) * (cy - anchor.Y)) > 4)
            canvas.DrawLine(anchor.X, anchor.Y, cx, cy, color, 1.2, 0.9);
        canvas.FillCircle(anchor.X, anchor.Y, 2.6, color);
        canvas.FillRect(box.X0, box.Y0, box.X1, box.Y1, White, 0.93);
        canvas.StrokeRect(box.X0, box.Y0, box.X1, box.Y1, color);
        canvas.DrawText((int)Math.Round(box.X0 + 3), (int)Math.Round(box.Y0 + 3), text, TextDark, LabelScale);
    }

    // ------------------------------------------------------------------------------ overlays ---

    private static void DrawTitle(
        RasterCanvas canvas, ViewProjection projection, double mmPerPixel, SchematicRenderResult result,
        SchematicRenderOptions options, int width)
    {
        canvas.FillRect(0, 0, width, TitleHeight, Band);
        canvas.FillRect(0, TitleHeight - 1, width, TitleHeight, BandLine);
        var title = projection.Describe() + "  |  1 px = " + FormatMm(mmPerPixel) + " mm  |  focus " +
                    result.FocusDrawn + ", context " + result.ContextDrawn;
        var maxChars = Math.Max(8, (width - 16) / (BitmapFont.GlyphSize * LabelScale));
        if (title.Length > maxChars) title = title.Substring(0, maxChars - 1) + "~";
        canvas.DrawText(8, (TitleHeight - 16) / 2, title, TextDark, LabelScale);

        if (string.IsNullOrWhiteSpace(options.Banner)) return;
        canvas.FillRect(0, TitleHeight, width, TitleHeight + BannerHeight, BannerColor, 0.12);
        var banner = options.Banner!.Trim();
        if (banner.Length > maxChars) banner = banner.Substring(0, maxChars - 1) + "~";
        canvas.DrawText(8, TitleHeight + (BannerHeight - 16) / 2, banner, BannerColor, LabelScale);
    }

    private static void DrawFooter(RasterCanvas canvas, ViewProjection projection, Frame frame, int width, int height, double mmPerPixel)
    {
        var top = height - FooterHeight;
        canvas.FillRect(0, top, width, height, Band);
        canvas.FillRect(0, top, width, top + 1, BandLine);

        // Axis triad (Tekla colours: X red, Y green, Z blue).
        double ox = 34, oy = top + FooterHeight / 2.0 + 2;
        var axes = new[]
        {
            ("X", new Vec3(1, 0, 0), Rgb.FromHex(0xD62728)),
            ("Y", new Vec3(0, 1, 0), Rgb.FromHex(0x2CA02C)),
            ("Z", new Vec3(0, 0, 1), Rgb.FromHex(0x1F5FD6)),
        };
        var toward = new List<string>();
        foreach (var (name, dir, color) in axes)
        {
            var sx = Vec3.Dot(dir, projection.Right);
            var sy = -Vec3.Dot(dir, projection.Up);
            var len = Math.Sqrt(sx * sx + sy * sy);
            if (len < 0.2)
            {
                toward.Add(name + (Vec3.Dot(dir, projection.Forward) < 0 ? " toward you" : " away"));
                continue;
            }
            var ex = ox + sx * 14;
            var ey = oy + sy * 14;
            canvas.DrawLine(ox, oy, ex, ey, color, 2);
            canvas.DrawText((int)Math.Round(ox + sx * 20 - 4), (int)Math.Round(oy + sy * 20 - 4), name, color, 1);
        }
        canvas.FillCircle(ox, oy, 2.5, TextDark);
        if (toward.Count > 0) canvas.DrawText(62, top + 8, string.Join(", ", toward), TextMuted, 1);

        // Scale bar: a round length between ~80 and ~200 px.
        var target = 140 * mmPerPixel;
        var step = NiceStep(target);
        var barPx = step / mmPerPixel;
        var x1 = width - 20.0;
        var x0 = x1 - barPx;
        var y = top + FooterHeight - 14.0;
        canvas.DrawLine(x0, y, x1, y, TextDark, 2);
        canvas.DrawLine(x0, y - 5, x0, y + 1, TextDark, 2);
        canvas.DrawLine(x1, y - 5, x1, y + 1, TextDark, 2);
        var text = step >= 1000 ? FormatNumber(step / 1000) + " m" : FormatNumber(step) + " mm";
        var (tw, _) = RasterCanvas.MeasureText(text, LabelScale);
        canvas.DrawText((int)Math.Round(x1 - tw), top + 5, text, TextDark, LabelScale);
    }

    /// <summary>
    /// Model-coordinate ticks for views whose screen axes are global axes. A tick label that
    /// would cover a grid bubble or a level label is left out (the tick mark stays).
    /// </summary>
    private static void DrawAxisTicks(
        RasterCanvas canvas, ViewProjection projection, Frame frame, int width,
        List<(double X0, double Y0, double X1, double Y1)> occupied)
    {
        var aligned = projection.AxisAligned();
        if (aligned == null) return;
        var ((hAxis, hSign), (vAxis, vSign)) = aligned.Value;

        // Horizontal axis along the bottom edge of the frame.
        var hStep = NiceStep(230 / frame.Scale);
        var uLeft = frame.U0 + (frame.Left - frame.Cx) / frame.Scale;
        var uRight = frame.U0 + (frame.Right - frame.Cx) / frame.Scale;
        var aMin = Math.Min(hSign * uLeft, hSign * uRight);
        var aMax = Math.Max(hSign * uLeft, hSign * uRight);
        for (var a = Math.Ceiling(aMin / hStep) * hStep; a <= aMax; a += hStep)
        {
            var x = frame.Cx + (hSign * a - frame.U0) * frame.Scale;
            var y = frame.Bottom;
            canvas.DrawLine(x, y - 6, x, y + 2, TextMuted, 1.2);
            var text = hAxis + "=" + FormatNumber(a);
            var (tw, th) = RasterCanvas.MeasureText(text, LabelScale);
            var tx = (int)Math.Round(Math.Max(2, Math.Min(width - tw - 2, x - tw / 2.0)));
            var box = (tx - 2.0, (double)(y - th - 10), tx + tw + 2.0, y - 7.0);
            if (occupied.Any(o => Overlaps(o, box, 1))) continue;
            canvas.FillRect(box.Item1, box.Item2, box.Item3, box.Item4, White, 0.85);
            canvas.DrawText(tx, y - th - 8, text, TextMuted, LabelScale);
        }

        // Vertical axis along the right edge of the frame.
        var vStep = NiceStep(130 / frame.Scale);
        var vTop = frame.V0 + (frame.Cy - frame.Top) / frame.Scale;
        var vBottom = frame.V0 + (frame.Cy - frame.Bottom) / frame.Scale;
        var bMin = Math.Min(vSign * vTop, vSign * vBottom);
        var bMax = Math.Max(vSign * vTop, vSign * vBottom);
        for (var b = Math.Ceiling(bMin / vStep) * vStep; b <= bMax; b += vStep)
        {
            var yy = frame.Cy - (vSign * b - frame.V0) * frame.Scale;
            // Keep clear of the bottom row, which carries the horizontal ticks.
            if (yy > frame.Bottom - 30) continue;
            var x = frame.Right;
            canvas.DrawLine(x - 6, yy, x + 2, yy, TextMuted, 1.2);
            var text = vAxis + "=" + FormatNumber(b);
            var (tw, th) = RasterCanvas.MeasureText(text, LabelScale);
            var tx = (int)(x - tw - 9);
            var box = (tx - 2.0, yy - th / 2.0 - 2, tx + tw + 2.0, yy + th / 2.0 + 2);
            if (occupied.Any(o => Overlaps(o, box, 1))) continue;
            canvas.FillRect(box.Item1, box.Item2, box.Item3, box.Item4, White, 0.85);
            canvas.DrawText(tx, (int)Math.Round(yy - th / 2.0), text, TextMuted, LabelScale);
        }
    }

    // ------------------------------------------------------------------------------- mapping ---

    private static void FillMapping(
        SchematicRenderResult result, ViewProjection projection, Frame frame, List<Vec3> framePoints, int width, int height)
    {
        var m = 1 / frame.Scale;
        var depths = framePoints.Select(p => Vec3.Dot(p, projection.Forward)).ToList();
        var d0 = (depths.Min() + depths.Max()) / 2;
        var u00 = frame.U0 - frame.Cx * m;
        var v00 = frame.V0 + frame.Cy * m;
        var origin = projection.Right * u00 + projection.Up * v00 + projection.Forward * d0;

        result.ViewDirection = projection.Forward.ToPoint();
        result.ScreenRight = projection.Right.ToPoint();
        result.ScreenUp = projection.Up.ToPoint();
        result.PixelOrigin = origin.ToPoint(1);

        var aligned = projection.AxisAligned();
        if (aligned != null)
        {
            var ((hAxis, hSign), (vAxis, vSign)) = aligned.Value;
            var h0 = hSign * u00;
            var v0 = vSign * v00;
            var depthAxis = new[] { "X", "Y", "Z" }.First(a => a != hAxis && a != vAxis);
            result.PixelToModel = string.Format(CultureInfo.InvariantCulture,
                "{0} = {1:0.#} {2} px*{3:0.###}; {4} = {5:0.#} {6} py*{3:0.###}; {7} is the viewing depth (not shown)",
                hAxis, h0, hSign > 0 ? "+" : "-", m, vAxis, v0, vSign > 0 ? "-" : "+", depthAxis);
        }
        else
        {
            var r = projection.Right * m;
            var dn = projection.Up * -m;
            result.PixelToModel = string.Format(CultureInfo.InvariantCulture,
                "model = ({0:0.#}, {1:0.#}, {2:0.#}) + px*({3:0.###}, {4:0.###}, {5:0.###}) + py*({6:0.###}, {7:0.###}, {8:0.###}) " +
                "+ unknown depth along viewDirection",
                origin.X, origin.Y, origin.Z, r.X, r.Y, r.Z, dn.X, dn.Y, dn.Z);
        }
    }

    private static Box3D? BoxOf(IEnumerable<Vec3> points)
    {
        var list = points.ToList();
        if (list.Count == 0) return null;
        return new Box3D(
            new Point3D(Math.Round(list.Min(p => p.X), 1), Math.Round(list.Min(p => p.Y), 1), Math.Round(list.Min(p => p.Z), 1)),
            new Point3D(Math.Round(list.Max(p => p.X), 1), Math.Round(list.Max(p => p.Y), 1), Math.Round(list.Max(p => p.Z), 1)));
    }

    // -------------------------------------------------------------------------------- helpers ---

    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw)) return 1000;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / exp;
        var nice = f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10;
        return nice * exp;
    }

    private static string FormatNumber(double v)
    {
        if (Math.Abs(v) < 1e-6) return "0"; // never "-0"
        return Math.Abs(v - Math.Round(v)) < 1e-6
            ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatMm(double v) =>
        v >= 10 ? v.ToString("0.#", CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Normalize(string? value, string[] allowed, string fallback, SchematicRenderResult result, string name)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        if (v.Length == 0) return fallback;
        if (allowed.Contains(v)) return v;
        result.Warnings.Add(name + " '" + value + "' is not one of " + string.Join("|", allowed) + "; used '" + fallback + "'.");
        return fallback;
    }

    private static string AppendSentence(string? current, string next) =>
        string.IsNullOrWhiteSpace(current) ? next : current + " " + next;

    private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

    private static bool Finite(Point3D p) =>
        p != null && !double.IsNaN(p.X) && !double.IsNaN(p.Y) && !double.IsNaN(p.Z) &&
        !double.IsInfinity(p.X) && !double.IsInfinity(p.Y) && !double.IsInfinity(p.Z);
}
