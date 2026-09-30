using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Geometry;

/// <summary>
/// Tekla grid semantics, shared by both backends (and unit-tested without Tekla):
///
/// * <c>Grid.CoordinateX</c>/<c>CoordinateY</c> are SPACINGS: the first value is measured from
///   the grid origin, each further value from the previous line; <c>n*step</c> repeats a spacing.
///   <c>CoordinateZ</c> holds ABSOLUTE levels (verified live on Tekla 2023, model 3219:
///   "0.00 200.00 300.00 …" is labelled "0.000 +0.200 +0.300 …").
/// * <c>Grid.LabelX/LabelY/LabelZ</c> are space-separated labels in line order.
/// * A grid has its own coordinate system (<c>ModelObject.GetCoordinateSystem()</c>): the same
///   model held a second grid at origin (−4950, −7250, −260), which a reader assuming the global
///   origin would place several metres off.
/// </summary>
public static class GridMath
{
    private static readonly string[] CyrillicLabels =
        { "А", "Б", "В", "Г", "Д", "Е", "Ж", "И", "К", "Л", "М", "Н", "П", "Р", "С", "Т" };

    /// <summary>
    /// Parse a Tekla grid coordinate string. <paramref name="relative"/> = X/Y spacing semantics,
    /// false = Z absolute levels. <c>n*step</c> always adds <c>step</c> n times; a string that starts
    /// with a repeat gets a line at 0 first (Tekla's "4*6000" = 0, 6000 … 24000).
    /// </summary>
    public static List<double> ParsePositions(string? text, bool relative)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var current = 0.0;
        foreach (var raw in text!.Split(new[] { ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            var star = token.IndexOf('*');
            if (star > 0 && star < token.Length - 1 &&
                int.TryParse(token.Substring(0, star), NumberStyles.Integer, CultureInfo.InvariantCulture, out var repeat) &&
                repeat > 0 &&
                TryParse(token.Substring(star + 1), out var step))
            {
                if (result.Count == 0) result.Add(current);
                for (var i = 0; i < repeat; i++)
                {
                    current += step;
                    result.Add(current);
                }
                continue;
            }

            if (!TryParse(token, out var value)) continue;
            current = relative && result.Count > 0 ? current + value : value;
            result.Add(current);
        }
        return result;
    }

    /// <summary>Split a Tekla label string ("1 2 3", "А Б В", "0.000 +3.000").</summary>
    public static List<string> SplitLabels(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text!.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>
    /// Pair positions with labels. A missing label falls back to the old convention (X: 1, 2, 3…;
    /// Y: А, Б, В…; Z: the level in metres) so callers never get a blank name.
    /// </summary>
    public static List<GridAxisLine> BuildAxis(IReadOnlyList<double> positions, IReadOnlyList<string> labels, string axis)
    {
        var lines = new List<GridAxisLine>();
        for (var i = 0; i < positions.Count; i++)
        {
            var label = i < labels.Count && !string.IsNullOrWhiteSpace(labels[i])
                ? labels[i]
                : FallbackLabel(axis, i, positions[i]);
            lines.Add(new GridAxisLine { Label = label, Position = positions[i] });
        }
        return lines;
    }

    /// <summary>The pre-v0.8 generated label, used only when Tekla has none.</summary>
    public static string FallbackLabel(string axis, int index, double position)
    {
        switch (axis)
        {
            case "X": return (index + 1).ToString(CultureInfo.InvariantCulture);
            case "Y": return index < CyrillicLabels.Length ? CyrillicLabels[index] : "Y" + (index + 1);
            default: return (position / 1000.0).ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Flatten grids into global grid lines for <c>tekla_list_grids</c>: X/Y lines with their
    /// global coordinate and end points, Z levels with their global elevation.
    /// </summary>
    public static List<GridLineInfo> Flatten(IEnumerable<SchematicGrid> grids)
    {
        var result = new List<GridLineInfo>();
        foreach (var grid in grids)
        {
            var o = grid.Origin;
            var ax = Normalize(grid.AxisX, new Point3D(1, 0, 0));
            var ay = Normalize(grid.AxisY, new Point3D(0, 1, 0));
            var az = Cross(ax, ay);
            var xAligned = Math.Abs(ax.X - 1) < 1e-6;
            var yAligned = Math.Abs(ay.Y - 1) < 1e-6;
            var zAligned = Math.Abs(az.Z - 1) < 1e-6;
            var rotated = !(xAligned && yAligned);

            var yMin = grid.LinesY.Count > 0 ? grid.LinesY.Min(l => l.Position) : 0;
            var yMax = grid.LinesY.Count > 0 ? grid.LinesY.Max(l => l.Position) : 0;
            var xMin = grid.LinesX.Count > 0 ? grid.LinesX.Min(l => l.Position) : 0;
            var xMax = grid.LinesX.Count > 0 ? grid.LinesX.Max(l => l.Position) : 0;

            foreach (var line in grid.LinesX)
            {
                result.Add(new GridLineInfo
                {
                    Axis = "X",
                    Label = line.Label,
                    Coordinate = Round(xAligned ? o.X + line.Position : line.Position),
                    GridId = grid.Id,
                    Rotated = rotated,
                    Start = At(o, ax, line.Position, ay, yMin),
                    End = At(o, ax, line.Position, ay, yMax),
                });
            }
            foreach (var line in grid.LinesY)
            {
                result.Add(new GridLineInfo
                {
                    Axis = "Y",
                    Label = line.Label,
                    Coordinate = Round(yAligned ? o.Y + line.Position : line.Position),
                    GridId = grid.Id,
                    Rotated = rotated,
                    Start = At(o, ax, xMin, ay, line.Position),
                    End = At(o, ax, xMax, ay, line.Position),
                });
            }
            foreach (var level in grid.Levels)
            {
                result.Add(new GridLineInfo
                {
                    Axis = "Z",
                    Label = level.Label,
                    Coordinate = Round(zAligned ? o.Z + level.Position : level.Position),
                    GridId = grid.Id,
                    Rotated = !zAligned,
                });
            }
        }
        return result;
    }

    /// <summary>One grid at the global origin from flat lines (the mock keeps its grid flat).</summary>
    public static SchematicGrid FromFlat(IEnumerable<GridLineInfo> lines, int? id = null)
    {
        var grid = new SchematicGrid { Id = id };
        foreach (var line in lines)
        {
            var axisLine = new GridAxisLine { Label = line.Label, Position = line.Coordinate };
            switch (line.Axis)
            {
                case "X": grid.LinesX.Add(axisLine); break;
                case "Y": grid.LinesY.Add(axisLine); break;
                case "Z": grid.Levels.Add(axisLine); break;
            }
        }
        return grid;
    }

    /// <summary>
    /// Resolve "X label × Y label" to a point. Prefers an X and a Y line from the SAME grid
    /// (labels repeat across grids: model 3219 has "1" and "А" in two grids), intersects the
    /// actual lines for rotated grids, and says in the message when the choice was ambiguous.
    /// </summary>
    public static PointResult Resolve(IReadOnlyList<GridLineInfo> lines, string axisXLabel, string axisYLabel, double z)
    {
        var result = new PointResult { AxisX = axisXLabel, AxisY = axisYLabel, Z = z };
        var xs = lines.Where(g => g.Axis == "X" &&
                                  string.Equals(g.Label, axisXLabel?.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var ys = lines.Where(g => g.Axis == "Y" &&
                                  string.Equals(g.Label, axisYLabel?.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (xs.Count == 0 || ys.Count == 0)
        {
            result.Message = $"Grid label not found (X='{axisXLabel}': {xs.Count > 0}, Y='{axisYLabel}': {ys.Count > 0}).";
            return result;
        }

        GridLineInfo gx = xs[0], gy = ys[0];
        var sameGrid = xs.SelectMany(x => ys.Where(y => x.GridId == y.GridId).Select(y => (x, y))).ToList();
        if (sameGrid.Count > 0) (gx, gy) = sameGrid[0];

        if (xs.Count > 1 || ys.Count > 1)
        {
            var grids = xs.Concat(ys).Select(g => g.GridId?.ToString(CultureInfo.InvariantCulture) ?? "?").Distinct();
            result.Message = $"Labels '{axisXLabel}'/'{axisYLabel}' occur in several grids ({string.Join(", ", grids)}); " +
                             $"used grid {gx.GridId?.ToString(CultureInfo.InvariantCulture) ?? "?"}.";
        }

        if ((gx.Rotated || gy.Rotated) && gx.Start != null && gx.End != null && gy.Start != null && gy.End != null)
        {
            if (!IntersectXY(gx.Start, gx.End, gy.Start, gy.End, out var x, out var y))
            {
                result.Message = "Grid lines are parallel; cannot resolve an intersection.";
                return result;
            }
            result.X = Round(x);
            result.Y = Round(y);
        }
        else
        {
            result.X = gx.Coordinate;
            result.Y = gy.Coordinate;
        }
        result.Resolved = true;
        return result;
    }

    private static bool IntersectXY(Point3D a0, Point3D a1, Point3D b0, Point3D b1, out double x, out double y)
    {
        x = y = 0;
        double dax = a1.X - a0.X, day = a1.Y - a0.Y, dbx = b1.X - b0.X, dby = b1.Y - b0.Y;
        var den = dax * dby - day * dbx;
        if (Math.Abs(den) < 1e-9) return false;
        var t = ((b0.X - a0.X) * dby - (b0.Y - a0.Y) * dbx) / den;
        x = a0.X + t * dax;
        y = a0.Y + t * day;
        return true;
    }

    private static Point3D At(Point3D o, Point3D ax, double a, Point3D ay, double b) =>
        new Point3D(Round(o.X + ax.X * a + ay.X * b), Round(o.Y + ax.Y * a + ay.Y * b), Round(o.Z + ax.Z * a + ay.Z * b));

    /// <summary>Unit vector, or <paramref name="fallback"/> for a zero vector.</summary>
    public static Point3D Normalize(Point3D? v, Point3D fallback)
    {
        if (v == null) return fallback;
        var len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len < 1e-12 ? fallback : new Point3D(v.X / len, v.Y / len, v.Z / len);
    }

    private static Point3D Cross(Point3D a, Point3D b) =>
        new Point3D(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static bool TryParse(string token, out double value) =>
        double.TryParse(token.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static double Round(double v) => Math.Round(v, 2);
}
