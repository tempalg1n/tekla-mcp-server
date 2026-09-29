using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TeklaMcp.Core.FileExchange;

/// <summary>
/// One parsed record from an exchange file. A row that could not be parsed still comes back —
/// with <see cref="Error"/> set — so the import can report the bad line number instead of
/// silently dropping data.
/// </summary>
public sealed class DataFileRow
{
    /// <summary>1-based line number the record started on.</summary>
    public long LineNumber { get; set; }

    /// <summary>Zero-based index of this record among the data records (excludes a CSV header).</summary>
    public long RecordIndex { get; set; }

    /// <summary>Value of the GUID column; empty when the column was missing/blank.</summary>
    public string Guid { get; set; } = "";

    /// <summary>Every other column, in file order. Later duplicates win.</summary>
    public List<KeyValuePair<string, string>> Values { get; set; } =
        new List<KeyValuePair<string, string>>();

    /// <summary>Set when the record could not be parsed or has no GUID.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Streaming reader for the exchange formats: JSONL (one flat JSON object per line) and CSV
/// (RFC-4180 quoting, ',' or ';' auto-detected — Russian Excel writes ';').
///
/// Deliberately hand-rolled: Core carries no JSON dependency, and this only ever needs flat
/// scalar objects. Nested objects/arrays in a JSONL record are skipped, not an error — an
/// export file carries geometry the import does not care about.
/// </summary>
public sealed class DataFileReader
{
    private readonly string _format;
    private readonly string _guidColumn;

    public DataFileReader(string normalizedFormat, string guidColumn)
    {
        _format = normalizedFormat;
        _guidColumn = string.IsNullOrWhiteSpace(guidColumn) ? "guid" : guidColumn.Trim();
    }

    /// <summary>
    /// Pick the format: an explicit value wins, otherwise it is taken from the extension
    /// (".csv" → csv, anything else → jsonl).
    /// </summary>
    public static bool TryResolveFormat(string? format, string path, out string normalized, out string error)
    {
        if (!string.IsNullOrWhiteSpace(format))
            return DataFileWriters.TryNormalizeFormat(format, out normalized, out error);

        error = "";
        normalized = string.Equals(Path.GetExtension(path ?? ""), ".csv", StringComparison.OrdinalIgnoreCase)
            ? DataFileWriters.Csv
            : DataFileWriters.Jsonl;
        return true;
    }

    /// <summary>Stream records. Never throws on malformed content — see <see cref="DataFileRow.Error"/>.</summary>
    public IEnumerable<DataFileRow> Read(TextReader reader) =>
        _format == DataFileWriters.Csv ? ReadCsv(reader) : ReadJsonl(reader);

    // ------------------------------------------------------------------ JSONL ----

    private IEnumerable<DataFileRow> ReadJsonl(TextReader reader)
    {
        long lineNumber = 0;
        long recordIndex = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var row = new DataFileRow { LineNumber = lineNumber, RecordIndex = recordIndex++ };
            var cells = new List<KeyValuePair<string, string>>();
            if (!TryParseJsonObject(line, cells, out var error))
            {
                row.Error = error;
                yield return row;
                continue;
            }

            Distribute(row, cells);
            yield return row;
        }
    }

    // -------------------------------------------------------------------- CSV ----

    private IEnumerable<DataFileRow> ReadCsv(TextReader reader)
    {
        var headerLine = reader.ReadLine();
        if (headerLine is null) yield break;

        var delimiter = DetectDelimiter(headerLine);
        var header = ParseCsvLine(headerLine, delimiter);
        long lineNumber = 1;
        long recordIndex = 0;

        foreach (var record in ParseCsvRecords(reader, delimiter, lineNumber))
        {
            // A trailing newline produces one empty field — not a record.
            if (record.Fields.Count == 1 && record.Fields[0].Length == 0) continue;

            var row = new DataFileRow { LineNumber = record.StartLine, RecordIndex = recordIndex++ };
            if (record.Fields.Count != header.Count)
            {
                row.Error = $"Row has {record.Fields.Count} fields, header has {header.Count}.";
                yield return row;
                continue;
            }

            var cells = new List<KeyValuePair<string, string>>(header.Count);
            for (var i = 0; i < header.Count; i++)
                cells.Add(new KeyValuePair<string, string>(header[i], record.Fields[i]));

            Distribute(row, cells);
            yield return row;
        }
    }

