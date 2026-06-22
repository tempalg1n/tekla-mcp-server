using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>Shared parsing + query helpers for the write/generator tools.</summary>
internal static class ToolHelpers
{
    /// <summary>Parse "x,y,z" (mm) into a point, or null if not three numbers.</summary>
    public static Point3D? ParsePoint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var nums = ParseNums(raw);
        return nums.Count >= 3 ? new Point3D(nums[0], nums[1], nums[2]) : null;
    }

    /// <summary>Parse "x,y,z; x,y,z; ..." into a list of points.</summary>
    public static List<Point3D> ParsePoints(string? raw)
    {
        var result = new List<Point3D>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var chunk in raw!.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = ParsePoint(chunk);
            if (p != null) result.Add(p);
        }
        return result;
    }

    /// <summary>Parse a delimited list of numbers, e.g. "0,6000,12000".</summary>
    public static List<double> ParseNums(string? raw)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var token in raw!.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token.Trim().Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                result.Add(v);
        }
        return result;
    }

    /// <summary>Parse a comma/semicolon/newline separated string list.</summary>
    public static List<string> ParseList(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var part in raw!.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) result.Add(trimmed);
        }
        return result;
    }

    public static ObjectQuery BuildQuery(
        string? type = null,
        string? @class = null,
        string? profile = null,
        string? material = null,
        string? nameContains = null,
        string? udaName = null,
        string? udaEquals = null,
        string? guidIn = null,
        bool useSelection = false) =>
        new ObjectQuery
        {
            Type = type,
            Class = @class,
            Profile = profile,
            Material = material,
            NameContains = nameContains,
            UdaName = udaName,
            UdaEquals = udaEquals,
            GuidIn = ParseList(guidIn),
            UseSelection = useSelection,
        };

    /// <summary>True if the object has both endpoints (i.e. a linear member with geometry).</summary>
    public static bool HasEnds(ModelObjectInfo o) =>
        o.StartX.HasValue && o.StartY.HasValue && o.StartZ.HasValue &&
        o.EndX.HasValue && o.EndY.HasValue && o.EndZ.HasValue;

    public static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
}
