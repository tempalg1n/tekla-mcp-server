using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.FileExchange;

/// <summary>Streams <see cref="DataRow"/>s to a file. One row at a time — never buffers a model.</summary>
public interface IDataFileWriter : IDisposable
{
    void Write(DataRow row);
    void Flush();
}

/// <summary>Factory for the supported export formats.</summary>
public static class DataFileWriters
{
    public const string Jsonl = "jsonl";
    public const string Csv = "csv";

    /// <summary>Normalize a caller-supplied format; empty/null means JSONL.</summary>
    public static bool TryNormalizeFormat(string? format, out string normalized, out string error)
    {
        error = "";
        normalized = (format ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0) normalized = Jsonl;
        if (normalized == "json") normalized = Jsonl;
        if (normalized == Jsonl || normalized == Csv) return true;

        error = $"Unsupported format '{format}'. Use 'jsonl' (default) or 'csv'.";
        normalized = Jsonl;
        return false;
    }

    /// <summary>
    /// Create a writer. <paramref name="prototype"/> is a row of the same shape as the data
    /// (built from an empty record) — CSV takes its header from it, so an export that matched
    /// nothing still produces a headed file.
    /// </summary>
    public static IDataFileWriter Create(
        TextWriter writer, string normalizedFormat, DataRow prototype, bool writeHeader) =>
        normalizedFormat == Csv
            ? (IDataFileWriter)new CsvDataWriter(writer, prototype, writeHeader)
            : new JsonlDataWriter(writer);
}

/// <summary>One JSON object per line — the format the offline matching step reads.</summary>
public sealed class JsonlDataWriter : IDataFileWriter
{
    private readonly TextWriter _writer;
    private readonly StringBuilder _buffer = new StringBuilder(512);

    public JsonlDataWriter(TextWriter writer) => _writer = writer;

    public void Write(DataRow row)
    {
        _buffer.Clear();
        _buffer.Append('{');
        var first = true;
        foreach (var cell in row.Cells)
        {
            if (!first) _buffer.Append(',');
            first = false;
            JsonText.AppendString(_buffer, cell.Key);
            _buffer.Append(':');
            AppendValue(_buffer, cell.Value);
        }
        _buffer.Append('}');
        _writer.WriteLine(_buffer.ToString());
    }

    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Flush();

    private static void AppendValue(StringBuilder sb, RowValue value)
    {
        switch (value.Kind)
        {
            case RowValueKind.Text:
                if (value.TextValue is null) sb.Append("null");
                else JsonText.AppendString(sb, value.TextValue);
                break;

            case RowValueKind.Integer:
                sb.Append(value.IntegerValue?.ToString(CultureInfo.InvariantCulture) ?? "null");
                break;

            case RowValueKind.Number:
                sb.Append(JsonText.Number(value.NumberValue, value.Decimals));
                break;

            case RowValueKind.Point:
                AppendPoint(sb, value.PointValue, value.Decimals);
                break;

            case RowValueKind.Aabb:
                var names = new[] { "minX", "minY", "minZ", "maxX", "maxY", "maxZ" };
                var any = false;
                foreach (var bound in value.Bounds) any |= bound.HasValue;
                if (!any) { sb.Append("null"); break; }
                sb.Append('{');
                for (var i = 0; i < names.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    JsonText.AppendString(sb, names[i]);
                    sb.Append(':').Append(JsonText.Number(value.Bounds[i], 2));
                }
                sb.Append('}');
                break;

            case RowValueKind.CoordSystem:
                if (value.Origin is null && value.AxisX is null && value.AxisY is null && value.AxisZ is null)
                {
                    sb.Append("null");
                    break;
                }
                sb.Append("{\"origin\":");
                AppendPoint(sb, value.Origin, 2);
                sb.Append(",\"axisX\":");
                AppendPoint(sb, value.AxisX, 6);
                sb.Append(",\"axisY\":");
                AppendPoint(sb, value.AxisY, 6);
                sb.Append(",\"axisZ\":");
                AppendPoint(sb, value.AxisZ, 6);
                sb.Append('}');
                break;

            case RowValueKind.Points:
                if (value.PolygonValue is null) { sb.Append("null"); break; }
                sb.Append('[');
                for (var i = 0; i < value.PolygonValue.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendPoint(sb, value.PolygonValue[i], 2);
                }
                sb.Append(']');
                break;

            default:
                sb.Append("null");
                break;
        }
    }

    private static void AppendPoint(StringBuilder sb, Point3D? point, int decimals)
    {
        if (point is null) { sb.Append("null"); return; }
        sb.Append("{\"x\":").Append(JsonText.Number(point.X, decimals))
          .Append(",\"y\":").Append(JsonText.Number(point.Y, decimals))
          .Append(",\"z\":").Append(JsonText.Number(point.Z, decimals))
          .Append('}');
    }
}

