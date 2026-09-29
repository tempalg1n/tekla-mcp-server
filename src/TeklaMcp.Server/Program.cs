using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using TeklaMcp.Core;
using TeklaMcp.Mock;

// ---------------------------------------------------------------------------
// Tekla MCP server — entry point.
//
// Transport: stdio. The MCP client (Claude Desktop, Claude Code, etc.) launches
// this process and speaks JSON-RPC over stdin/stdout. CRITICAL: stdout is reserved
// for the protocol, so ALL logging must go to stderr (configured below).
// ---------------------------------------------------------------------------

// Nothing in this process may write to stdout except the MCP transport (which uses the raw
// Console.OpenStandardOutput stream, not Console.Out). The Tekla Open API itself does
// Console.WriteLine("Connection failed : …") when a remoting channel fails — one such line
// would corrupt the JSON-RPC framing. Route all Console.Out writers to stderr up front.
Console.SetOut(Console.Error);

// The server has no appsettings/user-secrets dependency. Disabling generic-host defaults keeps
// stdio startup deterministic and avoids host configuration/file-watcher stalls when a net8
// artifact rolls forward to a newer installed .NET runtime.
var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    DisableDefaults = true,
    ContentRootPath = Directory.GetCurrentDirectory(),
});

// Route every log to stderr to keep stdout clean for the MCP protocol.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// --- Pick the model backend -------------------------------------------------
// net8.0 build (macOS/dev): always Mock — there is no Tekla.
// net48 build (Windows): real Tekla, unless TEKLA_MCP_USE_MOCK=1 forces the mock
// (handy for smoke-testing the server on Windows without a model open).
var forceMock = Environment.GetEnvironmentVariable("TEKLA_MCP_USE_MOCK") == "1";

#if NET48
if (forceMock)
{
    builder.Services.AddSingleton<ITeklaModelService, MockTeklaModelService>();
}
else
{
    // Per-version build: this artifact is compiled for ONE Tekla version. The resolver
    // loads the Tekla Open API assemblies from the installed/running Tekla and fails fast
    // on a version mismatch. Must run before the first Tekla type is touched.
    TeklaMcp.Tekla.TeklaAssemblyResolver.Register();
    TeklaMcp.Tekla.TeklaRemotingChannel.Align();
    builder.Services.AddSingleton<ITeklaModelService, TeklaMcp.Tekla.TeklaModelService>();
}
#else
_ = forceMock; // mock is the only option on this TFM
builder.Services.AddSingleton<ITeklaModelService, MockTeklaModelService>();
#endif

// --- Register the MCP server + tools ---------------------------------------
// Server instructions tell the connecting agent HOW to use this server: dedicated tools first,
// then the sanctioned script escape hatch (tekla_run_csharp), and always report gaps
// (tekla_report_gap). Sent to the client in the MCP `initialize` response.
const string serverInstructions =
    "Tekla MCP server. Use these tools to read, analyze, and (with preview-by-default safety) " +
    "create/edit/delete objects in the live Tekla Structures model and drawings.\n\n" +
    "Escalation ladder when you need something:\n" +
    "1. PREFER a dedicated tekla_* tool — fastest and safest.\n" +
    "2. If no tool covers the need, use the sanctioned escape hatch: verify API signatures with " +
    "`tekla_search_api` / `tekla_get_api_doc`, compile the exact source with `tekla_check_csharp`, " +
    "then run it with `tekla_run_csharp` (read-only by default, policy-checked, execution deadline). " +
    "Never guess Tekla API members.\n" +
    "3. Whether or not a script worked, call `tekla_report_gap` for anything recurring so it can " +
    "become a first-class tool; offer the user the ready-to-file issue draft it returns.\n\n" +
    "Never use EXTERNAL automation (files, macros outside this server) and never fabricate or " +
    "guess model data. On the Mock backend scripts are validated but not executed — say so " +
    "instead of inventing results.\n\n" +
    "WHOLE-MODEL ANALYTICS: use the streaming tools (tekla_group_weight_by / tekla_sum_weight / " +
    "tekla_list_distinct_values) — they support grouping by user fields ('uda:USER_FIELD_1') and " +
    "never read solids. Unknown field layout? tekla_discover_udas first, then " +
    "tekla_find_attributes_by_value — and always check its scannedObjects/truncated before " +
    "declaring a value absent. Models can exceed 400k objects while MCP clients abort requests " +
    "after ~60 s and the result is lost even if the server finishes: cap heavy scans with " +
    "maxObjects and continue via cursor=nextCursor instead of raising timeouts. Before a " +
    "scripting session, call tekla_get_api_reference_status once — it tells you whether " +
    "tekla_search_api can verify signatures on this machine.\n\n" +
    "BULK DATA IN AND OUT goes through a file, never through your context: tekla_export_parts_file " +
    "(objects with optional solidAabb/coordSystem/contourPoints/cog/uda:NAME), " +
    "tekla_export_reference_objects_file (IFC objects with world AABB + placement) and " +
    "tekla_set_udas_from_file (GUID-keyed UDA writes). Reach for them as soon as the job involves " +
    "more than a few hundred objects or per-object geometry — do NOT page thousands of rows through " +
    "tekla_find_objects/tekla_get_reference_geometry, and do NOT put long GUID lists in a script. " +
    "They only accept absolute paths under the configured roots (TEKLA_MCP_FILE_ROOT, default " +
    "%LOCALAPPDATA%\\TeklaMcp\\exchange plus the model folder), page with maxObjects + cursor + " +
    "append, and return counters only — the data stays in the file.\n\n" +
    "Model and drawing write/UI tools default to apply=false (preview). Drawing points explicitly " +
    "distinguish view-local, global model, and sheet/paper-mm coordinate spaces. Show the plan and " +
    "only set apply=true after the user confirms. The same contract applies to scripted mutations: " +
    "show the user the script and what it will change, get their explicit go-ahead, only then " +
    "rerun with allowMutations=true — and keep changes traceable (MCP_ORIGIN UDA) and " +
    "reversible (Tekla Ctrl+Z).\n\n" +
    "The DRAWING tool layer (tekla_*drawing*) is EXPERIMENTAL: new in v0.7.0 with limited live " +
    "testing, and Tekla's Drawing API has version-specific quirks. If a drawing tool fails " +
    "unexpectedly, tell the user plainly and report it with tekla_report_gap instead of " +
    "retrying blindly or scripting around it.\n\n" +
    "CURVED PARTS: radius and curved dimensions need exact arc points. Get them from " +
    "tekla_get_part_curve_geometry (bends with radius/chord/sagitta/arc length, inner/outer arcs " +
    "of round tubes and bars, optional view coordinates) — never estimate them from bounding " +
    "boxes or from the segmented polyline of a drawing part.";

var informationalVersion = System.Reflection.Assembly.GetExecutingAssembly()
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
    .FirstOrDefault()?.InformationalVersion;
var serverVersion = (informationalVersion ?? "0.7.0").Split('+')[0];

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "tekla-mcp",
            Title = "Tekla MCP Server",
            Version = serverVersion,
        };
        options.ServerInstructions = serverInstructions;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly() // discovers [McpServerToolType] classes in this assembly
    // Pass the real exception text to the agent instead of the SDK's bare
    // "An error occurred invoking '<tool>'." (see ToolErrorFilter).
    .WithRequestFilters(filters => filters.AddCallToolFilter(TeklaMcp.Server.ToolErrorFilter.Create));

var app = builder.Build();
await app.RunAsync();
