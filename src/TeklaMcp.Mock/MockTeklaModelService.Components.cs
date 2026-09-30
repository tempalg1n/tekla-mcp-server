using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Mock;

/// <summary>
/// Mock half of <c>tekla_create_component</c>: validates the spec and the input objects like the
/// live backend, records the component — and says plainly that it did not run a plugin, so
/// <see cref="ComponentInfo.ChildCount"/> is 0 rather than an invented number.
/// </summary>
public sealed partial class MockTeklaModelService
{
    // Tekla's BaseComponent.PLUGIN_OBJECT_NUMBER / CUSTOM_OBJECT_NUMBER.
    private const int MockPluginNumber = -100000;
    private const int MockCustomNumber = -1;

    public WriteResult CreateComponents(IReadOnlyList<ComponentSpec> specs, bool apply)
    {
        var result = new WriteResult { Operation = "create_components", Applied = apply, Backend = BackendName };
        if (specs == null || specs.Count == 0)
        {
            result.Message = "No component specs provided.";
            return result;
        }

        foreach (var spec in specs)
        {
            result.PlannedCount++;
            var errors = ComponentSpecs.Validate(spec);
            var missing = spec.Inputs
                .Where(i => ComponentSpecs.NormalizeInputType(i.Type) == "object" && GetObjectByGuid(i.Guid ?? "") is null)
                .Select(i => "Input object not found: " + i.Guid);
            errors.AddRange(missing);
            if (errors.Count > 0)
            {
                result.Errors.Add((spec.Name ?? "component") + ": " + string.Join(" ", errors));
                continue;
            }

            var kind = ComponentSpecs.NormalizeKind(spec.Kind);
            var objects = spec.Inputs
                .Where(i => ComponentSpecs.NormalizeInputType(i.Type) == "object")
                .Select(i => i.Guid!)
                .ToList();
            var component = new ComponentInfo
            {
                Guid = apply ? Guid.NewGuid().ToString() : "(preview)",
                Id = apply ? _nextId++ : 0,
                Type = "Component",
                Name = spec.Name ?? "",
                Number = kind == "system" ? spec.Number ?? 0 : kind == "custom" ? MockCustomNumber : MockPluginNumber,
                PrimaryGuid = objects.FirstOrDefault() ?? "",
                SecondaryGuids = objects.Skip(1).ToList(),
                Status = apply ? "OK" : "",
                ChildCount = apply ? 0 : (int?)null,
            };
            if (result.ComponentPreview.Count < 20) result.ComponentPreview.Add(component);
            if (!apply) continue;

            _components.Add(component);
            result.CreatedCount++;
            result.CreatedGuids.Add(component.Guid);
            result.CreatedIds.Add(component.Id);
        }

        if (apply && result.CreatedCount > 0)
            result.Message = "Mock: the component is recorded, but no plugin code runs here — childCount stays 0.";
        return result;
    }
}
