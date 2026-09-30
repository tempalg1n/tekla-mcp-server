using System;
using System.Collections.Generic;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.FileExchange;

/// <summary>Shape of one exported cell. Drives both the JSON shape and the CSV column count.</summary>
public enum RowValueKind
{
    Text,
    Integer,
    Number,
    /// <summary>A single point: JSON <c>{"x":…,"y":…,"z":…}</c>, CSV three prefixed columns.</summary>
    Point,
    /// <summary>Solid world AABB: JSON <c>{"minX":…,…}</c>, CSV six columns minX…maxZ.</summary>
    Aabb,
    /// <summary>Origin + X/Y/Z axes: JSON four nested points, CSV twelve cs* columns.</summary>
    CoordSystem,
    /// <summary>A polygon: JSON array of points, CSV one packed "x y z;x y z" column.</summary>
    Points,
}

/// <summary>
/// One exported cell. A cell always carries its KIND even when it has no value, so a row built
/// from an empty record can serve as the header prototype (see <see cref="DataFileWriters"/>).
/// </summary>
public sealed class RowValue
{
    private RowValue(RowValueKind kind) => Kind = kind;

    public RowValueKind Kind { get; }
    public string? TextValue { get; private set; }
    public long? IntegerValue { get; private set; }
    public double? NumberValue { get; private set; }
    public Point3D? PointValue { get; private set; }
    /// <summary>CSV column prefix for <see cref="RowValueKind.Point"/> ("start" → startX/Y/Z).</summary>
    public string PointPrefix { get; private set; } = "";
    /// <summary>minX, minY, minZ, maxX, maxY, maxZ — any element may be null.</summary>
    public double?[] Bounds { get; private set; } = new double?[6];
    public Point3D? Origin { get; private set; }
    public Point3D? AxisX { get; private set; }
    public Point3D? AxisY { get; private set; }
    public Point3D? AxisZ { get; private set; }
    public IReadOnlyList<Point3D>? PolygonValue { get; private set; }

    /// <summary>Decimal places used when formatting <see cref="RowValueKind.Number"/> and points.</summary>
    public int Decimals { get; private set; } = 2;

    public static RowValue Text(string? value) =>
        new RowValue(RowValueKind.Text) { TextValue = value };

    public static RowValue Integer(long? value) =>
        new RowValue(RowValueKind.Integer) { IntegerValue = value };

    public static RowValue Number(double? value, int decimals = 2) =>
        new RowValue(RowValueKind.Number) { NumberValue = value, Decimals = decimals };

    public static RowValue Point(Point3D? value, string csvPrefix, int decimals = 2) =>
        new RowValue(RowValueKind.Point) { PointValue = value, PointPrefix = csvPrefix, Decimals = decimals };

    public static RowValue Aabb(
        double? minX, double? minY, double? minZ, double? maxX, double? maxY, double? maxZ) =>
        new RowValue(RowValueKind.Aabb) { Bounds = new[] { minX, minY, minZ, maxX, maxY, maxZ } };

    public static RowValue CoordSystem(Point3D? origin, Point3D? axisX, Point3D? axisY, Point3D? axisZ) =>
        new RowValue(RowValueKind.CoordSystem) { Origin = origin, AxisX = axisX, AxisY = axisY, AxisZ = axisZ };

    public static RowValue Points(IReadOnlyList<Point3D>? value) =>
        new RowValue(RowValueKind.Points) { PolygonValue = value };
}

/// <summary>
/// One record on its way to a file: ordered (column name → cell) pairs. Reused across objects
/// during an export — a whole-model run produces 65k+ rows and there is no reason to allocate a
/// new row object for each.
/// </summary>
public sealed class DataRow
{
    private readonly List<KeyValuePair<string, RowValue>> _cells =
        new List<KeyValuePair<string, RowValue>>();

    public IReadOnlyList<KeyValuePair<string, RowValue>> Cells => _cells;

    public void Add(string name, RowValue value) =>
        _cells.Add(new KeyValuePair<string, RowValue>(name ?? "", value));

    public void Clear() => _cells.Clear();

    /// <summary>
    /// CSV column names this row expands to. Derived from the cell KINDS, so a prototype row
    /// built from an empty record yields exactly the header of a populated one.
    /// </summary>
    public IReadOnlyList<string> CsvColumns()
    {
        var columns = new List<string>();
        foreach (var cell in _cells)
        {
            var name = cell.Key;
            switch (cell.Value.Kind)
            {
                case RowValueKind.Point:
                    var prefix = string.IsNullOrEmpty(cell.Value.PointPrefix) ? name : cell.Value.PointPrefix;
                    columns.Add(prefix + "X");
                    columns.Add(prefix + "Y");
                    columns.Add(prefix + "Z");
                    break;
                case RowValueKind.Aabb:
                    columns.AddRange(new[] { "minX", "minY", "minZ", "maxX", "maxY", "maxZ" });
                    break;
                case RowValueKind.CoordSystem:
                    foreach (var part in new[] { "Origin", "AxisX", "AxisY", "AxisZ" })
                        foreach (var axis in new[] { "X", "Y", "Z" })
                            columns.Add("cs" + part + axis);
                    break;
                default:
                    columns.Add(name);
                    break;
            }
        }

        return columns;
    }
}
