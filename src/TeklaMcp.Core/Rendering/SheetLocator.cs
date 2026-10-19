using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Rendering;

/// <summary>
/// Finds the drawing sheet on a capture of Tekla's drawing editor (<c>tekla_capture_drawing</c>).
/// The Open API cannot read the editor's zoom or scroll position (no member mentions one,
/// 2021–2026), so the sheet is located from the picture: the editor draws the sheet's edges as
/// straight lines, and the paper size fixes their aspect ratio. A rectangle of long lines with
/// that aspect ratio is the sheet; with several, the largest wins (a title-block frame lies inside
/// the sheet).
///
/// Cost: one pass over every pixel per direction, then pairs of the 64 longest lines per
/// direction — tens of milliseconds for a 2500×1200 editor.
///
/// Probed live (Tekla 2021, A2 GA sheet, 2026-10-16): a freshly opened drawing fills the editor's
/// height — left, right and top edges visible, the bottom edge on the window border — at 2.867
/// px/mm, and view frames from <c>View.GetAxisAlignedBoundingBox()</c> landed on the drawn views.
/// A fourth edge may therefore be missing, but only ON the border: a sheet that runs off the
/// window (the user zoomed in) is not found, because three lines of a smaller rectangle (the
/// title-block frame) would fit it just as well. And no edge may run on past a corner: the cells of
/// a title block seen up close matched A2's proportions on the live probe. Callers then show the
/// whole window and say the mapping is unknown rather than guess.
/// </summary>
public static class SheetLocator
{
    private const int MinLinePx = 40;
    private const int MinSheetPx = 64;
    private const double MinCoverage = 0.9;
    private const int MaxCandidates = 64;

    /// <summary>
    /// Locate a sheet of <paramref name="sheetWidthMm"/>×<paramref name="sheetHeightMm"/> on a
    /// <paramref name="width"/>×<paramref name="height"/> picture (<paramref name="rgb"/> = 0xRRGGBB,
    /// row-major; pass the window's CLIENT area, without its border lines).
    /// </summary>
    public static SheetPixelMapping Locate(int width, int height, int[] rgb, double sheetWidthMm, double sheetHeightMm)
    {
        var result = new SheetPixelMapping();
        if (width < MinSheetPx || height < MinSheetPx || rgb == null || rgb.Length < width * height)
        {
            result.Message = "The picture is too small to locate the sheet.";
            return result;
        }
        if (!(sheetWidthMm > 0) || !(sheetHeightMm > 0))
        {
            result.Message = "The sheet size is unknown, so the sheet cannot be located on the picture.";
            return result;
        }

        var picture = new Picture(width, height, rgb);
        var columns = Candidates(picture, vertical: true);
        var rows = Candidates(picture, vertical: false);

        Candidate? best = null;
        foreach (var (left, right) in Pairs(columns))
            Keep(ref best, Fit(picture, left, right, sheetWidthMm, sheetHeightMm, rows, vertical: true));
        foreach (var (top, bottom) in Pairs(rows))
            Keep(ref best, Fit(picture, top, bottom, sheetHeightMm, sheetWidthMm, columns, vertical: false));

        if (best == null)
        {
            result.Message = "The sheet's edges were not found on the picture: the drawing editor is zoomed in past " +
                             "the sheet (or the sheet's edge is not drawn). Ask the user to zoom out until the whole " +
                             "sheet is visible, then capture again.";
            return result;
        }

        result.Found = true;
        result.Method = "sheet-edges";
        result.PixelsPerMm = Math.Round(best.Scale, 5);
        result.SheetLeftPx = Math.Round(best.Left, 2);
        result.SheetTopPx = Math.Round(best.Top, 2);
        result.EdgesFound = best.Edges;
        if (best.Edges < 4)
            result.Message = "Three sheet edges were found; the fourth lies on the window border.";
        return result;
    }

    private sealed class Picture
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int[] Rgb;
        public readonly int Background;

        public Picture(int width, int height, int[] rgb)
        {
            Width = width;
            Height = height;
            Rgb = rgb;
            var counts = new Dictionary<int, int>();
            for (var y = 0; y < height; y += 3)
            for (var x = 0; x < width; x += 3)
            {
                var c = rgb[y * width + x];
                counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;
            }
            Background = counts.OrderByDescending(p => p.Value).First().Key;
        }

