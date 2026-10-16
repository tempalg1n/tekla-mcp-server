using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Geometry;

/// <summary>
/// Model geometry of drawing parts and bolts in a view's DISPLAY coordinates (issue #38), and the
/// filters over it. Backends hand over points already transformed into the view; the box and the
/// filters are shared and tested here.
/// </summary>
public static class ViewGeometry
{
    /// <summary>
    /// The box spanned by <paramref name="points"/> — feed it the solid's own vertices, never the 8
    /// corners of the global AABB: through a tilted view those give a box that is too big
    /// (240.7 × 6.2 × 24.1 instead of 241 × 6 × 24, field report).
    /// </summary>
    public static bool TryBox(IEnumerable<Point3D> points, out Point3D min, out Point3D max)
    {
        min = max = new Point3D();
        var list = (points ?? Enumerable.Empty<Point3D>()).Where(p => p != null).ToList();
        if (list.Count == 0) return false;
        min = new Point3D(list.Min(p => p.X), list.Min(p => p.Y), list.Min(p => p.Z));
        max = new Point3D(list.Max(p => p.X), list.Max(p => p.Y), list.Max(p => p.Z));
        return true;
    }

    /// <summary>
    /// Applies the geometry filters of <paramref name="query"/>: a z (depth) range and an x/y
    /// region must overlap the object's box; bolt positions outside the z range are dropped (bolts
    /// deeper in a section leaked into a plate's bolt chain), and a bolt with none left fails.
    /// Objects without model geometry fail any geometry filter. Name/profile are substrings.
    /// </summary>
    public static bool Matches(DrawingModelGeometry? geometry, DrawingObjectQuery query)
    {
        var hasGeometryFilter = query.ZMin.HasValue || query.ZMax.HasValue ||
                                query.RegionMin != null || query.RegionMax != null;
        var hasTextFilter = !string.IsNullOrWhiteSpace(query.NameContains) ||
                            !string.IsNullOrWhiteSpace(query.ProfileContains);
        if (!hasGeometryFilter && !hasTextFilter) return true;
        if (geometry == null) return false;

        if (!Contains(geometry.Name, query.NameContains) || !Contains(geometry.Profile, query.ProfileContains))
            return false;
        if (!hasGeometryFilter) return true;

        if (geometry.BoltPositions != null && (query.ZMin.HasValue || query.ZMax.HasValue))
        {
            geometry.BoltPositions = geometry.BoltPositions
                .Where(p => (!query.ZMin.HasValue || p.Z >= query.ZMin.Value - Tolerance) &&
                            (!query.ZMax.HasValue || p.Z <= query.ZMax.Value + Tolerance))
                .ToList();
            if (geometry.BoltPositions.Count == 0) return false;
            if (TryBox(geometry.BoltPositions, out var boltMin, out var boltMax))
            {
                geometry.Min = boltMin;
                geometry.Max = boltMax;
            }
        }

        if (geometry.Min == null || geometry.Max == null) return false;
        if (query.ZMin.HasValue && geometry.Max.Z < query.ZMin.Value - Tolerance) return false;
        if (query.ZMax.HasValue && geometry.Min.Z > query.ZMax.Value + Tolerance) return false;
        if (query.RegionMin != null &&
            (geometry.Max.X < query.RegionMin.X - Tolerance || geometry.Max.Y < query.RegionMin.Y - Tolerance))
            return false;
        if (query.RegionMax != null &&
            (geometry.Min.X > query.RegionMax.X + Tolerance || geometry.Min.Y > query.RegionMax.Y + Tolerance))
            return false;
        return true;
    }

    private const double Tolerance = 0.01;

    private static bool Contains(string? value, string? part) =>
        string.IsNullOrWhiteSpace(part) ||
        (value ?? "").IndexOf(part!.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
}
