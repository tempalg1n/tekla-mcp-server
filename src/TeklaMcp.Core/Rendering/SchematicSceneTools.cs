using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Rendering;

/// <summary>Scene helpers both backends share, so "focus", "region" and "context" mean the same thing.</summary>
public static class SchematicSceneTools
{
    /// <summary>True when the query names anything (filter, GUIDs or the selection) — i.e. there is a focus.</summary>
    public static bool HasScope(ObjectQuery? q)
    {
        if (q == null) return false;
        return q.UseSelection ||
               (q.GuidIn != null && q.GuidIn.Any(g => !string.IsNullOrWhiteSpace(g))) ||
               !string.IsNullOrWhiteSpace(q.Type) ||
               !string.IsNullOrWhiteSpace(q.Class) ||
               !string.IsNullOrWhiteSpace(q.Profile) ||
               !string.IsNullOrWhiteSpace(q.Material) ||
               !string.IsNullOrWhiteSpace(q.NameContains) ||
               (!string.IsNullOrWhiteSpace(q.UdaName) && (q.UdaIsEmpty || !string.IsNullOrWhiteSpace(q.UdaEquals))) ||
               !string.IsNullOrWhiteSpace(q.AttributeName);
    }

    /// <summary>World AABB of one item's geometry, or null when it has none.</summary>
    public static Box3D? BoundsOf(SchematicItem item)
    {
        if (item.Box != null) return item.Box;
        if (item.Points == null || item.Points.Count == 0) return null;
        return new Box3D(
            new Point3D(item.Points.Min(p => p.X), item.Points.Min(p => p.Y), item.Points.Min(p => p.Z)),
            new Point3D(item.Points.Max(p => p.X), item.Points.Max(p => p.Y), item.Points.Max(p => p.Z)));
    }

    /// <summary>Union AABB of the items, or null when none has geometry.</summary>
    public static Box3D? BoundsOf(IEnumerable<SchematicItem> items)
    {
        Box3D? total = null;
        foreach (var item in items)
        {
            var b = BoundsOf(item);
            if (b == null) continue;
            total = total == null
                ? new Box3D(new Point3D(b.Min.X, b.Min.Y, b.Min.Z), new Point3D(b.Max.X, b.Max.Y, b.Max.Z))
                : new Box3D(
                    new Point3D(Math.Min(total.Min.X, b.Min.X), Math.Min(total.Min.Y, b.Min.Y), Math.Min(total.Min.Z, b.Min.Z)),
                    new Point3D(Math.Max(total.Max.X, b.Max.X), Math.Max(total.Max.Y, b.Max.Y), Math.Max(total.Max.Z, b.Max.Z)));
        }
        return total;
    }

    public static Box3D Expand(Box3D box, double margin) =>
        new Box3D(
            new Point3D(box.Min.X - margin, box.Min.Y - margin, box.Min.Z - margin),
            new Point3D(box.Max.X + margin, box.Max.Y + margin, box.Max.Z + margin));

    public static bool Intersects(Box3D a, Box3D b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X &&
        a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y &&
        a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;

    /// <summary>Parse "minX,minY,minZ,maxX,maxY,maxZ" (mm); corners may be given in any order.</summary>
    public static bool TryParseBox(string? text, out Box3D box, out string error)
    {
        box = new Box3D();
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Region is empty.";
            return false;
        }
        var parts = text!.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var values = new List<double>();
        foreach (var p in parts)
        {
            if (!double.TryParse(p, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                error = "Region value '" + p + "' is not a number.";
                return false;
            }
            values.Add(v);
        }
        if (values.Count != 6)
        {
            error = "Region needs 6 numbers 'minX,minY,minZ,maxX,maxY,maxZ' (mm), got " + values.Count + ".";
            return false;
        }
        box = new Box3D(
            new Point3D(Math.Min(values[0], values[3]), Math.Min(values[1], values[4]), Math.Min(values[2], values[5])),
            new Point3D(Math.Max(values[0], values[3]), Math.Max(values[1], values[4]), Math.Max(values[2], values[5])));
        return true;
    }
}
