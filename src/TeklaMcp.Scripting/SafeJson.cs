using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace TeklaMcp.Scripting;

/// <summary>
/// Defensive object → JSON renderer for script return values. Arbitrary Tekla objects can
/// hold cycles, remoting proxies and properties that throw, so instead of a real serializer
/// this walks the value with hard caps (depth, item count, total size) and per-property
/// try/catch, degrading to <c>ToString()</c>. Output is for the agent's eyes — best effort,
/// never an exception.
/// </summary>
public static class SafeJson
{
    private const int MaxDepth = 4;
    private const int MaxItems = 100;
    private const int MaxProperties = 25;
    private const int MaxStringLength = 4_000;
    private const int MaxTotalLength = 64_000;
    private const int MaxTruncatedPreviewLength = 16_000;

    public static string ToJson(object? value)
    {
        var sb = new StringBuilder();
        try
        {
            Write(sb, value, MaxDepth);
        }
        catch (Exception ex)
        {
            return "\"<serialization failed: " + Escape(ex.Message) + ">\"";
        }

        if (sb.Length > MaxTotalLength)
            return TruncatedEnvelope(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Never return a raw JSON prefix: cutting inside a string/object produced invalid JSON
    /// and forced clients to special-case large results. Preserve a bounded preview as a JSON
    /// string inside a small, always-valid envelope instead.
    /// </summary>
    private static string TruncatedEnvelope(StringBuilder captured)
    {
        var previewLength = Math.Min(captured.Length, MaxTruncatedPreviewLength);
        var preview = captured.ToString(0, previewLength);
        return "{\"truncated\":true,\"capturedLength\":" +
               captured.Length.ToString(CultureInfo.InvariantCulture) +
               ",\"preview\":\"" + Escape(preview) +
               "\",\"guidance\":\"Return a smaller or aggregated value.\"}";
    }

    private static void Write(StringBuilder sb, object? value, int depth)
    {
        if (sb.Length > MaxTotalLength)
            return;

        if (value == null)
        {
            sb.Append("null");
            return;
        }

        switch (value)
        {
            case bool b: sb.Append(b ? "true" : "false"); return;
            case string s: WriteString(sb, s); return;
            case char c: WriteString(sb, c.ToString()); return;
            case float f: WriteDouble(sb, f); return;
            case double d: WriteDouble(sb, d); return;
            case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
            case DateTime dt: WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture)); return;
            case Guid g: WriteString(sb, g.ToString()); return;
            case Enum e: WriteString(sb, e.ToString()); return;
        }

        if (value is sbyte || value is byte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong)
        {
            sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        if (depth <= 0)
        {
            WriteString(sb, Stringify(value));
            return;
        }

        if (value is IDictionary dict)
        {
            sb.Append('{');
            var i = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (i >= MaxItems)
                {
                    if (i > 0) sb.Append(',');
                    WriteString(sb, "…");
                    sb.Append(':');
                    WriteString(sb, "+" + (dict.Count - MaxItems) + " more entries (capped)");
                    break;
                }
                if (i++ > 0) sb.Append(',');
                WriteString(sb, Stringify(entry.Key));
                sb.Append(':');
                Write(sb, entry.Value, depth - 1);
            }
            sb.Append('}');
            return;
        }

        if (value is IEnumerable seq)
        {
            sb.Append('[');
            var i = 0;
            foreach (var item in seq)
            {
                if (i >= MaxItems)
                {
                    if (i > 0) sb.Append(',');
                    WriteString(sb, "…more items (capped at " + MaxItems + ")");
                    break;
                }
                if (i++ > 0) sb.Append(',');
                Write(sb, item, depth - 1);
            }
            sb.Append(']');
            return;
        }

        WriteObject(sb, value, depth);
    }

    private static void WriteObject(StringBuilder sb, object value, int depth)
    {
        PropertyInfo[] props;
        try
        {
            props = value.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Take(MaxProperties)
                .ToArray();
        }
        catch
        {
            WriteString(sb, Stringify(value));
            return;
        }

        if (props.Length == 0)
        {
            WriteString(sb, Stringify(value));
            return;
        }

        sb.Append('{');
        var first = true;
        foreach (var prop in props)
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(sb, prop.Name);
            sb.Append(':');
            try
            {
                Write(sb, prop.GetValue(value), depth - 1);
            }
            catch (Exception ex)
            {
                WriteString(sb, "<threw: " + BaseMessage(ex) + ">");
            }
        }
        sb.Append('}');
    }

    private static void WriteDouble(StringBuilder sb, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
            WriteString(sb, d.ToString(CultureInfo.InvariantCulture));
        else
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        if (s.Length > MaxStringLength)
            s = s.Substring(0, MaxStringLength) + "…";
        sb.Append('"').Append(Escape(s)).Append('"');
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
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
        return sb.ToString();
    }

    private static string Stringify(object? value)
    {
        try
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }
        catch (Exception ex)
        {
            return "<ToString threw: " + BaseMessage(ex) + ">";
        }
    }

    private static string BaseMessage(Exception ex)
        => (ex.InnerException ?? ex).Message;
}
