using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeklaMcp.Core;

namespace TeklaMcp.Server;

/// <summary>
/// Makes a failing tool say WHY. For every exception except <see cref="McpException"/> the MCP
/// SDK sends only "An error occurred invoking '&lt;tool&gt;'." (verified on 1.4.0 with an
/// in-memory client). So the backends' diagnostics — e.g. "No connection to Tekla Structures …
/// model channel …, published Tekla pipes …" from a lost connection — were built and then
/// dropped, and agents saw a bare tool error with no cause (DEV-005 field report, H19).
///
/// The SDK turns exceptions into that text only AFTER the call-tool filter pipeline, so this
/// filter still sees the original exception and returns its flattened text in the same shape
/// the SDK uses for McpException. This is a local,
/// single-user stdio server: the flattened message (paths, channel names, the Tekla error) is
/// exactly what the agent and the user need to act on. McpException and cancellation keep the
/// SDK's own handling.
/// </summary>
internal static class ToolErrorFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not McpException && ex is not OperationCanceledException)
            {
                var tool = context.Params?.Name ?? "(unknown tool)";
                var message = ErrorText.Flatten(ex);
                Console.Error.WriteLine($"[tool] {tool} failed: {message}"); // stderr: stdout is the protocol
                return new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock>
                    {
                        new TextContentBlock { Text = $"An error occurred invoking '{tool}': {message}" },
                    },
                };
            }
        };
}
