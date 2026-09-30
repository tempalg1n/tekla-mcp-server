using System;
using System.Collections.Generic;
using System.Linq;

namespace TeklaMcp.Core.FileExchange;

/// <summary>One exportable column. <see cref="ExportFieldKind.Uda"/> carries the UDA name.</summary>
public enum ExportFieldKind
{
    Guid,
    Id,
    Type,
    Name,
    Class,
    Profile,
    Material,
    AssemblyPos,
    LengthMm,
    WeightKg,
    Finish,
    /// <summary>World AABB of <c>Part.GetSolid()</c> — the expensive one (~1.5 ms per part live).</summary>
    SolidAabb,
    StartPoint,
    EndPoint,
    /// <summary>Contour polygon of a ContourPlate / PolyBeam, global mm.</summary>
    ContourPoints,
    /// <summary>Part coordinate system: origin + X/Y/Z unit axes, global mm.</summary>
    CoordSystem,
    /// <summary>Centre of gravity (COG_X/COG_Y/COG_Z report properties), global mm.</summary>
    Cog,
    Uda,
}

/// <summary>A single requested column.</summary>
public sealed class ExportField
{
    public ExportField(ExportFieldKind kind, string name, string udaName = "")
    {
        Kind = kind;
        Name = name;
        UdaName = udaName;
    }

    public ExportFieldKind Kind { get; }

    /// <summary>Canonical field name as written to the file / echoed in the result.</summary>
    public string Name { get; }

    /// <summary>UDA name for <see cref="ExportFieldKind.Uda"/>, otherwise empty.</summary>
    public string UdaName { get; }
}

/// <summary>
/// The parsed <c>fields</c> argument plus the cost flags a backend needs in order to pay for
/// nothing it was not asked for. This matters: a whole-model export reads 65k+ parts, and
/// <c>GetSolid()</c> / <c>GetCoordinateSystem()</c> / per-UDA reads are each a remoting call.
/// Filter cheap, enrich late — see the performance rules in AGENTS.md.
/// </summary>
public sealed class ExportFieldSet
{
    internal ExportFieldSet(IReadOnlyList<ExportField> fields)
    {
        Fields = fields;
        UdaNames = fields.Where(f => f.Kind == ExportFieldKind.Uda)
                         .Select(f => f.UdaName)
                         .ToList();
        NeedsReportProperties = fields.Any(f =>
            f.Kind == ExportFieldKind.AssemblyPos ||
            f.Kind == ExportFieldKind.LengthMm ||
            f.Kind == ExportFieldKind.WeightKg);
        NeedsSolid = fields.Any(f => f.Kind == ExportFieldKind.SolidAabb);
        NeedsCog = fields.Any(f => f.Kind == ExportFieldKind.Cog);
        NeedsCoordSystem = fields.Any(f => f.Kind == ExportFieldKind.CoordSystem);
        NeedsContour = fields.Any(f => f.Kind == ExportFieldKind.ContourPoints);
        NeedsEndPoints = fields.Any(f =>
            f.Kind == ExportFieldKind.StartPoint || f.Kind == ExportFieldKind.EndPoint);
    }

    public IReadOnlyList<ExportField> Fields { get; }
    public IReadOnlyList<string> UdaNames { get; }
    public bool NeedsReportProperties { get; }
    public bool NeedsSolid { get; }
    public bool NeedsCog { get; }
    public bool NeedsCoordSystem { get; }
    public bool NeedsContour { get; }
    public bool NeedsEndPoints { get; }

    /// <summary>Canonical field names, in file order.</summary>
    public IReadOnlyList<string> Names => Fields.Select(f => f.Name).ToList();
}

/// <summary>Parses and validates the <c>fields</c> argument of the export tools.</summary>
public static class ExportFields
{
    /// <summary>The columns used when the caller names none — identity + bill-of-materials.</summary>
    public static readonly IReadOnlyList<string> DefaultFieldNames = new[]
    {
        "guid", "id", "type", "name", "class", "profile", "material",
        "assemblyPos", "lengthMm", "weightKg",
    };