    /// <summary>
    /// Pick ',' or ';' by whichever the header uses more (outside quotes). Excel in a Russian
    /// locale writes ';' and would otherwise parse as a single column.
    /// </summary>
    private static char DetectDelimiter(string headerLine)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        var inQuotes = false;
        foreach (var c in headerLine)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (inQuotes) continue;
            else if (c == ',') commas++;
            else if (c == ';') semicolons++;
            else if (c == '\t') tabs++;
        }

        if (semicolons > commas && semicolons >= tabs) return ';';
        if (tabs > commas && tabs > semicolons) return '\t';
        return ',';
    }

    private sealed class CsvRecord
    {
        public List<string> Fields { get; set; } = new List<string>();
        public long StartLine { get; set; }
    }

    private static List<string> ParseCsvLine(string line, char delimiter)
    {
        using (var reader = new StringReader(line))
        {
            foreach (var record in ParseCsvRecords(reader, delimiter, 0))
                return record.Fields;
        }
        return new List<string>();
    }

    /// <summary>
    /// Character-level RFC-4180 parser: honours doubled quotes and newlines inside quoted
    /// fields, and tracks the line a record started on for error reporting.
    /// </summary>
    private static IEnumerable<CsvRecord> ParseCsvRecords(TextReader reader, char delimiter, long linesConsumed)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = linesConsumed + 1;
        var recordStartLine = line;
        var started = false;
        int read;

        while ((read = reader.Read()) >= 0)
        {
            var c = (char)read;
            started = true;

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }

            if (c == '"' && field.Length == 0) { inQuotes = true; continue; }

            if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && reader.Peek() == '\n') reader.Read();
                line++;
                fields.Add(field.ToString());
                field.Clear();
                yield return new CsvRecord { Fields = fields, StartLine = recordStartLine };
                fields = new List<string>();
                recordStartLine = line;
                started = false;
                continue;
            }

            field.Append(c);
        }

        if (started || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return new CsvRecord { Fields = fields, StartLine = recordStartLine };
        }
    }

    // ----------------------------------------------------------------- shared ----

    /// <summary>Split parsed cells into the GUID and everything else.</summary>
    private void Distribute(DataFileRow row, List<KeyValuePair<string, string>> cells)
    {
        foreach (var cell in cells)
        {
            if (string.Equals(cell.Key, _guidColumn, StringComparison.OrdinalIgnoreCase))
            {
                row.Guid = (cell.Value ?? "").Trim();
                continue;
            }
            row.Values.Add(cell);
        }

        if (row.Guid.Length == 0)
            row.Error = $"No value in the '{_guidColumn}' column.";
    }

    // ------------------------------------------------------------- JSON parser ----

    /// <summary>
    /// Parse one flat JSON object. Nested objects/arrays are skipped (an export row carries
    /// geometry an import ignores); explicit nulls are treated as "column absent". Numbers keep
    /// their literal text so a UDA write can re-type them exactly as written.
    /// </summary>
    internal static bool TryParseJsonObject(
        string text, List<KeyValuePair<string, string>> into, out string error)
    {
        error = "";
        var i = 0;
        SkipWhitespace(text, ref i);
        if (i >= text.Length || text[i] != '{')
        {
            error = "Line is not a JSON object (expected '{').";
            return false;
        }
        i++;

        SkipWhitespace(text, ref i);
        if (i < text.Length && text[i] == '}') return true;

        while (i < text.Length)
        {
            SkipWhitespace(text, ref i);
            if (i >= text.Length || text[i] != '"')
            {
                error = $"Expected a quoted property name at position {i}.";
                return false;
            }

            if (!TryReadJsonString(text, ref i, out var key, out error)) return false;

            SkipWhitespace(text, ref i);
            if (i >= text.Length || text[i] != ':')
            {
                error = $"Expected ':' after property '{key}'.";
                return false;
            }
            i++;
            SkipWhitespace(text, ref i);

            if (i >= text.Length)
            {
                error = $"Property '{key}' has no value.";
                return false;
            }

            if (text[i] == '"')
            {
                if (!TryReadJsonString(text, ref i, out var value, out error)) return false;
                into.Add(new KeyValuePair<string, string>(key, value));
            }
            else if (text[i] == '{' || text[i] == '[')
            {
                if (!TrySkipJsonContainer(text, ref i, out error)) return false;
            }
            else
            {
                var start = i;
                while (i < text.Length && text[i] != ',' && text[i] != '}' && !char.IsWhiteSpace(text[i])) i++;
                var token = text.Substring(start, i - start);
                if (token.Length == 0)
                {
                    error = $"Property '{key}' has no value.";
                    return false;
                }
                // null means "not set" — an import must not clear a UDA by accident.
                if (!string.Equals(token, "null", StringComparison.Ordinal))
                    into.Add(new KeyValuePair<string, string>(key, token));
            }

            SkipWhitespace(text, ref i);
            if (i < text.Length && text[i] == ',') { i++; continue; }
            if (i < text.Length && text[i] == '}') return true;

            error = $"Expected ',' or '}}' after property '{key}'.";
            return false;
        }

        error = "Unterminated JSON object.";
        return false;
    }

    private static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
    }

    private static bool TryReadJsonString(string text, ref int i, out string value, out string error)
    {
        value = "";
        error = "";
        var sb = new StringBuilder();
        i++; // opening quote

        while (i < text.Length)
        {
            var c = text[i++];
            if (c == '"') { value = sb.ToString(); return true; }

            if (c != '\\') { sb.Append(c); continue; }

            if (i >= text.Length) break;
            var escape = text[i++];
            switch (escape)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > text.Length)
                    {
                        error = "Truncated \\u escape.";
                        return false;
                    }
                    var hex = text.Substring(i, 4);
                    if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        error = $"Invalid \\u escape '\\u{hex}'.";
                        return false;
                    }
                    sb.Append((char)code);
                    i += 4;
                    break;
                default:
                    error = $"Invalid escape '\\{escape}'.";
                    return false;
            }
        }

        error = "Unterminated string.";
        return false;
    }

    private static bool TrySkipJsonContainer(string text, ref int i, out string error)
    {
        error = "";
        var depth = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                if (!TryReadJsonString(text, ref i, out _, out error)) return false;
                continue;
            }

            i++;
            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']')
            {
                depth--;
                if (depth == 0) return true;
            }
        }

        error = "Unterminated nested value.";
        return false;
    }
}
