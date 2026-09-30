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
///
/// A lost Tekla connection (<see cref="ConnectionErrors.IsTeklaConnectionFailure"/>) can surface
/// from ANY tool, not only from <c>tekla_get_connection_info</c> — the first real remoting call
/// after a Tekla restart throws a bare, localized <c>RemotingException</c>. The live build passes
/// <paramref name="connectionDiagnoser"/> (<c>TeklaRemotingChannel.DiagnoseConnectionFailure</c>)
/// so those failures read as cause + action + channel state instead.
/// </summary>
internal static class ToolErrorFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        Func<Exception, string>? connectionDiagnoser = null) =>
        async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not McpException && ex is not OperationCanceledException)
            {
                var tool = context.Params?.Name ?? "(unknown tool)";
                var message = Describe(ex, connectionDiagnoser);
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

    internal static string Describe(Exception ex, Func<Exception, string>? connectionDiagnoser)
    {
        if (connectionDiagnoser is null || !ConnectionErrors.IsTeklaConnectionFailure(ex))
            return ErrorText.Flatten(ex);
        try
        {
            return connectionDiagnoser(ex);
        }
        catch (Exception diagnoserFailure)
        {
            // Never let the diagnosis hide the original error.
            return ErrorText.Flatten(ex) + " (connection diagnostics failed: " + ErrorText.Flatten(diagnoserFailure) + ")";
        }
    }
}
