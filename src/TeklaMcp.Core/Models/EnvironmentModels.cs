using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>One advanced option (XS_* variable) as the running Tekla resolves it.</summary>
public sealed class AdvancedOptionValue
{
    public string Name { get; set; } = "";

    /// <summary>False when Tekla does not know the option (or reported no value for it).</summary>
    public bool Found { get; set; }

    /// <summary>The value as text; booleans/numbers are formatted invariantly.</summary>
    public string? Value { get; set; }

    /// <summary>"string", "bool", "int" or "double" — the overload that answered.</summary>
    public string? ValueType { get; set; }

    /// <summary>
    /// With asPaths: the value split on ';' into valid paths (Tekla's own parsing — paths need not
    /// exist). Null when not requested.
    /// </summary>
    public List<string>? Paths { get; set; }

    /// <summary>Entries Tekla rejected as paths, with its reason.</summary>
    public List<string> InvalidPaths { get; set; } = new List<string>();

    public string? Message { get; set; }
}

/// <summary>What to list from the environment catalogs.</summary>
public sealed class CatalogQuery
{
    /// <summary>
    /// profiles (library profiles), parametric_profiles, materials, components or
    /// uda_definitions (user-defined attribute definitions).
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>Case-insensitive substring of the item name (component: name or UI name).</summary>
    public string? NameContains { get; set; }

    /// <summary>uda_definitions only: a CatalogObjectTypeEnum name such as PART, STEEL_BEAM, BOLT.</summary>
    public string? ObjectType { get; set; }

    /// <summary>Read per-item extras (profile parameters, UDA label/options/object types). Slower.</summary>
    public bool Details { get; set; }

    /// <summary>Maximum items returned in this page.</summary>
    public int Limit { get; set; } = 200;

    /// <summary>Continuation cursor from the previous page (a catalog offset).</summary>
    public string? Cursor { get; set; }
}

/// <summary>One catalog entry. Kind-specific extras go into <see cref="Properties"/>.</summary>
public sealed class CatalogItemInfo
{
    public string Name { get; set; } = "";

    /// <summary>Profile type, material type, component type or UDA value type.</summary>
    public string? Type { get; set; }

    public string? SubType { get; set; }

    /// <summary>Component number (negative numbers are custom components).</summary>
    public int? Number { get; set; }

    /// <summary>Kind-specific values (details=true), e.g. "h", "density", "label", "objectTypes".</summary>
    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
}

/// <summary>A page of catalog entries with its coverage, like every other scan in this server.</summary>
public sealed class CatalogListResult
{
    public string Kind { get; set; } = "";
    public List<CatalogItemInfo> Items { get; set; } = new List<CatalogItemInfo>();

    /// <summary>Catalog entries looked at in this page (including ones the name filter dropped).</summary>
    public long Scanned { get; set; }

    /// <summary>True when the page stopped at the limit and more entries follow.</summary>
    public bool Truncated { get; set; }

    /// <summary>Pass back as cursor to continue; null when the catalog is exhausted.</summary>
    public string? NextCursor { get; set; }

    public string Backend { get; set; } = "";
    public string? Message { get; set; }
}
