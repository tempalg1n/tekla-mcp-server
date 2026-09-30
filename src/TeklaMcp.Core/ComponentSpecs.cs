using System;
using System.Collections.Generic;
using System.Globalization;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core;

/// <summary>
/// Validation and normalization of <see cref="ComponentSpec"/> shared by both backends and the
/// tool layer, so a spec the mock accepts is one the live backend would try — and a malformed one
/// is refused before anything touches Tekla.
/// </summary>
public static class ComponentSpecs
{
    public static string NormalizeKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? "plugin" : kind!.Trim().ToLowerInvariant();

    public static string NormalizeInputType(string? type) =>
        (type ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
        {
            "object" or "model_object" or "part" => "object",
            "point" or "position" => "point",
            "two_points" or "twopoints" or "points" or "line" => "two_points",
            "polygon" => "polygon",
            var other => other,
        };

    /// <summary>Every problem with the spec, empty when it can be tried.</summary>
    public static List<string> Validate(ComponentSpec? spec)
    {
        var errors = new List<string>();
        if (spec is null) { errors.Add("Missing component spec."); return errors; }

        var kind = NormalizeKind(spec.Kind);
        if (kind != "plugin" && kind != "custom" && kind != "system")
            errors.Add($"kind '{spec.Kind}' is not plugin, custom or system.");
        if (string.IsNullOrWhiteSpace(spec.Name) && kind != "system")
            errors.Add("name is required (the plugin/custom component name from tekla_list_catalog kind=components).");
        if (kind == "system" && !(spec.Number is int n && n > 0))
            errors.Add("kind=system needs number > 0 (the system component number).");

        if (spec.Inputs is null || spec.Inputs.Count == 0)
            errors.Add("inputs is empty — a component needs its input objects/points in the expected order.");
        else
            for (var i = 0; i < spec.Inputs.Count; i++)
            {
                var input = spec.Inputs[i];
                var type = NormalizeInputType(input?.Type);
                var points = input?.Points?.Count ?? 0;
                switch (type)
                {
                    case "object":
                        if (string.IsNullOrWhiteSpace(input!.Guid)) errors.Add($"inputs[{i}]: type=object needs a guid.");
                        break;
                    case "point":
                        if (points != 1) errors.Add($"inputs[{i}]: type=point needs exactly 1 point (got {points}).");
                        break;
                    case "two_points":
                        if (points != 2) errors.Add($"inputs[{i}]: type=two_points needs exactly 2 points (got {points}).");
                        break;
                    case "polygon":
                        if (points < 3) errors.Add($"inputs[{i}]: type=polygon needs at least 3 points (got {points}).");
                        break;
                    default:
                        errors.Add($"inputs[{i}]: type '{input?.Type}' is not object, point, two_points or polygon.");
                        break;
                }
            }

        foreach (var attribute in spec.Attributes ?? new List<ComponentAttributeSpec>())
        {
            if (string.IsNullOrWhiteSpace(attribute?.Name)) { errors.Add("An attribute has no name."); continue; }
            var type = (attribute!.Type ?? "string").Trim().ToLowerInvariant();
            if (type == "int" && !int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                errors.Add($"attribute {attribute.Name}: '{attribute.Value}' is not an int.");
            else if (type == "double" && !double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                errors.Add($"attribute {attribute.Name}: '{attribute.Value}' is not a double (use '.' as decimal separator).");
            else if (type != "string" && type != "int" && type != "double")
                errors.Add($"attribute {attribute.Name}: type '{attribute.Type}' is not string, int or double.");
        }
        return errors;
    }
}
