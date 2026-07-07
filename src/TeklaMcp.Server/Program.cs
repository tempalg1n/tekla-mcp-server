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
// Server instructions tell the connecting agent HOW to use this server: do the work with the
// provided tools, and when something is missing, REPORT it (tekla_report_gap) instead of
// scripting around it. Sent to the client in the MCP `initialize` response.
const string serverInstructions =
    "Tekla MCP server. Use these tools to read, analyze, and (with preview-by-default safety) " +
    "create/edit/delete objects in the live Tekla Structures model.\n\n" +
    "Work WITH the tools. Do NOT write ad-hoc scripts, macros, or external automation to work " +
    "around a limitation, and never fabricate or guess model data.\n\n" +
    "If a needed operation or data field is not available, or an existing tool returns " +
    "insufficient data: stop and (1) tell the user exactly what is missing and what you were " +
    "trying to do; (2) call `tekla_report_gap` to produce a structured capability request " +
    "(a ready-to-file issue draft); (3) offer to file it as a GitHub issue, or ask the user to.\n\n" +
    "Write tools (create/edit/delete) default to apply=false (preview). Show the plan and only " +
    "set apply=true after the user confirms.";

builder.Services
    .AddMcpServer(options => options.ServerInstructions = serverInstructions)
    .WithStdioServerTransport()
    .WithToolsFromAssembly(); // discovers [McpServerToolType] classes in this assembly

await builder.Build().RunAsync();
