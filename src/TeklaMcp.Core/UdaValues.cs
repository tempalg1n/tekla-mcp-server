using System;
using System.Collections.Generic;
using System.Globalization;

namespace TeklaMcp.Core;

/// <summary>The value type of a user-defined attribute (Tekla's <c>PropertyTypeEnum</c>).</summary>
public enum UdaValueKind
{
    /// <summary>Not defined in the catalog and not learned yet.</summary>
    Unknown,
    String,
    Integer,
    Double,
}

/// <summary>
/// The three typed getters of ONE user-defined attribute on ONE object.
///
/// Verified live on Tekla 2021 (issue #21, 300 beams of model 3155, 2026-10-16): for a SET
/// attribute all three <c>GetUserProperty</c> overloads answer true, and only the one matching the
/// stored type returns the value — the string getter answers "" for a number, the int/double
/// getters answer 0 for a string. An attribute that is not set answers false from all three.
/// "The first getter that succeeds" therefore reads every number as blank.
/// </summary>
public interface IUdaGetters
{
    bool TryGetString(out string value);
    bool TryGetInteger(out int value);
    bool TryGetDouble(out double value);

    /// <summary>
    /// The type Tekla stored for this object's value (live: one <c>GetAllUserProperties</c> call,
    /// so it is asked only when nothing cheaper can tell). Unknown when not set or unreadable.
    /// </summary>
    UdaValueKind ReadStoredKind();
}

/// <summary>
/// Reads and writes UDA values by their declared type instead of by trial (issue #21). Shared
/// by every UDA read of the live backend — named reads, export, grouping, <c>udaEquals</c> /
/// <c>udaIsEmpty</c>, the import's current-value check — and by its writes.
///
/// States stay distinct: not set (no value), a blank string (""), integer 0, other numbers.
/// </summary>
public static class UdaValues
{
    /// <summary>Tekla's <c>PropertyTypeEnum</c> name (TYPE_INT, TYPE_DOUBLE, TYPE_STRING).</summary>
    public static UdaValueKind FromCatalogType(string? typeName)
    {
        switch ((typeName ?? "").Trim().ToUpperInvariant())
        {
            case "TYPE_INT": return UdaValueKind.Integer;
            case "TYPE_DOUBLE": return UdaValueKind.Double;
            case "TYPE_STRING": return UdaValueKind.String;
            default: return UdaValueKind.Unknown;
        }
    }

    /// <summary>The kind of a value from <c>GetAllUserProperties</c> (boxed int, double or string).</summary>
    public static UdaValueKind FromStoredValue(object? value)
    {
        switch (value)
        {
            case int _: return UdaValueKind.Integer;
            case double _: return UdaValueKind.Double;
            case string _: return UdaValueKind.String;
            default: return UdaValueKind.Unknown;
        }
    }

    public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads one attribute. Returns false when it is not set. <paramref name="learned"/> is the
    /// type an <see cref="UdaValueKind.Unknown"/> read found out, for the caller to cache.
    /// </summary>
    public static bool TryRead(UdaValueKind kind, IUdaGetters getters, out string value, out UdaValueKind learned)
    {
        learned = UdaValueKind.Unknown;
        value = "";
        switch (kind)
        {
            case UdaValueKind.Integer:
                if (!getters.TryGetInteger(out var integer)) return false;
                value = Format(integer);
                return true;

            case UdaValueKind.Double:
                if (!getters.TryGetDouble(out var number)) return false;
                value = Format(number);
                return true;

            case UdaValueKind.String:
                if (!getters.TryGetString(out var text)) return false;
                value = text ?? "";
                // Blank — or a number in a string attribute (servers up to v0.8.1 wrote "123" as an
                // integer): the string getter answers "" for that, the double getter has it.
                if (value.Length == 0 && getters.TryGetDouble(out var stray) && stray != 0)
                    value = Format(stray);
                return true;

            default:
                if (getters.TryGetString(out var unknownText))
                {
                    value = unknownText ?? "";
                    if (value.Length > 0)
                    {
                        learned = UdaValueKind.String;
                        return true;
                    }

                    // "" is a blank string or any number: ask what Tekla stored.
                    var stored = getters.ReadStoredKind();
                    if (stored != UdaValueKind.Unknown)
                    {
                        learned = stored;
                        if (TryRead(stored, getters, out value, out _)) return true;
                        value = "";
                        return true;
                    }

                    if (getters.TryGetDouble(out var guess) && guess != 0) value = Format(guess);
                    return true;
                }

                // The string getter refused: not set (Tekla 2021), or a stricter getter on a number.
                if (getters.TryGetInteger(out var unknownInteger))
                {
                    value = Format(unknownInteger);
                    return true;
                }
                if (getters.TryGetDouble(out var unknownNumber))
                {
                    value = Format(unknownNumber);
                    return true;
                }
                return false;
        }
    }

