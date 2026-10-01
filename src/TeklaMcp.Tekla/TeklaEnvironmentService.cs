using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TS = Tekla.Structures;
using TSC = Tekla.Structures.Catalogs;

namespace TeklaMcp.Tekla;

/// <summary>
/// Live half of the environment read tools (backlog §5, DEV-005: component development needed
/// XS_MACRO_DIRECTORY and the catalogs, and had to script both).
///
/// Both APIs ride on their own remoting clients: <c>TeklaStructuresSettings</c> on the base
/// channel, <c>CatalogHandler</c> on the Catalogs channel. Neither is covered by the connection
/// guard directly, so every call first goes through <c>GetConnectedModel()</c> — the same Tekla
/// publishes all of its channels, and that check refuses before anything dials a missing one.
/// Compiled against 2021–2026; advanced options ran live on Tekla 2023 (see the unknown-option
/// note below). TODO(windows): run the rest of the catalog surface live — UDA definitions with
/// details=true in particular (docs/tekla-api-notes.md).
/// </summary>
public sealed partial class TeklaModelService
{
    public IReadOnlyList<AdvancedOptionValue> GetAdvancedOptions(IReadOnlyList<string> names, bool asPaths)
    {
        GetConnectedModel();
        var result = new List<AdvancedOptionValue>();
        foreach (var raw in names ?? Array.Empty<string>())
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            result.Add(ReadAdvancedOption(name, asPaths));
        }
        return result;
    }

    private static AdvancedOptionValue ReadAdvancedOption(string name, bool asPaths)
    {
        var option = new AdvancedOptionValue { Name = name };
        try
        {
            // String first: advanced options are text in Tekla's settings files, so it answers for
            // most of them; the typed overloads are the fallback for options stored otherwise.
            var text = "";
            var flag = false;
            var integer = 0;
            var number = 0.0;
            // Tekla answers true with "" for an option it does not know at all (verified live, 2023),
            // so an empty text is "unset or unknown" — never "found", and no typed overload is asked
            // after it (a bool overload could turn the same unknown option into "FALSE").
            var answered = TS.TeklaStructuresSettings.GetAdvancedOption(name, ref text);
            if (answered && !string.IsNullOrEmpty(text))
                Found(option, text, "string");
            else if (answered)
                option.Message = "Tekla returned an empty value: the option is unset in this environment — or " +
                                 "unknown; the API does not tell the two apart (names are case-sensitive).";
            else if (TS.TeklaStructuresSettings.GetAdvancedOption(name, ref flag))
                Found(option, flag ? "TRUE" : "FALSE", "bool");
            else if (TS.TeklaStructuresSettings.GetAdvancedOption(name, ref integer))
                Found(option, integer.ToString(CultureInfo.InvariantCulture), "int");
            else if (TS.TeklaStructuresSettings.GetAdvancedOption(name, ref number))
                Found(option, number.ToString("R", CultureInfo.InvariantCulture), "double");
            else
                option.Message = "Tekla does not define this advanced option in the current environment " +
                                 "(names are case-sensitive, e.g. XS_MACRO_DIRECTORY).";

            if (asPaths && option.Found)
            {
                var invalid = new List<string>();
                if (TS.TeklaStructuresSettings.GetAdvancedOptionPaths(
                        name, out var paths,
                        (advancedOption, invalidString, reason) => invalid.Add(invalidString + " — " + reason)))
                    option.Paths = paths ?? new List<string>();
                else
                    option.Paths = new List<string>();
                option.InvalidPaths.AddRange(invalid);
            }
        }
        catch (Exception ex)
        {
            option.Message = ErrorText.Flatten(ex);
        }
        return option;
    }

    private static void Found(AdvancedOptionValue option, string value, string type)
    {
        option.Found = true;
        option.Value = value;
        option.ValueType = type;
    }

    /// <summary>
    /// The installed Tekla's own Open API assemblies (the resolver's folders — 2021: nt\bin\plugins,
    /// 2023+: bin, 2026: bin + bin\Net48Runtime), where Tekla also installs the XML docs. No Tekla
    /// type is touched and no remoting happens: the generator reads metadata from the files.
    /// </summary>
    public ApiReferenceSource? GetApiReferenceSource()
    {
        try
        {
            var bin = TeklaAssemblyResolver.BinDir;
            if (string.IsNullOrWhiteSpace(bin) || !System.IO.Directory.Exists(bin)) return null;
            // Every resolver folder: Tekla 2026 keeps Tekla.Structures.dll — the Geometry3d types —
            // only in bin\Net48Runtime, and a bin-only lookup generated a reference without them
            // while reporting no warning (issue #17).
            var folders = TeklaAssemblyResolver.ProbeDirectories;
            var paths = Scripting.ApiReference.CoreAssemblyNames
                .Select(name => TeklaBinLayout.FindAssemblyFile(name, folders, System.IO.File.Exists))
                .Where(path => path != null)
                .Select(path => path!)
                .ToList();
            var modelDll = System.IO.Path.Combine(bin, "Tekla.Structures.Model.dll");
            if (!paths.Contains(modelDll, StringComparer.OrdinalIgnoreCase)) return null;
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(modelDll).FileVersion;
            if (string.IsNullOrWhiteSpace(version)) version = TeklaAssemblyResolver.CompiledVersion?.ToString() ?? "unknown";
            var source = new ApiReferenceSource
            {
                VersionKey = "tekla-" + version,
                AssemblyPaths = paths,
                // 2021: some dependencies of nt/bin/plugins live in nt/bin.
                DependencyDirectories = { System.IO.Path.GetDirectoryName(bin!.TrimEnd('\\', '/')) ?? bin! },
                Description = "the Tekla " + version + " Open API in " + string.Join(" + ", folders),
            };
            source.XmlDirectories.AddRange(folders);
            return source;
        }
        catch
        {
            return null;
        }
    }

    public CatalogListResult ListCatalog(CatalogQuery query)
    {
        query ??= new CatalogQuery();
        var kind = CatalogListing.NormalizeKind(query.Kind, out var error);
        if (kind is null) return CatalogFailure(query.Kind ?? "", error);

        try
        {
            GetConnectedModel();
            var catalog = new TSC.CatalogHandler();
            if (!catalog.GetConnectionStatus())
                return CatalogFailure(kind,
                    "The Catalog API is not connected although the model is. Its client was probably created " +
                    "while Tekla was starting; the Open API does not retry inside a process — restart this MCP " +
                    "server (reconnect it in the MCP client).");

            switch (kind)
            {
                case "profiles":
                {
                    var items = catalog.GetLibraryProfileItems();
                    items.SelectInstances = query.Details; // names alone need no per-item Select()
                    return CatalogListing.Page(kind, Drain<TSC.ProfileItem>(items),
                        p => new[] { ProfileName(p) }, p => MapProfile(p, query.Details), query, BackendName);
                }
                case "parametric_profiles":
                {
                    var items = catalog.GetParametricProfileItems();
                    items.SelectInstances = query.Details;
                    return CatalogListing.Page(kind, Drain<TSC.ProfileItem>(items),
                        p => new[] { ProfileName(p) }, p => MapProfile(p, query.Details), query, BackendName);
                }
                case "materials":
                    return CatalogListing.Page(kind, Drain<TSC.MaterialItem>(catalog.GetMaterialItems()),
                        m => new[] { m.MaterialName, m.AliasName1, m.AliasName2, m.AliasName3 },
                        m => MapMaterial(m, query.Details), query, BackendName);
                case "components":
                    return CatalogListing.Page(kind, Drain<TSC.ComponentItem>(catalog.GetComponentItems()),
                        c => new[] { c.Name, c.UIName },
                        c => new CatalogItemInfo
                        {
                            Name = c.Name ?? "",
                            Type = c.Type.ToString(),
                            Number = c.Number,
                            Properties = query.Details
                                ? new Dictionary<string, string>
                                {
                                    ["uiName"] = c.UIName ?? "",
                                    ["attributeFileExtension"] = c.AttributeFileExtension ?? "",
                                }
                                : new Dictionary<string, string>(),
                        },
                        query, BackendName);
                default: // uda_definitions
                {
                    TSC.UserPropertyItemEnumerator items;
                    if (string.IsNullOrWhiteSpace(query.ObjectType))
                        items = catalog.GetUserPropertyItems();
                    else if (Enum.TryParse<TSC.CatalogObjectTypeEnum>(query.ObjectType!.Trim(), true, out var objectType))
                        items = catalog.GetUserPropertyItems(objectType);
                    else
                        return CatalogFailure(kind,
                            $"Unknown objectType '{query.ObjectType}'. Use one of: " +
                            string.Join(", ", Enum.GetNames(typeof(TSC.CatalogObjectTypeEnum))) + ".");
                    return CatalogListing.Page(kind, Drain<TSC.UserPropertyItem>(items),
                        u => new[] { u.Name }, u => MapUdaDefinition(u, query.Details), query, BackendName);
                }
            }
        }
        catch (Exception ex)
        {
            return CatalogFailure(kind, ErrorText.Flatten(ex));
        }
    }

    private static CatalogListResult CatalogFailure(string kind, string? message) =>
        new CatalogListResult { Kind = kind, Backend = BackendName, Message = message };

    /// <summary>The catalog enumerators are non-generic IEnumerators with a typed Current.</summary>
    private static IEnumerable<T> Drain<T>(IEnumerator enumerator) where T : class
    {
        while (enumerator.MoveNext())
            if (enumerator.Current is T item) yield return item;
    }

    private static string ProfileName(TSC.ProfileItem item) =>
        item is TSC.LibraryProfileItem library ? library.ProfileName ?? ""
        : item is TSC.ParametricProfileItem parametric ? parametric.ProfilePrefix ?? ""
        : "";

    private static CatalogItemInfo MapProfile(TSC.ProfileItem item, bool details)
    {
        var info = new CatalogItemInfo { Name = ProfileName(item) };
        if (!details) return info; // type and parameters need the per-item Select()

        info.Type = item.ProfileItemType.ToString();
        info.SubType = item.ProfileItemSubType.ToString();
        try
        {
            if (!string.IsNullOrWhiteSpace(item.ParameterString))
                info.Properties["parameterString"] = item.ParameterString;
            foreach (var entry in item.aProfileItemParameters ?? new ArrayList())
            {
                if (!(entry is TSC.ProfileItemParameter parameter)) continue;
                var key = !string.IsNullOrWhiteSpace(parameter.Symbol) ? parameter.Symbol : parameter.Property;
                if (string.IsNullOrWhiteSpace(key) || info.Properties.ContainsKey(key)) continue;
                info.Properties[key] = !string.IsNullOrWhiteSpace(parameter.StringValue)
                    ? parameter.StringValue
                    : parameter.Value.ToString("R", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex)
        {
            info.Properties["error"] = ErrorText.Flatten(ex);
        }
        return info;
    }

    private static CatalogItemInfo MapMaterial(TSC.MaterialItem item, bool details)
    {
        var info = new CatalogItemInfo { Name = item.MaterialName ?? "", Type = item.Type.ToString() };
        if (!details) return info;
        info.Properties["aliases"] = string.Join(", ",
            new[] { item.AliasName1, item.AliasName2, item.AliasName3 }.Where(a => !string.IsNullOrWhiteSpace(a)));
        info.Properties["profileDensity"] = item.ProfileDensity.ToString("R", CultureInfo.InvariantCulture);
        info.Properties["plateDensity"] = item.PlateDensity.ToString("R", CultureInfo.InvariantCulture);
        info.Properties["modulusOfElasticity"] = item.ModulusOfElasticity.ToString("R", CultureInfo.InvariantCulture);
        info.Properties["poissonsRatio"] = item.PoissonsRatio.ToString("R", CultureInfo.InvariantCulture);
        info.Properties["thermalDilatation"] = item.ThermalDilatation.ToString("R", CultureInfo.InvariantCulture);
        info.Properties["designCode"] = item.DesignCode.ToString(CultureInfo.InvariantCulture);
        return info;
    }

    private static CatalogItemInfo MapUdaDefinition(TSC.UserPropertyItem item, bool details)
    {
        var info = new CatalogItemInfo { Name = item.Name ?? "", Type = item.Type.ToString() };
        if (!details) return info;
        // TODO(windows): verify the enumerator hands out fully selected items (label, object
        // types); UserPropertyItem.Select() exists if it does not.
        try
        {
            info.SubType = item.FieldType.ToString();
            info.Properties["label"] = item.GetLabel() ?? "";
            info.Properties["level"] = item.Level.ToString();
            info.Properties["affectsNumbering"] = item.AffectsNumbering ? "true" : "false";
            info.Properties["unique"] = item.Unique ? "true" : "false";
            var objectTypes = new List<TSC.CatalogObjectTypeEnum>();
            if (item.GetObjectTypes(ref objectTypes))
                info.Properties["objectTypes"] = string.Join(",", objectTypes);
        }
        catch (Exception ex)
        {
            info.Properties["error"] = ErrorText.Flatten(ex);
        }
        return info;
    }
}
