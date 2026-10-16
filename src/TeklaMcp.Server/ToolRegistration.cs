using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TeklaMcp.Core;

namespace TeklaMcp.Server;

/// <summary>The toolset (<c>TEKLA_MCP_TOOLSETS</c>) of every tool in a <c>[McpServerToolType]</c> class.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class ToolsetAttribute : Attribute
{
    public ToolsetAttribute(string name) => Name = name;

    /// <summary>One of <see cref="ToolExposure.AllToolsets"/>.</summary>
    public string Name { get; }
}

/// <summary>
/// The filtered equivalent of <c>WithToolsFromAssembly()</c>: registers the <c>[McpServerTool]</c>
/// methods of this assembly that the operator's switches allow. Filtering happens on the attributes
/// BEFORE a tool exists, so it fails closed: in read-only mode a tool that does not declare
/// <c>ReadOnly = true</c> is never created.
/// </summary>
internal static class ToolRegistration
{
    internal sealed class Entry
    {
        public Entry(MethodInfo method, McpServerToolAttribute attribute, string toolset)
        {
            Method = method;
            Attribute = attribute;
            Toolset = toolset;
        }

        public MethodInfo Method { get; }
        public McpServerToolAttribute Attribute { get; }
        public string Toolset { get; }
    }

    /// <summary>Every tool method in this assembly. Throws on a tool class without a valid toolset.</summary>
    public static List<Entry> Discover()
    {
        var entries = new List<Entry>();
        foreach (var type in typeof(ToolRegistration).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null) continue;
            var toolset = type.GetCustomAttribute<ToolsetAttribute>()?.Name;
            if (toolset == null || !ToolExposure.AllToolsets.Contains(toolset))
                throw new InvalidOperationException(
                    type.FullName + " needs [Toolset(...)] naming one of: " + string.Join(", ", ToolExposure.AllToolsets) + ".");

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(all))
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute == null) continue;
                // Static only: an instance tool would need a per-call target factory, which none uses.
                if (!method.IsStatic)
                    throw new InvalidOperationException(type.FullName + "." + method.Name + ": tool methods must be static.");
                entries.Add(new Entry(method, attribute, toolset));
            }
        }
        return entries;
    }

    /// <summary>The tools to serve: read-only mode keeps <c>ReadOnly = true</c> tools; toolsets null = all.</summary>
    public static List<Entry> Select(List<Entry> discovered, bool readOnly, IReadOnlyList<string>? toolsets) =>
        discovered
            .Where(entry => !readOnly || entry.Attribute.ReadOnly)
            .Where(entry => toolsets == null || toolsets.Contains(entry.Toolset))
            .ToList();

    public static IMcpServerBuilder WithSelectedTools(this IMcpServerBuilder builder, IEnumerable<Entry> tools)
    {
        foreach (var tool in tools)
        {
            var method = tool.Method;
            // What WithTools<T>() does for a static method: DI services are resolved per call.
            builder.Services.AddSingleton(services =>
                McpServerTool.Create(method, target: null, new McpServerToolCreateOptions { Services = services }));
        }
        return builder;
    }
}
