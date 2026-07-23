using ModelContextProtocol.Client;

if (args.Length < 1)
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tests/TeklaMcp.Smoke -- <server.dll> [expected-tool-count]");
    return 2;
}

var serverDll = Path.GetFullPath(args[0]);
var expectedToolCount = args.Length > 1 ? int.Parse(args[1]) : 100;
if (!File.Exists(serverDll))
{
    Console.Error.WriteLine("Server assembly not found: " + serverDll);
    return 2;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "tekla-mcp-smoke",
    Command = "dotnet",
    Arguments = new[] { serverDll },
    WorkingDirectory = Path.GetDirectoryName(serverDll),
    // This launches this repository's own server binary. Preserve DOTNET_ROOT/runtime
    // settings so the smoke works on developer machines and CI images alike.
    InheritEnvironmentVariables = true,
    ShutdownTimeout = TimeSpan.FromSeconds(3),
    StandardErrorLines = line => Console.Error.WriteLine("[server] " + line),
});

await using var client = await McpClient.CreateAsync(
    transport,
    cancellationToken: timeout.Token);
var tools = await client.ListToolsAsync(
    options: null,
    cancellationToken: timeout.Token);

Require(client.ServerInfo.Name == "tekla-mcp",
    "initialize returned unexpected server name: " + client.ServerInfo.Name);
Require(client.ServerInfo.Version == "0.7.0",
    "initialize returned unexpected server version: " + client.ServerInfo.Version);
Require(!string.IsNullOrWhiteSpace(client.ServerInstructions),
    "initialize returned no server instructions.");
Require(tools.Count == expectedToolCount,
    "tools/list returned " + tools.Count + " tools; expected " + expectedToolCount + ".");
Require(tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() == tools.Count,
    "tools/list contains duplicate names.");

var requiredTools = new[]
{
    "tekla_get_reference_geometry",
    "tekla_create_beams",
    "tekla_create_connection",
    "tekla_get_drawing_sheet",
    "tekla_create_ga_drawing_view",
    "tekla_check_csharp",
    "tekla_run_csharp",
};
foreach (var name in requiredTools)
{
    var tool = tools.SingleOrDefault(candidate => candidate.Name == name);
    Require(tool != null, "Required tool is missing: " + name);
    Require(!string.IsNullOrWhiteSpace(tool!.Description),
        "Required tool has no description: " + name);
    Require(tool.JsonSchema.ValueKind == System.Text.Json.JsonValueKind.Object,
        "Required tool has no object input schema: " + name);
}

var statusTool = tools.Single(tool => tool.Name == "tekla_get_connection_info");
var status = await statusTool.CallAsync(
    arguments: null,
    cancellationToken: timeout.Token);
Require(status.IsError != true, "Mock status tool returned an MCP error.");

Console.WriteLine(
    "MCP smoke passed: " + client.ServerInfo.Name + " " +
    client.ServerInfo.Version + ", " + tools.Count + " unique tools.");
return 0;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
