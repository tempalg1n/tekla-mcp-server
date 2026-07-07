using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeklaMcp.Core;
using TeklaMcp.Mock;

// ---------------------------------------------------------------------------
// Tekla MCP server — entry point.
//
// Transport: stdio. The MCP client (Claude Desktop, Claude Code, etc.) launches
// this process and speaks JSON-RPC over stdin/stdout. CRITICAL: stdout is reserved
// for the protocol, so ALL logging must go to stderr (configured below).
// ---------------------------------------------------------------------------

var builder = Host.CreateApplicationBuilder(args);

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
    // One build, any Tekla version: resolve the Tekla Open API assemblies from the
    // installed/running Tekla at runtime. Must run before the first Tekla type is touched.
    TeklaMcp.Tekla.TeklaAssemblyResolver.Register();
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
    "create/edit/delete objects in the live Tekla Structures model.\n\n" +
    "Escalation ladder when you need something:\n" +
    "1. PREFER a dedicated tekla_* tool — fastest and safest.\n" +
    "2. If no tool covers the need, use the sanctioned escape hatch: verify API signatures with " +
    "`tekla_search_api` / `tekla_get_api_doc`, then run a short C# script with `tekla_run_csharp` " +
    "(read-only by default, policy-checked, hard timeout). Never guess Tekla API members.\n" +
    "3. Whether or not a script worked, call `tekla_report_gap` for anything recurring so it can " +
    "become a first-class tool; offer the user the ready-to-file issue draft it returns.\n\n" +
    "Never use EXTERNAL automation (files, macros outside this server) and never fabricate or " +
    "guess model data. On the Mock backend scripts are validated but not executed — say so " +
    "instead of inventing results.\n\n" +
    "Write tools (create/edit/delete) default to apply=false (preview). Show the plan and only " +
    "set apply=true after the user confirms. The same contract applies to scripted mutations: " +
    "show the user the script and what it will change, get their explicit go-ahead, only then " +
    "rerun with allowMutations=true — and keep changes traceable (MCP_ORIGIN UDA) and " +
    "reversible (Tekla Ctrl+Z).";

builder.Services
    .AddMcpServer(options => options.ServerInstructions = serverInstructions)
    .WithStdioServerTransport()
    .WithToolsFromAssembly(); // discovers [McpServerToolType] classes in this assembly

await builder.Build().RunAsync();
