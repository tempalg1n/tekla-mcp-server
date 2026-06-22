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
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(); // discovers [McpServerToolType] classes in this assembly

await builder.Build().RunAsync();
