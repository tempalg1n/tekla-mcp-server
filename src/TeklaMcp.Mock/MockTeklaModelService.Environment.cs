using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Mock;

/// <summary>
/// Mock half of the environment read tools (<c>tekla_get_advanced_options</c>,
/// <c>tekla_list_catalog</c>). A small, believable environment — the profiles and materials the
/// sample model uses, a few components, the UDAs it carries — served through the same
/// <see cref="CatalogListing"/> paging as the live catalogs.
/// </summary>
public sealed partial class MockTeklaModelService
{
    private static readonly Dictionary<string, string> MockAdvancedOptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["XS_MACRO_DIRECTORY"] = @"C:\ProgramData\Trimble\Tekla Structures\2026.0\Environments\common\macros;C:\TeklaStructuresModels\MockModel\macros",
            ["XS_FIRM"] = @"C:\TeklaFirm",
            ["XS_PROJECT"] = "",
            ["XS_SYSTEM"] = @"C:\ProgramData\Trimble\Tekla Structures\2026.0\Environments\default\system",
            ["XS_IMPERIAL"] = "FALSE",
            ["XS_DEFAULT_LENGTH_UNIT"] = "mm",
        };

    public IReadOnlyList<AdvancedOptionValue> GetAdvancedOptions(IReadOnlyList<string> names, bool asPaths)
    {
        var result = new List<AdvancedOptionValue>();
        foreach (var raw in names ?? Array.Empty<string>())
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            var option = new AdvancedOptionValue { Name = name };
            if (MockAdvancedOptions.TryGetValue(name, out var value))
            {
                option.Found = true;
                option.Value = value;
                option.ValueType = "string";
                if (asPaths)
                    option.Paths = value.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            }
            else
            {
                option.Message = "Not defined in this environment (mock).";
            }
            result.Add(option);
        }
        return result;
    }

    private sealed class MockCatalogItem
    {
        public string Name = "";
        public string? AltName;
        public string? Type;
        public string? SubType;
        public int? Number;
        public string? ObjectTypes;
        public Dictionary<string, string> Details = new Dictionary<string, string>();
    }

    public CatalogListResult ListCatalog(CatalogQuery query)
    {
        query ??= new CatalogQuery();
        var kind = CatalogListing.NormalizeKind(query.Kind, out var error);
        if (kind is null) return new CatalogListResult { Kind = query.Kind ?? "", Backend = BackendName, Message = error };

        IEnumerable<MockCatalogItem> source = MockCatalog(kind);
        if (kind == "uda_definitions" && !string.IsNullOrWhiteSpace(query.ObjectType))
        {
            var wanted = query.ObjectType!.Trim().ToUpperInvariant();
            source = source.Where(i => (i.ObjectTypes ?? "").Split(',').Contains(wanted));
        }

        return CatalogListing.Page(
            kind,
            source,
            i => i.AltName is null ? new[] { i.Name } : new[] { i.Name, i.AltName },
            i => new CatalogItemInfo
            {
                Name = i.Name,
                Type = i.Type,
                SubType = i.SubType,
                Number = i.Number,
                Properties = query.Details
                    ? new Dictionary<string, string>(i.Details)
                    : new Dictionary<string, string>(),
            },
            query,
            BackendName);
    }

    private static IEnumerable<MockCatalogItem> MockCatalog(string kind)
    {
        switch (kind)
        {
            case "profiles":
                foreach (var (name, h, b) in new[] { ("IPE300", 300, 150), ("IPE400", 400, 180), ("HEA300", 290, 300), ("HEB300", 300, 300) })
                    yield return new MockCatalogItem
                    {
                        Name = name, Type = "I", SubType = name.Substring(0, 3),
                        Details = { ["h"] = h.ToString(CultureInfo.InvariantCulture), ["b"] = b.ToString(CultureInfo.InvariantCulture) },
                    };
                yield return new MockCatalogItem { Name = "PD168.3*6", Type = "RO", Details = { ["D"] = "168.3", ["t"] = "6" } };
                break;
            case "parametric_profiles":
                yield return new MockCatalogItem { Name = "PL", Type = "B", Details = { ["parameters"] = "t*b" } };
                yield return new MockCatalogItem { Name = "PD", Type = "RO", Details = { ["parameters"] = "D*t" } };
                break;
            case "materials":
                yield return new MockCatalogItem { Name = "S355J2", Type = "MATERIAL_STEEL", Details = { ["profileDensity"] = "7850" } };
                yield return new MockCatalogItem { Name = "S235JR", Type = "MATERIAL_STEEL", Details = { ["profileDensity"] = "7850" } };
                yield return new MockCatalogItem { Name = "C30/37", Type = "MATERIAL_CONCRETE", Details = { ["profileDensity"] = "2500" } };
                break;
            case "components":
                yield return new MockCatalogItem { Name = "Clip angle", AltName = "Clip angle (141)", Type = "CONNECTION", Number = 141 };
                yield return new MockCatalogItem { Name = "End plate", AltName = "End plate (144)", Type = "CONNECTION", Number = 144 };
                yield return new MockCatalogItem { Name = "KXMp_Handrail", AltName = "KXMp Handrail", Type = "COMPONENT", Number = -100000 };
                break;
            case "uda_definitions":
                foreach (var field in new[] { "USER_FIELD_1", "USER_FIELD_2", "USER_FIELD_3", "USER_FIELD_4" })
                    yield return new MockCatalogItem
                    {
                        Name = field, Type = "STRING", ObjectTypes = "PART,STEEL_BEAM,STEEL_COLUMN",
                        Details = { ["label"] = "User field " + field.Substring(field.Length - 1), ["objectTypes"] = "PART,STEEL_BEAM,STEEL_COLUMN" },
                    };
                yield return new MockCatalogItem
                {
                    Name = "MCP_ORIGIN", Type = "STRING", ObjectTypes = "PART,STEEL_BEAM,STEEL_COLUMN",
                    Details = { ["label"] = "MCP origin", ["objectTypes"] = "PART,STEEL_BEAM,STEEL_COLUMN" },
                };
                yield return new MockCatalogItem
                {
                    Name = "BOLT_COMMENT", Type = "STRING", ObjectTypes = "BOLT",
                    Details = { ["label"] = "Comment", ["objectTypes"] = "BOLT" },
                };
                break;
        }
    }
}
