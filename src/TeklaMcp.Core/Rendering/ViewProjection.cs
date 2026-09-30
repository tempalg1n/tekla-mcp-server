using System;
using System.Collections.Generic;
using System.Globalization;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Rendering;

/// <summary>
/// Orthographic camera for schematics and for rotating a live Tekla view. A view is defined by
/// the direction the camera LOOKS along — the same convention as Tekla's
/// <c>ViewCamera.DirectionVector</c> — plus an up vector derived from global Z (global Y when
/// looking straight up or down). Screen right = look × up, screen up = right × look.
/// </summary>
public sealed class ViewProjection
{
    /// <summary>Preset name → viewing direction (not normalized).</summary>
    private static readonly Dictionary<string, (double X, double Y, double Z)> Presets =
        new Dictionary<string, (double, double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["top"] = (0, 0, -1),    // plan, X right, Y up
            ["plan"] = (0, 0, -1),
            ["bottom"] = (0, 0, 1),
            ["front"] = (0, 1, 0),   // from −Y looking +Y: X right, Z up
            ["back"] = (0, -1, 0),
            ["left"] = (1, 0, 0),    // from −X looking +X: −Y right, Z up
            ["right"] = (-1, 0, 0),
            ["iso"] = (-1, 1, -1),   // from +X −Y +Z (south-east)
            ["iso_se"] = (-1, 1, -1),
            ["iso_sw"] = (1, 1, -1),
            ["iso_ne"] = (-1, -1, -1),
            ["iso_nw"] = (1, -1, -1),
        };

    /// <summary>Names accepted by <see cref="TryCreate"/> besides "dx,dy,dz".</summary>
    public static readonly string[] PresetNames =
        { "top", "bottom", "front", "back", "left", "right", "iso", "iso_sw", "iso_ne", "iso_nw" };

    public string Name { get; }

    /// <summary>Unit viewing direction (camera looks along it).</summary>
    public Vec3 Forward { get; }

    /// <summary>Unit global direction of screen right.</summary>
    public Vec3 Right { get; }

    /// <summary>Unit global direction of screen up.</summary>
    public Vec3 Up { get; }

    private ViewProjection(string name, Vec3 forward, Vec3 right, Vec3 up)
    {
        Name = name;
        Forward = forward;
        Right = right;
        Up = up;
    }

    /// <summary>
    /// Build from a preset (see <see cref="PresetNames"/>) or a viewing direction "dx,dy,dz".
    /// Null/blank = <paramref name="fallback"/>.
    /// </summary>
    public static bool TryCreate(string? view, out ViewProjection projection, out string error, string fallback = "iso")
    {
        projection = null!;
        error = "";
        var text = string.IsNullOrWhiteSpace(view) ? fallback : view!.Trim();

        if (Presets.TryGetValue(text, out var preset))
        {
            var name = text.ToLowerInvariant();
            if (name == "plan") name = "top";
            if (name == "iso_se") name = "iso";
            projection = FromDirection(name, new Vec3(preset.X, preset.Y, preset.Z));
            return true;
        }

        var parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var dx) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dy) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var dz))
        {
            var dir = new Vec3(dx, dy, dz);
            if (dir.Length < 1e-9)
            {
                error = "View direction '" + text + "' has zero length.";
                return false;
            }
            projection = FromDirection("custom", dir);
            return true;
        }

        error = "Unknown view '" + text + "'. Use one of " + string.Join(", ", PresetNames) +
                " or a viewing direction 'dx,dy,dz' (the direction the camera looks along).";
        return false;
    }

    /// <summary>Camera looking along <paramref name="direction"/>.</summary>
    public static ViewProjection FromDirection(string name, Vec3 direction)
    {
        var f = direction.Normalized();
        var upHint = Math.Abs(f.Z) > 0.999 ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);
        var r = Vec3.Cross(f, upHint).Normalized();
        var u = Vec3.Cross(r, f).Normalized();
        return new ViewProjection(name, f, r, u);
    }

    /// <summary>Screen coordinates in mm: u along <see cref="Right"/>, v along <see cref="Up"/>, depth along <see cref="Forward"/>.</summary>
    public (double U, double V, double Depth) Project(Point3D p)
    {
        var v = new Vec3(p.X, p.Y, p.Z);
        return (Vec3.Dot(v, Right), Vec3.Dot(v, Up), Vec3.Dot(v, Forward));
    }

    /// <summary>
    /// For views whose screen axes are global axes: the axis letter and sign of screen right and
    /// screen up (e.g. top → ("X", +1), ("Y", +1)). Null for oblique views.
    /// </summary>
    public ((string Axis, int Sign) Horizontal, (string Axis, int Sign) Vertical)? AxisAligned()
    {
        var h = AxisOf(Right);
        var v = AxisOf(Up);
        if (h == null || v == null) return null;
        return (h.Value, v.Value);
    }

    private static (string Axis, int Sign)? AxisOf(Vec3 d)
    {
        const double tol = 1e-6;
        if (Math.Abs(Math.Abs(d.X) - 1) < tol) return ("X", d.X > 0 ? 1 : -1);
        if (Math.Abs(Math.Abs(d.Y) - 1) < tol) return ("Y", d.Y > 0 ? 1 : -1);
        if (Math.Abs(Math.Abs(d.Z) - 1) < tol) return ("Z", d.Z > 0 ? 1 : -1);
        return null;
    }

    /// <summary>Short description of the viewpoint for titles, e.g. "top (plan)" or "custom (-0.37,-0.93,0.04)".</summary>
    public string Describe()
    {
        switch (Name)
        {
            case "top": return "TOP (plan)";
            case "bottom": return "BOTTOM";
            case "front": return "FRONT (looking +Y)";
            case "back": return "BACK (looking -Y)";
            case "left": return "LEFT (looking +X)";
            case "right": return "RIGHT (looking -X)";
            case "iso": return "ISO from +X-Y+Z";
            case "iso_sw": return "ISO from -X-Y+Z";
            case "iso_ne": return "ISO from +X+Y+Z";
            case "iso_nw": return "ISO from -X+Y+Z";
            default:
                return string.Format(CultureInfo.InvariantCulture, "VIEW dir ({0:0.##},{1:0.##},{2:0.##})",
                    Forward.X, Forward.Y, Forward.Z);
        }
    }
}

/// <summary>Small immutable 3D vector for projection math.</summary>
public readonly struct Vec3
{
    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public Vec3 Normalized()
    {
        var len = Length;
        return len < 1e-12 ? this : new Vec3(X / len, Y / len, Z / len);
    }

    public static Vec3 FromPoint(Point3D p) => new Vec3(p.X, p.Y, p.Z);
    public Point3D ToPoint(int decimals = 4) =>
        new Point3D(Math.Round(X, decimals), Math.Round(Y, decimals), Math.Round(Z, decimals));

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) =>
        new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
}