    private static readonly Dictionary<string, ExportFieldKind> KnownFields =
        new Dictionary<string, ExportFieldKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["guid"] = ExportFieldKind.Guid,
            ["id"] = ExportFieldKind.Id,
            ["type"] = ExportFieldKind.Type,
            ["name"] = ExportFieldKind.Name,
            ["class"] = ExportFieldKind.Class,
            ["profile"] = ExportFieldKind.Profile,
            ["material"] = ExportFieldKind.Material,
            ["assemblyPos"] = ExportFieldKind.AssemblyPos,
            ["lengthMm"] = ExportFieldKind.LengthMm,
            ["weightKg"] = ExportFieldKind.WeightKg,
            ["finish"] = ExportFieldKind.Finish,
            ["solidAabb"] = ExportFieldKind.SolidAabb,
            ["startPoint"] = ExportFieldKind.StartPoint,
            ["endPoint"] = ExportFieldKind.EndPoint,
            ["contourPoints"] = ExportFieldKind.ContourPoints,
            ["coordSystem"] = ExportFieldKind.CoordSystem,
            ["cog"] = ExportFieldKind.Cog,
        };

    /// <summary>Canonical spelling of every non-UDA field, for error messages and docs.</summary>
    public static IReadOnlyList<string> AllFieldNames { get; } = new[]
    {
        "guid", "id", "type", "name", "class", "profile", "material", "assemblyPos",
        "lengthMm", "weightKg", "finish", "solidAabb", "startPoint", "endPoint",
        "contourPoints", "coordSystem", "cog",
    };

    /// <summary>
    /// Parse requested field names. Null/empty yields <see cref="DefaultFieldNames"/>.
    /// Duplicates collapse to their first occurrence. Returns false with a caller-facing
    /// <paramref name="error"/> for an unknown name — the tools report it instead of throwing.
    /// </summary>
    public static bool TryParse(IEnumerable<string>? requested, out ExportFieldSet fieldSet, out string error)
    {
        error = "";
        var names = (requested ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToList();
        if (names.Count == 0) names = DefaultFieldNames.ToList();

        var fields = new List<ExportField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (name.StartsWith("uda:", StringComparison.OrdinalIgnoreCase))
            {
                var udaName = name.Substring(4).Trim();
                if (udaName.Length == 0)
                {
                    error = $"'{name}' names no UDA. Use e.g. 'uda:USER_FIELD_1'.";
                    fieldSet = Empty();
                    return false;
                }

                // A whole-UDA dump means GetAllUserProperties per object — ~100 ms each on live
                // models (measured), i.e. hours over a full model. Named fields only.
                if (udaName == "*")
                {
                    error = "'uda:*' is not supported: reading every UDA costs a separate " +
                            "GetAllUserProperties call per object (~100 ms each live, hours over a " +
                            "full model). Discover the field layout with tekla_discover_udas, then " +
                            "name the UDAs you need, e.g. 'uda:USER_FIELD_1'.";
                    fieldSet = Empty();
                    return false;
                }

                if (seen.Add("uda:" + udaName))
                    fields.Add(new ExportField(ExportFieldKind.Uda, udaName, udaName));
                continue;
            }

            if (!KnownFields.TryGetValue(name, out var kind))
            {
                error = $"Unknown field '{name}'. Available: {string.Join(", ", AllFieldNames)}, " +
                        "or 'uda:NAME' for a user-defined attribute.";
                fieldSet = Empty();
                return false;
            }

            // Canonical spelling wins, so the file header does not echo the caller's casing.
            var canonical = AllFieldNames.First(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (seen.Add(canonical)) fields.Add(new ExportField(kind, canonical));
        }

        fieldSet = new ExportFieldSet(fields);
        return true;
    }

    private static ExportFieldSet Empty() => new ExportFieldSet(new List<ExportField>());
}