    /// <summary>
    /// Converts a requested value to the attribute's type before it is written. A declared
    /// string stays a string ("123" too); a declared number must parse, a blank included —
    /// the Open API cannot unset an attribute. Unknown attributes keep the old guess: integer,
    /// then double, then string. Returns false with <paramref name="error"/> for a mismatch.
    /// </summary>
    public static bool TryConvertForWrite(
        UdaValueKind kind, string name, string? raw, out object value, out string? error)
    {
        var text = raw ?? "";
        var trimmed = text.Trim();
        error = null;
        value = text;
        switch (kind)
        {
            case UdaValueKind.String:
                return true;

            case UdaValueKind.Integer:
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    value = integer;
                    return true;
                }
                error = $"UDA '{name}' is an integer attribute; '{text}' is not an integer" +
                        (trimmed.Length == 0 ? " (the Open API cannot unset an attribute — write 0 or leave it out)." : ".");
                return false;

            case UdaValueKind.Double:
                if (double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out var number))
                {
                    value = number;
                    return true;
                }
                error = $"UDA '{name}' is a decimal attribute; '{text}' is not a number" +
                        (trimmed.Length == 0 ? " (the Open API cannot unset an attribute — write 0 or leave it out)." : ".");
                return false;

            default:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var guessInteger))
                    value = guessInteger;
                else if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                             CultureInfo.InvariantCulture, out var guessNumber))
                    value = guessNumber;
                return true;
        }
    }
}

/// <summary>
/// Per-model cache of UDA types: what the catalog declares (looked up once per name) and what
/// reads of undeclared names found out. Names are case-sensitive, as in Tekla (2021 defines both
/// <c>comment</c> and <c>COMMENT</c>). Thread-safe.
/// </summary>
public sealed class UdaKindCache
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, UdaValueKind> _kinds = new Dictionary<string, UdaValueKind>(StringComparer.Ordinal);
    private string? _scope;

    /// <summary>Forgets everything when the model (scope) changed; another model has another catalog.</summary>
    public void ResetIfScopeChanged(string? scope)
    {
        lock (_gate)
        {
            if (string.Equals(_scope, scope, StringComparison.OrdinalIgnoreCase)) return;
            _scope = scope;
            _kinds.Clear();
        }
    }

    /// <summary>The cached kind; false when the name was never looked up.</summary>
    public bool TryGet(string name, out UdaValueKind kind)
    {
        lock (_gate) return _kinds.TryGetValue(name, out kind);
    }

    /// <summary>Records a catalog lookup (Unknown = not declared).</summary>
    public void SetDeclared(string name, UdaValueKind kind)
    {
        lock (_gate) _kinds[name] = kind;
    }

    /// <summary>Records what a read found out — only for names the catalog did not declare.</summary>
    public void Learn(string name, UdaValueKind kind)
    {
        if (kind == UdaValueKind.Unknown) return;
        lock (_gate)
        {
            if (!_kinds.TryGetValue(name, out var current) || current == UdaValueKind.Unknown)
                _kinds[name] = kind;
        }
    }
}
