using System;
using System.Globalization;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Geometry;

/// <summary>
/// Section-view semantics measured live on Tekla 2021 (issue #34, ~10 truss-node sections at
/// 1:10): the new view's restriction box is z ∈ [−depthUp, +depthDown] with +z toward the
/// viewer — depthUp is how far the view sees BEHIND the cut plane, depthDown the slab between the
/// cut plane and the viewer — and the view looks along the cut line's direction rotated +90° in
/// the source view (a cut drawn upwards looks toward −x).
/// </summary>
public static class SectionViews
{
    /// <summary>The look direction in source-view coordinates (unit x/y), or null for a degenerate cut.</summary>
    public static Point3D? LookDirection(Point3D cutStart, Point3D cutEnd)
    {
        var dx = cutEnd.X - cutStart.X;
        var dy = cutEnd.Y - cutStart.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= CurveMath.Tolerance) return null;
        return new Point3D(-dy / length, dx / length, 0);
    }

    /// <summary>Why the depths are unusable (zero or negative), or null when both are fine.</summary>
    public static string? CheckDepths(double? depthUp, double? depthDown)
    {
        if (depthUp > 0 && depthDown > 0) return null;
        return "depthUp and depthDown must both be > 0 mm (model mm): depthUp is how far the section " +
               "sees BEHIND the cut plane, depthDown the slab between the cut plane and the viewer, and a " +
               "zero depth leaves the section box without thickness on that side. Typical: depthDown 5–10, " +
               "depthUp as deep as the view must see.";
    }

    /// <summary>One line for a preview: where the section looks and its box depth.</summary>
    public static string Describe(Point3D cutStart, Point3D cutEnd, double depthUp, double depthDown)
    {
        var look = LookDirection(cutStart, cutEnd);
        var direction = look == null
            ? "unknown (the cut points coincide)"
            : string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}) in the source view", look.X, look.Y);
        return string.Format(CultureInfo.InvariantCulture,
            "The section looks toward {0}; its restriction box is z ∈ [{1:0.###}, {2:0.###}] in the new view " +
            "(+z toward the viewer).", direction, -depthUp, depthDown);
    }
}
