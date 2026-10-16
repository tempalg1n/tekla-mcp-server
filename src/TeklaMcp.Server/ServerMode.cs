using ModelContextProtocol;
using TeklaMcp.Core;

namespace TeklaMcp.Server;

/// <summary>
/// The operator switches this process was started with (<see cref="ToolExposure"/>), set once in
/// Program.cs before any tool runs.
///
/// Read-only mode does not trust the annotations alone: <see cref="ToolRegistration"/> serves only
/// tools annotated <c>ReadOnly = true</c>, and every write path calls <see cref="ThrowIfReadOnly"/>
/// before its first side effect, so a writer annotated read-only by mistake still cannot write.
/// </summary>
internal static class ServerMode
{
    public static bool ReadOnly { get; private set; }

    /// <summary>The toolsets served, in <see cref="ToolExposure.AllToolsets"/> order; null = all.</summary>
    public static IReadOnlyList<string>? Toolsets { get; private set; }

    public static void Configure(bool readOnly, IReadOnlyList<string>? toolsets)
    {
        ReadOnly = readOnly;
        Toolsets = toolsets;
    }

    /// <summary>Throws (nothing done) in read-only mode. Call before the first write or UI change.</summary>
    public static void ThrowIfReadOnly(string operation)
    {
        if (ReadOnly) throw new McpException(ToolExposure.ReadOnlyRefusal(operation));
    }
}
