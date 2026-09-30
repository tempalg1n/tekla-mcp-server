using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Geometry;

/// <summary>
/// Backend-agnostic side of <c>tekla_get_part_solid</c>: caps, truncation honesty, loop areas and
/// the outer-loop pick. Backends feed faces into a <see cref="SolidBuilder"/>; once it is full it
/// only counts, so a live backend stops reading vertices over remoting.
/// </summary>
public static class SolidMath
{
    public const int MaxFacesLimit = 5000;
    public const int MaxPointsLimit = 50000;

    /// <summary>|area| of a planar polygon, by Newell's method projected on its normal.</summary>
    public static double LoopArea(IReadOnlyList<Point3D> vertices, Point3D normal)
    {
        if (vertices.Count < 3) return 0;
        double nx = 0, ny = 0, nz = 0;
        for (var i = 0; i < vertices.Count; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }
        var length = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
        if (length < 1e-12)
            return Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
        return Math.Abs(nx * normal.X + ny * normal.Y + nz * normal.Z) / length / 2;
    }

    /// <summary>The six faces of an axis-aligned box, outward normals, counter-clockwise loops.</summary>
    public static IEnumerable<(Point3D Normal, List<Point3D> Loop)> BoxFaces(Point3D min, Point3D max)
    {
        Point3D P(double x, double y, double z) => new Point3D(x, y, z);
        yield return (P(0, 0, -1), new List<Point3D> { P(min.X, min.Y, min.Z), P(min.X, max.Y, min.Z), P(max.X, max.Y, min.Z), P(max.X, min.Y, min.Z) });
        yield return (P(0, 0, 1), new List<Point3D> { P(min.X, min.Y, max.Z), P(max.X, min.Y, max.Z), P(max.X, max.Y, max.Z), P(min.X, max.Y, max.Z) });
        yield return (P(0, -1, 0), new List<Point3D> { P(min.X, min.Y, min.Z), P(max.X, min.Y, min.Z), P(max.X, min.Y, max.Z), P(min.X, min.Y, max.Z) });
        yield return (P(0, 1, 0), new List<Point3D> { P(min.X, max.Y, min.Z), P(min.X, max.Y, max.Z), P(max.X, max.Y, max.Z), P(max.X, max.Y, min.Z) });
        yield return (P(-1, 0, 0), new List<Point3D> { P(min.X, min.Y, min.Z), P(min.X, min.Y, max.Z), P(min.X, max.Y, max.Z), P(min.X, max.Y, min.Z) });
        yield return (P(1, 0, 0), new List<Point3D> { P(max.X, min.Y, min.Z), P(max.X, max.Y, min.Z), P(max.X, max.Y, max.Z), P(max.X, min.Y, max.Z) });
    }

    public static Point3D Round(Point3D p, int digits) =>
        new Point3D(Math.Round(p.X, digits), Math.Round(p.Y, digits), Math.Round(p.Z, digits));
}

/// <summary>Collects faces under the request's caps and writes the honest summary.</summary>
public sealed class SolidBuilder
{
    private readonly int _maxFaces;
    private readonly int _maxPoints;
    private readonly List<SolidFace> _faces = new List<SolidFace>();
    private int _faceCount;
    private int _points;
    private string? _truncatedReason;

    public SolidBuilder(int maxFaces, int maxPoints)
    {
        _maxFaces = Math.Max(0, Math.Min(maxFaces <= 0 ? 200 : maxFaces, SolidMath.MaxFacesLimit));
        _maxPoints = Math.Max(0, Math.Min(maxPoints <= 0 ? 1000 : maxPoints, SolidMath.MaxPointsLimit));
    }

    /// <summary>True once no further face will be returned — the caller may stop reading vertices.</summary>
    public bool Full => _truncatedReason != null;

    /// <summary>
    /// Counts the face and, while not full, reads its loops. A face that would cross the point cap
    /// is left out whole (never cut), and the builder turns full.
    /// </summary>
    public void Add(Point3D normal, int? originPartId, Func<IEnumerable<IEnumerable<Point3D>>> loops)
    {
        _faceCount++;
        if (Full) return;
        if (_faces.Count >= _maxFaces)
        {
            _truncatedReason = $"maxFaces={_maxFaces} reached";
            return;
        }

        var face = new SolidFace { Normal = SolidMath.Round(normal, 6), OriginPartId = originPartId };
        var points = 0;
        foreach (var loop in loops())
        {
            var vertices = loop.Select(p => SolidMath.Round(p, 3)).ToList();
            points += vertices.Count;
            face.Loops.Add(new SolidLoop { Vertices = vertices });
        }

        if (_points + points > _maxPoints)
        {
            _truncatedReason = $"maxPoints={_maxPoints} reached";
            return;
        }

        var outer = 0;
        for (var i = 0; i < face.Loops.Count; i++)
        {
            face.Loops[i].Area = Math.Round(SolidMath.LoopArea(face.Loops[i].Vertices, normal), 1);
            if (face.Loops[i].Area > face.Loops[outer].Area) outer = i;
        }
        face.OuterLoopIndex = outer;
        _points += points;
        _faces.Add(face);
    }

    public void Fill(PartSolidGeometry target)
    {
        target.Faces = _faces;
        target.FaceCount = _faceCount;
        target.FacesReturned = _faces.Count;
        target.PointsReturned = _points;
        target.Truncated = _truncatedReason != null;
        target.TruncatedReason = _truncatedReason == null
            ? null
            : _truncatedReason + $": {_faces.Count} of {_faceCount} faces returned — the list is NOT the whole " +
              "shape. Raise maxFaces/maxPoints, or use the AABB only as a box.";
    }
}
