using System;
using System.Collections.Generic;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Geometry;

/// <summary>
/// Conventions the drawing-dimension tools apply before handing points and distances to Tekla
/// (issues #26, #27). Both backends use them, so a mock preview shows what the live call will
/// send.
/// </summary>
public static class DimensionMath
{
    /// <summary>
    /// The tools take dimension-line distances in paper millimetres. Tekla's
    /// <c>StraightDimensionSetHandler.CreateDimensionSet</c> takes view units instead (model mm in a
    /// view) — verified live on Tekla 2021 (issue #26: distance 8 at 1:50 put the line 0.16 mm off
    /// the parts, 400 gave the intended 8 mm), although the Open API documents
    /// <c>StraightDimensionSet.Distance</c> as paper mm. The sheet has no scale (1).
    /// </summary>
    public static double PaperToViewDistance(double paperMm, double viewScale) =>
        paperMm * (viewScale > 0 ? viewScale : 1.0);

    /// <summary>
    /// Orders three arc points so that the CLOCKWISE path from the first to the third, in the
    /// view's XY plane, passes through the second — the way Tekla's <c>RadiusDimension</c> draws
    /// its arc whatever the second point (verified live on Tekla 2021, issue #27: a
    /// counter-clockwise arc passed start → mid → end got the complementary, near-full circle).
    /// Input: start, any point on the arc strictly between, end — in either direction.
    /// Returns the points unchanged when they already run clockwise, reversed when they run
    /// counter-clockwise, and null when they are coincident or collinear (no arc).
    /// </summary>
    public static IReadOnlyList<Point3D>? OrderArcClockwise(
        Point3D first, Point3D between, Point3D last, out bool reversed)
    {
        reversed = false;
        if (first == null || between == null || last == null) return null;
        var abX = between.X - first.X;
        var abY = between.Y - first.Y;
        var acX = last.X - first.X;
        var acY = last.Y - first.Y;
        var bcX = last.X - between.X;
        var bcY = last.Y - between.Y;
        var ab = Math.Sqrt(abX * abX + abY * abY);
        var ac = Math.Sqrt(acX * acX + acY * acY);
        var bc = Math.Sqrt(bcX * bcX + bcY * bcY);
        if (ab <= CurveMath.Tolerance || ac <= CurveMath.Tolerance || bc <= CurveMath.Tolerance)
            return null;

        // z of (b − a) × (c − a): positive = a → b → c turns counter-clockwise.
        var cross = abX * acY - abY * acX;
        // Height of b over the chord a–c, relative to the tolerance: below it the three points
        // are a straight line as far as a drawing is concerned.
        if (Math.Abs(cross) / ac <= CurveMath.Tolerance) return null;

        if (cross < 0) return new[] { first, between, last };
        reversed = true;
        return new[] { last, between, first };
    }
}