        public bool Ink(int x, int y) => Rgb[y * Width + x] != Background;
    }

    private sealed class Candidate
    {
        public double Left;
        public double Top;
        public double Scale;
        public int Edges;
        public double Area;
    }

    /// <summary>
    /// Columns (or rows) holding a long single-colour run; a group of neighbours (a line drawn
    /// thicker than one pixel) gives one candidate, the one with the longest run.
    /// </summary>
    private static List<int> Candidates(Picture p, bool vertical)
    {
        var lines = vertical ? p.Width : p.Height;
        var runs = new int[lines];
        // One row-major sweep (cache-friendly) keeping a run per column, or per row.
        var run = new int[lines];
        var previous = new int[lines];
        for (var i = 0; i < lines; i++) previous[i] = -1;
        for (var y = 0; y < p.Height; y++)
        {
            var row = y * p.Width;
            for (var x = 0; x < p.Width; x++)
            {
                var i = vertical ? x : y;
                var c = p.Rgb[row + x];
                if (c == p.Background) run[i] = 0;
                else if (c == previous[i]) run[i]++;
                else run[i] = 1;
                previous[i] = c;
                if (run[i] > runs[i]) runs[i] = run[i];
            }
        }

        var result = new List<int>();
        for (var i = 0; i < lines; i++)
        {
            if (runs[i] < MinLinePx) continue;
            var pick = i;
            while (i + 1 < lines && runs[i + 1] >= MinLinePx)
            {
                i++;
                if (runs[i] > runs[pick]) pick = i;
            }
            result.Add(pick);
        }
        // The sheet's edges are among the longest lines on the picture; keeping only those bounds
        // the pair search on a busy drawing.
        return result.OrderByDescending(i => runs[i]).Take(MaxCandidates).OrderBy(i => i).ToList();
    }

    private static IEnumerable<(int A, int B)> Pairs(List<int> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        for (var j = i + 1; j < lines.Count; j++)
            if (lines[j] - lines[i] >= MinSheetPx)
                yield return (lines[i], lines[j]);
    }

    /// <summary>
    /// Two parallel edges <paramref name="a"/> &lt; <paramref name="b"/> (columns when
    /// <paramref name="vertical"/>) are <paramref name="abMm"/> apart; find the perpendicular pair
    /// <paramref name="otherMm"/> apart at the same scale — both among <paramref name="others"/>, or
    /// one of them with the other on the picture's border.
    /// </summary>
    private static Candidate? Fit(Picture p, int a, int b, double abMm, double otherMm, List<int> others, bool vertical)
    {
        var scale = (b - a) / abMm;
        var span = otherMm * scale;
        var limit = vertical ? p.Height : p.Width;
        var tolerance = Math.Max(2.0, span * 0.006);
        Candidate? best = null;
        foreach (var near in others)
        {
            var far = near + span;
            var farLine = Nearest(others, far, tolerance);
            if (farLine.HasValue)
                Keep(ref best, Build(p, a, b, near, farLine.Value, abMm, otherMm, vertical, 4));
            else if (Math.Abs(far - limit) <= tolerance)
                Keep(ref best, Build(p, a, b, near, far, abMm, otherMm, vertical, 3));
        }
        foreach (var farEdge in others)
        {
            var near = farEdge - span;
            if (Math.Abs(near + 1) <= tolerance && Nearest(others, near, tolerance) == null)
                Keep(ref best, Build(p, a, b, near, farEdge, abMm, otherMm, vertical, 3));
        }
        return best;
    }

    private static int? Nearest(List<int> lines, double position, double tolerance)
    {
        int? best = null;
        foreach (var line in lines)
            if (Math.Abs(line - position) <= tolerance &&
                (best == null || Math.Abs(line - position) < Math.Abs(best.Value - position)))
                best = line;
        return best;
    }

    /// <summary>
    /// The rectangle with parallel edges <paramref name="a"/>, <paramref name="b"/> and
    /// perpendicular edges <paramref name="near"/>, <paramref name="far"/>, when the parts of its
    /// edges that are on the picture are drawn (≥ 90 % ink); null otherwise.
    /// </summary>
    private static Candidate? Build(
        Picture p, int a, int b, double near, double far, double abMm, double otherMm, bool vertical, int edges)
    {
        var limit = vertical ? p.Height : p.Width;
        var from = (int)Math.Max(0, Math.Round(near));
        var to = (int)Math.Min(limit - 1, Math.Round(far));
        if (to - from < MinSheetPx) return null;

        if (!Drawn(p, a, from, to, vertical) || !Drawn(p, b, from, to, vertical)) return null;
        foreach (var edge in new[] { near, far })
        {
            var line = (int)Math.Round(edge);
            if (line < 0 || line >= limit) continue;
            if (!Drawn(p, line, a, b, !vertical)) return null;
        }

        var left = vertical ? a : near;
        var right = vertical ? b : far;
        var top = vertical ? near : a;
        var bottom = vertical ? far : b;
        if (RunsPastCorners(p, left, right, top, bottom)) return null;

        var scale = edges == 4 ? ((b - a) / abMm + (far - near) / otherMm) / 2 : (b - a) / abMm;
        return new Candidate
        {
            Left = left,
            Top = top,
            Scale = scale,
            Edges = edges,
            Area = (b - a) * (far - near),
        };
    }

    /// <summary>
    /// The edge segment is drawn: ≥ 90 % ink over its length AND at both ends, so a rectangle mixing
    /// two sheet edges with two lines of the title-block frame (which stop a few mm short of the
    /// corners) is not taken for the sheet.
    /// </summary>
    private static bool Drawn(Picture p, int line, int from, int to, bool vertical)
    {
        if (Coverage(p, line, from, to, vertical) < MinCoverage) return false;
        var end = Math.Max(3, (to - from) / 100);
        return Coverage(p, line, from, from + end, vertical) >= 0.66 && Coverage(p, line, to - end, to, vertical) >= 0.66;
    }

    /// <summary>
    /// True when an edge line carries on past a corner of the rectangle. The sheet's edges stop at
    /// its corners — nothing is drawn outside the paper's outline — while the lines of a table or a
    /// title block run on into the next cell. Without this, cells of a title block seen up close
    /// pass for the sheet (live Tekla 2021: "Лист / №док. / Подпись" cells, 91×65 px, matched A2's
    /// aspect ratio to 0.3 %). Only corners and edges on the picture are checked.
    /// </summary>
    private static bool RunsPastCorners(Picture p, double left, double right, double top, double bottom)
    {
        int Line(double v) => (int)Math.Round(v);
        bool OnX(int x) => x >= 0 && x < p.Width;
        bool OnY(int y) => y >= 0 && y < p.Height;

        foreach (var (cx, dx) in new[] { (Line(left), -1), (Line(right), 1) })
        foreach (var (cy, dy) in new[] { (Line(top), -1), (Line(bottom), 1) })
        {
            if (!OnX(cx) || !OnY(cy)) continue;
            // The vertical edge beyond the corner (up or down), the horizontal one (left or right).
            if (InkBeyond(p, cx, cy, 0, dy) || InkBeyond(p, cx, cy, dx, 0)) return true;
        }
        return false;
    }

    /// <summary>More than one ink pixel 3…8 px beyond (<paramref name="x"/>, <paramref name="y"/>) along (dx, dy).</summary>
    private static bool InkBeyond(Picture p, int x, int y, int dx, int dy)
    {
        var ink = 0;
        for (var d = 3; d <= 8; d++)
        {
            int px = x + dx * d, py = y + dy * d;
            if (px < 0 || py < 0 || px >= p.Width || py >= p.Height) break;
            if (p.Ink(px, py)) ink++;
        }
        return ink > 1;
    }

    /// <summary>Share of ink pixels on a column (vertical) or row from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static double Coverage(Picture p, int line, int from, int to, bool vertical)
    {
        if (to < from) return 0;
        var ink = 0;
        for (var i = from; i <= to; i++)
            if (vertical ? p.Ink(line, i) : p.Ink(i, line))
                ink++;
        return (double)ink / (to - from + 1);
    }

    private static void Keep(ref Candidate? best, Candidate? candidate)
    {
        if (candidate == null) return;
        // The sheet is the outermost rectangle; more visible edges only break a tie.
        if (best == null || candidate.Area > best.Area * 1.01 ||
            (candidate.Area >= best.Area * 0.99 && candidate.Edges > best.Edges))
            best = candidate;
    }
}
