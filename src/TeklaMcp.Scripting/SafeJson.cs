using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace TeklaMcp.Scripting;

/// <summary>
/// Defensive object → JSON renderer for script return values. Arbitrary Tekla objects can
/// hold cycles, remoting proxies and properties that throw, so instead of a real serializer
/// this walks public properties and fields with hard caps (depth, item count, total size) and
/// per-member try/catch; containers nested past the depth cap become an explicit
/// <c>"[depth limit reached: …]"</c> marker. Output is for the agent's eyes — best effort,
/// never an exception.
/// </summary>
public static class SafeJson
{
    // Container levels (lists, dictionaries, objects) that get expanded; leaves never count.
    // Together with the item and total-size caps this is what terminates cyclic graphs.
    internal const int MaxDepth = 6;
    internal const int MaxItems = 100;
    internal const int MaxProperties = 25; // per object: properties and fields together
    internal const int MaxStringLength = 4_000;
    internal const int MaxTotalLength = 64_000;
    private const int MaxTruncatedPreviewLength = 16_000;

    public static string ToJson(object? value) => ToJson(value, out _);

    /// <summary>
    /// Same rendering, plus an out-of-band account of every cap that fired. The markers inside
    /// the JSON are for reading; <paramref name="report"/> is what a caller must check before
    /// treating the value as complete — a script returning <c>new { truncated = true }</c> looks
    /// exactly like the size envelope, and a capped list still parses as a valid, shorter list.
    /// </summary>
    public static string ToJson(object? value, out SafeJsonReport report)
    {
        report = new SafeJsonReport();
        var sb = new StringBuilder();
        try
        {
            Write(sb, value, MaxDepth, report);
        }
        catch (Exception ex)
        {
            report.SerializationFailed = true;
            return "\"<serialization failed: " + Escape(ex.Message) + ">\"";
        }

        if (sb.Length > MaxTotalLength)
        {
            report.SizeCapExceeded = true;
            return TruncatedEnvelope(sb);
        }
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

    private static void Write(StringBuilder sb, object? value, int depth, SafeJsonReport r)
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
            case string s: WriteString(sb, s, r); return;
            case char c: WriteString(sb, c.ToString(), r); return;
            case float f: WriteDouble(sb, f, r); return;
            case double d: WriteDouble(sb, d, r); return;
            case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
            case DateTime dt: WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture), r); return;
            case Guid g: WriteString(sb, g.ToString(), r); return;
            case Enum e: WriteString(sb, e.ToString(), r); return;
        }

        if (value is sbyte || value is byte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong)
        {
            sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        if (depth <= 0)
        {
            // Not ToString(): for a collection that is its CLR type name
            // ("System.Collections.Generic.List`1[System.Double]"), indistinguishable from data.
            r.DepthLimitHits++;
            WriteString(sb, DepthLimitMarker(value), r);
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
                    r.NoteCappedCollection(dict.Count);
                    if (i > 0) sb.Append(',');
                    WriteString(sb, "…", r);
                    sb.Append(':');
                    WriteString(sb, "+" + (dict.Count - MaxItems) + " more entries (capped)", r);
                    break;
                }
                if (i++ > 0) sb.Append(',');
                WriteString(sb, Stringify(entry.Key), r);
                sb.Append(':');
                Write(sb, entry.Value, depth - 1, r);
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
                    // Only a cheap Count; a lazy sequence is never enumerated to its end.
                    r.NoteCappedCollection(seq is ICollection col ? SafeCount(col) : -1);
                    if (i > 0) sb.Append(',');
                    WriteString(sb, "…more items (capped at " + MaxItems + ")", r);
                    break;
                }
                if (i++ > 0) sb.Append(',');
                Write(sb, item, depth - 1, r);
            }
            sb.Append(']');
            return;
        }

        WriteObject(sb, value, depth, r);
    }

    private static void WriteObject(StringBuilder sb, object value, int depth, SafeJsonReport r)
    {
        List<(string Name, Func<object?> Read)> members;
        try
        {
            members = ReadableMembers(value);
        }
        catch
        {
            WriteString(sb, Stringify(value), r);
            return;
        }

        // ToString() only when there is nothing else to show: for an object with members it is
        // usually a CLR type name or a lossy format (Tekla's Point prints "(1000,000, …)").
        if (members.Count == 0)
        {
            WriteString(sb, Stringify(value), r);
            return;
        }

        if (members.Count > MaxProperties)
        {
            r.PropertyCappedObjects++;
            members.RemoveRange(MaxProperties, members.Count - MaxProperties);
        }

        sb.Append('{');
        var first = true;
        foreach (var member in members)
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(sb, member.Name, r);
            sb.Append(':');
            var valueStart = sb.Length;
            try
            {
                Write(sb, member.Read(), depth - 1, r);
            }
            catch (Exception ex)
            {
                // A lazy value can fault half-written ("[1,2"); drop the fragment so the JSON stays
                // valid. Caps it already counted stay reported — overstating a cut is the safe side.
                sb.Length = valueStart;
                r.ThrowingProperties++;
                WriteString(sb, "<threw: " + BaseMessage(ex) + ">", r);
            }
        }
        sb.Append('}');
    }

    /// <summary>
    /// Public instance properties, then public instance fields (Tekla's <c>Point</c>/<c>Vector</c>
    /// keep X/Y/Z in fields) — all of them, so the caller can apply and report
    /// <see cref="MaxProperties"/>. A name is emitted once, so a member hidden with <c>new</c> is
    /// not repeated. Values are read lazily, one try/catch each.
    /// </summary>
    private static List<(string Name, Func<object?> Read)> ReadableMembers(object value)
    {
        var members = new List<(string Name, Func<object?> Read)>();
        var type = value.GetType();
        if (IsValueTuple(type))
        {
            AddTupleItems(members, value, 0);
            return members;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.CanRead && prop.GetIndexParameters().Length == 0 && names.Add(prop.Name))
                members.Add((prop.Name, () => prop.GetValue(value)));
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (names.Add(field.Name))
                members.Add((field.Name, () => field.GetValue(value)));
        }
        return members;
    }

    /// <summary>
    /// Tuple element names are compile-time only; at runtime a <c>ValueTuple</c> holds fields
    /// Item1..Item7 plus a nested <c>Rest</c> tuple from the 8th element on. Flatten it the way
    /// C# does (<c>t.Item8</c> is <c>t.Rest.Item1</c>) so an N-tuple renders as Item1..ItemN.
    /// </summary>
    private static void AddTupleItems(List<(string Name, Func<object?> Read)> members, object tuple, int offset)
    {
        var type = tuple.GetType();
        for (var i = 1; i <= 7; i++)
        {
            var item = type.GetField("Item" + i);
            if (item == null)
                return;
            members.Add(("Item" + (offset + i), () => item.GetValue(tuple)));
        }

        var rest = type.GetField("Rest")?.GetValue(tuple);
        if (rest != null && IsValueTuple(rest.GetType()))
            AddTupleItems(members, rest, offset + 7);
    }

    private static bool IsValueTuple(Type type) =>
        type.IsValueType && type.IsGenericType && type.Namespace == "System" &&
        type.Name.StartsWith("ValueTuple`", StringComparison.Ordinal);

    /// <summary>
    /// Explicit stand-in for a container past <see cref="MaxDepth"/>, e.g.
    /// <c>[depth limit reached: List&lt;Double&gt; with 3 items]</c>. Only a cheap
    /// <see cref="ICollection.Count"/> is read — lazy sequences are never enumerated here.
    /// </summary>
    private static string DepthLimitMarker(object value)
    {
        var marker = "[depth limit reached: " + TypeLabel(value.GetType());
        if (value is ICollection collection)
        {
            try
            {
                var count = collection.Count;
                marker += " with " + count.ToString(CultureInfo.InvariantCulture) +
                          (count == 1 ? " item" : " items");
            }
            catch
            {
                // The count is a nicety; the marker is explicit without it.
            }
        }
        return marker + "]";
    }

    /// <summary>Short C#-style name: <c>List&lt;Double&gt;</c>, <c>Double[]</c>, <c>anonymous object</c>.</summary>
    private static string TypeLabel(Type type)
    {
        if (type.IsArray)
            return TypeLabel(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";

        var name = type.Name;
        if (name.StartsWith("<>", StringComparison.Ordinal) && name.Contains("AnonymousType"))
            return "anonymous object";

        var tick = name.IndexOf('`');
        if (!type.IsGenericType || tick < 0)
            return name;
        return name.Substring(0, tick) +
               "<" + string.Join(", ", type.GetGenericArguments().Select(TypeLabel)) + ">";
    }

    private static void WriteDouble(StringBuilder sb, double d, SafeJsonReport r)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
            WriteString(sb, d.ToString(CultureInfo.InvariantCulture), r);
        else
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Shared by values, property names and dictionary keys, so a long key is counted as a cut
    /// string too — it is lost data either way. The cap notices themselves stay far below the cap.
    /// </summary>
    private static void WriteString(StringBuilder sb, string s, SafeJsonReport r)
    {
        if (s.Length > MaxStringLength)
        {
            r.NoteTruncatedString(s.Length);
            s = s.Substring(0, MaxStringLength) + "…";
        }
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

    private static int SafeCount(ICollection collection)
    {
        try { return collection.Count; }
        catch { return -1; }
    }

    private static string BaseMessage(Exception ex)
        => (ex.InnerException ?? ex).Message;
}
