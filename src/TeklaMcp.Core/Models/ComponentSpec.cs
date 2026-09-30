using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// A plugin, custom component or system component to insert with explicit input (backlog §5).
/// Unlike <see cref="ConnectionSpec"/> (primary + secondaries), components take an ORDERED list of
/// objects, points and polygons — the order the component's input definition expects.
/// </summary>
public sealed class ComponentSpec
{
    /// <summary>Plugin / custom component name as in the catalog (tekla_list_catalog kind=components).</summary>
    public string Name { get; set; } = "";

    /// <summary>"plugin" (default), "custom" or "system" (then <see cref="Number"/> is required).</summary>
    public string? Kind { get; set; }

    /// <summary>System components only: the component number (&gt; 0).</summary>
    public int? Number { get; set; }

    /// <summary>The input, in the order the component expects it.</summary>
    public List<ComponentInputSpec> Inputs { get; set; } = new List<ComponentInputSpec>();

    /// <summary>Saved attributes file to load first (name without extension, as in the dialog).</summary>
    public string? AttributesFile { get; set; }

    /// <summary>Attribute values set after the file, so they override it.</summary>
    public List<ComponentAttributeSpec> Attributes { get; set; } = new List<ComponentAttributeSpec>();
}

/// <summary>One input item: an object, one point, a point pair, or a polygon (GLOBAL mm).</summary>
public sealed class ComponentInputSpec
{
    /// <summary>"object", "point", "two_points" or "polygon".</summary>
    public string Type { get; set; } = "";

    /// <summary>type=object: the model object's GUID.</summary>
    public string? Guid { get; set; }

    /// <summary>type=point: 1 point; two_points: 2; polygon: 3 or more.</summary>
    public List<Point3D> Points { get; set; } = new List<Point3D>();
}

public sealed class ComponentAttributeSpec
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>"string" (default), "int" or "double" — must match the attribute's type in the component.</summary>
    public string? Type { get; set; }
}
