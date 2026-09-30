using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// Read-only access to the Tekla ENVIRONMENT — advanced options and catalogs — which component
/// development kept scripting around (DEV-005, backlog §5).
/// </summary>
[McpServerToolType]
public static class ModelEnvironmentTools
{
    [McpServerTool(Name = "tekla_get_advanced_options")]
    [Description("Read advanced options (XS_* variables) as the running Tekla resolves them for the open " +
                 "model — environment, firm, project and model settings already merged. Use it instead of " +
                 "guessing folders: XS_MACRO_DIRECTORY (macro/plugin folders), XS_FIRM, XS_PROJECT, XS_SYSTEM, " +
                 "XS_MODEL_TEMPLATE_DIRECTORY, …. Names are case-sensitive. An unknown option comes back with " +
                 "found=false, never as an error. asPaths=true also splits ';'-separated values into paths " +
                 "(Tekla's own parsing; paths need not exist). Read-only.")]
    public static IReadOnlyList<AdvancedOptionValue> GetAdvancedOptions(
        ITeklaModelService model,
        [Description("Advanced option names, comma/semicolon/newline separated, e.g. 'XS_MACRO_DIRECTORY,XS_FIRM'.")]
        string names,
        [Description("Also return the value split into paths (for *_DIRECTORY / path-list options). Default false.")]
        bool asPaths = false)
        => model.GetAdvancedOptions(ToolHelpers.ParseList(names), asPaths);

    [McpServerTool(Name = "tekla_list_catalog")]
    [Description("List an environment catalog of the running Tekla: kind = profiles (library profiles), " +
                 "parametric_profiles, materials, components (connections, components, details, seams, custom " +
                 "parts, plugins — with their numbers), or uda_definitions (every defined user attribute, with " +
                 "its type; objectType narrows it, e.g. STEEL_BEAM, BOLT, ASSEMBLY_DRAWING). This is the " +
                 "authoritative list of what EXISTS in the environment — tekla_discover_udas only samples what " +
                 "objects carry. nameContains filters by substring (components: name or UI name). details=true " +
                 "adds per-item data (profile dimensions, material densities, UDA label/object types) at a " +
                 "remoting call per item — keep limit small then. Paged: while truncated=true, repeat with " +
                 "cursor=nextCursor; 'scanned' says how much of the catalog a filtered page looked at. Read-only.")]
    public static CatalogListResult ListCatalog(
        ITeklaModelService model,
        [Description("profiles, parametric_profiles, materials, components or uda_definitions.")] string kind,
        [Description("Case-insensitive name substring. Empty = everything.")] string? nameContains = null,
        [Description("uda_definitions only: CatalogObjectTypeEnum name, e.g. PART, STEEL_BEAM, BOLT, GA_DRAWING.")]
        string? objectType = null,
        [Description("Read per-item details (slower: one remoting call per item). Default false.")] bool details = false,
        [Description("Items per page (default 200, max 1000).")] int limit = 200,
        [Description("Continuation cursor from the previous page's nextCursor.")] string? cursor = null)
    {
        if (CatalogListing.NormalizeKind(kind, out var error) is null)
            throw new ModelContextProtocol.McpException(error ?? "Unknown catalog kind.");
        return model.ListCatalog(new CatalogQuery
        {
            Kind = kind,
            NameContains = nameContains,
            ObjectType = objectType,
            Details = details,
            Limit = limit,
            Cursor = cursor,
        });
    }
}