/// <summary>
/// RFC-4180 CSV. Composite cells expand to several columns (AABB → minX…maxZ, points →
/// prefixed triples); a contour polygon becomes one packed "x y z;x y z" column, because a
/// variable-length polygon has no fixed column count.
/// </summary>
public sealed class CsvDataWriter : IDataFileWriter
{
    private readonly TextWriter _writer;
    private readonly char _delimiter;
    private readonly StringBuilder _buffer = new StringBuilder(512);

    public CsvDataWriter(TextWriter writer, DataRow prototype, bool writeHeader, char delimiter = ',')
    {
        _writer = writer;
        _delimiter = delimiter;
        if (!writeHeader) return;

        var first = true;
        foreach (var column in prototype.CsvColumns())
        {
            if (!first) _buffer.Append(_delimiter);
            first = false;
            _buffer.Append(Escape(column));
        }
        _writer.WriteLine(_buffer.ToString());
        _buffer.Clear();
    }

    public void Write(DataRow row)
    {
        _buffer.Clear();
        var first = true;

        void Cell(string text)
        {
            if (!first) _buffer.Append(_delimiter);
            first = false;
            _buffer.Append(Escape(text));
        }

        foreach (var cell in row.Cells)
        {
            var value = cell.Value;
            switch (value.Kind)
            {
                case RowValueKind.Text:
                    Cell(value.TextValue ?? "");
                    break;
                case RowValueKind.Integer:
                    Cell(value.IntegerValue?.ToString(CultureInfo.InvariantCulture) ?? "");
                    break;
                case RowValueKind.Number:
                    Cell(Plain(value.NumberValue, value.Decimals));
                    break;
                case RowValueKind.Point:
                    Cell(Plain(value.PointValue?.X, value.Decimals));
                    Cell(Plain(value.PointValue?.Y, value.Decimals));
                    Cell(Plain(value.PointValue?.Z, value.Decimals));
                    break;
                case RowValueKind.Aabb:
                    foreach (var bound in value.Bounds) Cell(Plain(bound, 2));
                    break;
                case RowValueKind.CoordSystem:
                    var axes = new[] { value.Origin, value.AxisX, value.AxisY, value.AxisZ };
                    for (var i = 0; i < axes.Length; i++)
                    {
                        // The origin is millimetres; the axes are unit vectors and need more digits.
                        var decimals = i == 0 ? 2 : 6;
                        Cell(Plain(axes[i]?.X, decimals));
                        Cell(Plain(axes[i]?.Y, decimals));
                        Cell(Plain(axes[i]?.Z, decimals));
                    }
                    break;
                case RowValueKind.Points:
                    Cell(PackPolygon(value.PolygonValue));
                    break;
                default:
                    Cell("");
                    break;
            }
        }

        _writer.WriteLine(_buffer.ToString());
    }

    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Flush();

    private static string PackPolygon(IReadOnlyList<Point3D>? points)
    {
        if (points is null || points.Count == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < points.Count; i++)
        {
            if (i > 0) sb.Append(';');
            sb.Append(Plain(points[i].X, 2)).Append(' ')
              .Append(Plain(points[i].Y, 2)).Append(' ')
              .Append(Plain(points[i].Z, 2));
        }
        return sb.ToString();
    }

    private static string Plain(double? value, int decimals) =>
        JsonText.Number(value, decimals) is var text && text == "null" ? "" : text;

    private string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.IndexOf('"') < 0 && value.IndexOf(_delimiter) < 0 &&
            value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>
/// Minimal JSON text helpers. Core carries no JSON dependency (see the project comment in
/// TeklaMcp.Core.csproj) and the existing parsers here are hand-rolled for the same reason.
/// </summary>
internal static class JsonText
{
    /// <summary>Append a quoted, escaped JSON string.</summary>
    public static void AppendString(StringBuilder sb, string? value)
    {
        sb.Append('"');
        if (!string.IsNullOrEmpty(value))
        {
            foreach (var c in value!)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// A JSON number, or "null". NaN/Infinity are not valid JSON, so they become null rather
    /// than silently producing a file no parser will accept. Fixed-point on purpose: "R" is
    /// allowed to emit exponent notation and, on .NET Framework, occasionally 17 noise digits —
    /// neither is welcome in a coordinate file a human may open.
    /// </summary>
    public static string Number(double? value, int decimals)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return "null";
        if (decimals < 0) decimals = 0;
        if (decimals > 12) decimals = 12;
        var format = decimals == 0 ? "0" : "0." + new string('#', decimals);
        return Math.Round(value.Value, decimals).ToString(format, CultureInfo.InvariantCulture);
    }
}
