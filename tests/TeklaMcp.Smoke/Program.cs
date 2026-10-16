using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tests/TeklaMcp.Smoke -- <server.dll> <expected-tool-count>");
    return 2;
}

var serverDll = Path.GetFullPath(args[0]);
var expectedToolCount = int.Parse(args[1]);
if (!File.Exists(serverDll))
{
    Console.Error.WriteLine("Server assembly not found: " + serverDll);
    return 2;
}

// Three server launches: default, read-only (TEKLA_MCP_READ_ONLY) and one toolset (TEKLA_MCP_TOOLSETS).
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
const string ReadOnlyVariable = "TEKLA_MCP_READ_ONLY";
const string ToolsetsVariable = "TEKLA_MCP_TOOLSETS";

// --- Default mode: the full tool set -------------------------------------------------------
IList<McpClientTool> tools;
await using (var client = await Connect(new Dictionary<string, string?>
{
    // The default run must not inherit an operator's switches from this shell.
    [ReadOnlyVariable] = null,
    [ToolsetsVariable] = null,
}))
{
    tools = await client.ListToolsAsync(
        options: null,
        cancellationToken: timeout.Token);

    Require(client.ServerInfo.Name == "tekla-mcp",
        "initialize returned unexpected server name: " + client.ServerInfo.Name);
    Require(client.ServerInfo.Version == "0.8.1",
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
        "tekla_export_parts_file",
        "tekla_set_udas_from_file",
        "tekla_export_reference_objects_file",
        "tekla_get_part_curve_geometry",
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

    // Every tool declares its behaviour hints explicitly (AGENTS.md §4): clients build their
    // approval UX on them, and read-only mode serves exactly the readOnlyHint=true tools.
    var unannotated = new List<string>();
    foreach (var tool in tools)
    {
        var hints = tool.ProtocolTool.Annotations;
        var missing = new List<string>();
        if (hints?.ReadOnlyHint is null) missing.Add("readOnlyHint");
        if (hints?.OpenWorldHint is null) missing.Add("openWorldHint");
        if (hints?.ReadOnlyHint == false)
        {
            if (hints.DestructiveHint is null) missing.Add("destructiveHint");
            if (hints.IdempotentHint is null) missing.Add("idempotentHint");
        }
        if (missing.Count > 0) unannotated.Add(tool.Name + " (" + string.Join(", ", missing) + ")");
    }
    Require(unannotated.Count == 0,
        "Tools without explicit annotations — set them on [McpServerTool]: " + string.Join("; ", unannotated));
    RequireHints(tools, "tekla_list_objects", readOnly: true);
    RequireHints(tools, "tekla_check_csharp", readOnly: true);
    RequireHints(tools, "tekla_create_beam", readOnly: false, destructive: false);
    RequireHints(tools, "tekla_delete_objects", readOnly: false, destructive: true);
    RequireHints(tools, "tekla_run_csharp", readOnly: false, destructive: true);
    RequireHints(tools, "tekla_export_parts_file", readOnly: false, destructive: true);
    RequireHints(tools, "tekla_select_objects", readOnly: false, destructive: false);

    var statusText = await CallText(client, "tekla_get_connection_info", null);
    Require(statusText.Contains("\"serverProcessId\":") && statusText.Contains("\"serverVersion\":\""),
        "Status tool does not report the server identity: " + statusText);
    Require(statusText.Contains("\"serverReadOnly\":false"),
        "Status tool does not report serverReadOnly=false in the default mode: " + statusText);

    // A mutating script without the approved hash must be refused before the backend sees it.
    var unapprovedText = await CallText(client, "tekla_run_csharp",
        new Dictionary<string, object?> { ["code"] = "new Model().CommitChanges();", ["allowMutations"] = true });
    Require(unapprovedText.Contains("requires expectedSha256"),
        "Mutating tekla_run_csharp without expectedSha256 was not rejected: " + unapprovedText);
}

// --- Read-only mode: only readOnlyHint=true tools, writes refused ---------------------------
var readOnlyNames = tools
    .Where(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint == true)
    .Select(tool => tool.Name)
    .OrderBy(name => name, StringComparer.Ordinal)
    .ToList();
int readOnlyCount;
await using (var client = await Connect(new Dictionary<string, string?>
{
    [ReadOnlyVariable] = "1",
    [ToolsetsVariable] = null,
}))
{
    var served = (await client.ListToolsAsync(options: null, cancellationToken: timeout.Token))
        .Select(tool => tool.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();
    readOnlyCount = served.Count;
    Require(served.SequenceEqual(readOnlyNames, StringComparer.Ordinal),
        "Read-only mode must serve exactly the readOnlyHint=true tools. Extra: " +
        string.Join(", ", served.Except(readOnlyNames)) + "; missing: " + string.Join(", ", readOnlyNames.Except(served)));
    Require(client.ServerInstructions?.Contains("READ-ONLY MODE") == true,
        "Read-only mode does not announce itself in the server instructions.");

    var statusText = await CallText(client, "tekla_get_connection_info", null);
    Require(statusText.Contains("\"serverReadOnly\":true"),
        "Status tool does not report serverReadOnly=true in read-only mode: " + statusText);

    foreach (var write in new[] { "tekla_delete_objects", "tekla_run_csharp", "tekla_select_objects" })
    {
        var refusal = await TryCall(client, write, new Dictionary<string, object?>
        {
            ["code"] = "1",
            ["guidIn"] = "00000000-0000-0000-0000-000000000000",
            ["apply"] = true,
        });
        Require(refusal != null, "Read-only mode let " + write + " run.");
    }
}

// --- One toolset: core is always served, the other toolsets are not --------------------------
int drawingsCount;
await using (var client = await Connect(new Dictionary<string, string?>
{
    [ReadOnlyVariable] = null,
    [ToolsetsVariable] = "drawings",
}))
{
    var served = (await client.ListToolsAsync(options: null, cancellationToken: timeout.Token))
        .Select(tool => tool.Name)
        .ToList();
    drawingsCount = served.Count;
    foreach (var name in new[] { "tekla_get_connection_info", "tekla_report_gap", "tekla_list_drawings" })
        Require(served.Contains(name), "TEKLA_MCP_TOOLSETS=drawings does not serve " + name + ".");
    foreach (var name in new[] { "tekla_list_objects", "tekla_run_csharp", "tekla_capture_view", "tekla_export_parts_file" })
        Require(!served.Contains(name), "TEKLA_MCP_TOOLSETS=drawings still serves " + name + ".");
    Require(client.ServerInstructions?.Contains("REDUCED TOOL SET") == true,
        "A reduced tool set does not announce itself in the server instructions.");
}

Console.WriteLine(
    "MCP smoke passed: tekla-mcp " + tools.Count + " unique annotated tools; read-only mode " +
    readOnlyCount + "; toolset 'drawings' " + drawingsCount + ".");
return 0;

async Task<McpClient> Connect(Dictionary<string, string?> environment)
{
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "tekla-mcp-smoke",
        Command = "dotnet",
        Arguments = new[] { serverDll },
        WorkingDirectory = Path.GetDirectoryName(serverDll),
        // This launches this repository's own server binary. Preserve DOTNET_ROOT/runtime
        // settings so the smoke works on developer machines and CI images alike.
        InheritEnvironmentVariables = true,
        EnvironmentVariables = environment,
        ShutdownTimeout = TimeSpan.FromSeconds(3),
        StandardErrorLines = line => Console.Error.WriteLine("[server] " + line),
    });
    return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
}

async Task<string> CallText(McpClient client, string tool, Dictionary<string, object?>? arguments)
{
    var result = await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
    Require(result.IsError != true, tool + " returned an MCP error: " + Text(result));
    return Text(result);
}

// The refusal text, or null when the call went through.
async Task<string?> TryCall(McpClient client, string tool, Dictionary<string, object?> arguments)
{
    try
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
        return result.IsError == true ? Text(result) : null;
    }
    catch (McpException ex)
    {
        return ex.Message;
    }
}

static string Text(CallToolResult result) =>
    string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

static void RequireHints(IList<McpClientTool> tools, string name, bool readOnly, bool? destructive = null)
{
    var hints = tools.Single(tool => tool.Name == name).ProtocolTool.Annotations;
    Require(hints?.ReadOnlyHint == readOnly, name + ": expected readOnlyHint=" + readOnly + ".");
    if (destructive is bool d)
        Require(hints?.DestructiveHint == d, name + ": expected destructiveHint=" + d + ".");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
