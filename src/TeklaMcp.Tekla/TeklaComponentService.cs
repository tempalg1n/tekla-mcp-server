using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla;

/// <summary>
/// Live half of <c>tekla_create_component</c> (backlog §5): a plugin / custom / system component
/// with an ordered <c>ComponentInput</c> — the primitive component development was missing next to
/// <c>CreateConnections</c>. A plugin's Run is not observable through the Open API, so every
/// created component is re-selected after the commit and its children are counted: zero children
/// is reported as a warning, never as success. Inputs are GLOBAL mm (global work plane).
/// Compiles 2021–2026. TODO(windows): run live with a real plugin (insert → children, attribute
/// types, whether Run happens inside Insert() or at CommitChanges()).
/// </summary>
public sealed partial class TeklaModelService
{
    private const int MaxChildTypes = 20;

    public WriteResult CreateComponents(IReadOnlyList<ComponentSpec> specs, bool apply)
    {
        var result = new WriteResult { Operation = "create_components", Applied = apply, Backend = BackendName };
        var progress = new WriteProgress();
        if (specs == null || specs.Count == 0)
        {
            result.Message = "No component specs provided.";
            return Stamp(result, progress);
        }

        try
        {
            var model = GetConnectedModel();
            // Freshly created input parts must be selectable (same race as CreateConnections).
            if (apply) model.CommitChanges();

            var created = new List<TSM.Component>();
            InGlobalWorkPlane(model, () =>
            {
                foreach (var spec in specs)
                {
                    result.PlannedCount++;
                    try
                    {
                        var errors = ComponentSpecs.Validate(spec);
                        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));

                        var input = BuildComponentInput(model, spec, out var objectGuids);
                        var preview = new ComponentInfo
                        {
                            Guid = "(preview)",
                            Type = "Component",
                            Name = spec.Name ?? "",
                            Number = ComponentNumber(spec),
                            PrimaryGuid = objectGuids.FirstOrDefault() ?? "",
                            SecondaryGuids = objectGuids.Skip(1).ToList(),
                        };
                        if (result.ComponentPreview.Count < 20) result.ComponentPreview.Add(preview);
                        if (!apply) continue;

                        var component = new TSM.Component(input) { Name = spec.Name ?? "", Number = ComponentNumber(spec) };
                        if (!string.IsNullOrWhiteSpace(spec.AttributesFile) &&
                            !component.LoadAttributesFromFile(spec.AttributesFile!.Trim()))
                            throw new InvalidOperationException("Attributes file could not be loaded: " + spec.AttributesFile);
                        foreach (var attribute in spec.Attributes ?? new List<ComponentAttributeSpec>())
                            SetComponentAttribute(component, attribute);

                        progress.BeginWrite();
                        if (!component.Insert())
                            throw new InvalidOperationException(
                                "Tekla rejected the component insert (" + (spec.Name ?? "") + "). Check the name, the " +
                                "input order the component expects and the attribute types.");
                        result.CreatedCount++;
                        result.CreatedIds.Add(component.Identifier.ID);
                        component.SetUserProperty("MCP_ORIGIN", "mcp:create_component");
                        created.Add(component);
                    }
                    catch (Exception exItem)
                    {
                        progress.ItemFailed(exItem);
                        result.Errors.Add((spec?.Name ?? "component") + ": " + ErrorText.Flatten(exItem));
                    }
                }
                return true;
            });

            if (apply)
            {
                model.CommitChanges();
                progress.Complete();
                ReadBackComponents(created, result);
            }
        }
        catch (Exception ex)
        {
            progress.Fail(ex);
            result.Message = ErrorText.Flatten(ex);
        }
        return Stamp(result, progress);
    }

    private static int ComponentNumber(ComponentSpec spec)
    {
        switch (ComponentSpecs.NormalizeKind(spec.Kind))
        {
            case "system": return spec.Number ?? 0;
            case "custom": return TSM.BaseComponent.CUSTOM_OBJECT_NUMBER;
            default: return TSM.BaseComponent.PLUGIN_OBJECT_NUMBER;
        }
    }

    private static TSM.ComponentInput BuildComponentInput(TSM.Model model, ComponentSpec spec, out List<string> objectGuids)
    {
        objectGuids = new List<string>();
        var input = new TSM.ComponentInput();
        for (var i = 0; i < spec.Inputs.Count; i++)
        {
            var item = spec.Inputs[i];
            bool added;
            switch (ComponentSpecs.NormalizeInputType(item.Type))
            {
                case "object":
                    var mo = TrySelectObjectByGuid(model, item.Guid ?? "");
                    if (mo is null) throw new InvalidOperationException($"inputs[{i}]: object not found: {item.Guid}");
                    added = input.AddInputObject(mo);
                    objectGuids.Add(ModelGuid(mo));
                    break;
                case "point":
                    added = input.AddOneInputPosition(ToPoint(item.Points[0]));
                    break;
                case "two_points":
                    added = input.AddTwoInputPositions(ToPoint(item.Points[0]), ToPoint(item.Points[1]));
                    break;
                default: // polygon
                    var polygon = new TSM.Polygon();
                    foreach (var point in item.Points) polygon.Points.Add(ToPoint(point));
                    added = input.AddInputPolygon(polygon);
                    break;
            }
            if (!added) throw new InvalidOperationException($"inputs[{i}]: Tekla refused the {item.Type} input.");
        }
        return input;
    }

    private static void SetComponentAttribute(TSM.Component component, ComponentAttributeSpec attribute)
    {
        var name = attribute.Name.Trim();
        switch ((attribute.Type ?? "string").Trim().ToLowerInvariant())
        {
            case "int":
                component.SetAttribute(name, int.Parse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture));
                break;
            case "double":
                component.SetAttribute(name, double.Parse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture));
                break;
            default:
                component.SetAttribute(name, attribute.Value ?? "");
                break;
        }
    }

    /// <summary>Replaces the previews with what the database holds, children included.</summary>
    private static void ReadBackComponents(List<TSM.Component> created, WriteResult result)
    {
        result.ComponentPreview.Clear();
        foreach (var component in created)
        {
            ComponentInfo info;
            try
            {
                var fresh = new TSM.Component { Identifier = component.Identifier };
                var source = fresh.Select() ? fresh : component;
                info = MapComponent(source);
                var guid = info.Guid;
                if (!string.IsNullOrWhiteSpace(guid)) result.CreatedGuids.Add(guid);

                var count = 0;
                var types = new SortedSet<string>(StringComparer.Ordinal);
                var children = source.GetChildren();
                while (children.MoveNext())
                {
                    if (children.Current is null) continue;
                    count++;
                    if (types.Count < MaxChildTypes) types.Add(children.Current.GetType().Name);
                }
                info.ChildCount = count;
                info.ChildTypes = types.ToList();
                if (count == 0)
                    result.Errors.Add(
                        $"Component {info.Name} (id {info.Id}) was inserted but created no objects — its Run " +
                        "probably failed (wrong input order/types, missing attribute). Check Tekla's session log, " +
                        "then fix or delete the component.");
            }
            catch (Exception ex)
            {
                info = new ComponentInfo { Id = component.Identifier.ID, Type = "Component", Name = component.Name ?? "" };
                result.Errors.Add($"Component id {info.Id}: read-back failed: {ErrorText.Flatten(ex)}");
            }
            if (result.ComponentPreview.Count < 20) result.ComponentPreview.Add(info);
        }
    }

}
