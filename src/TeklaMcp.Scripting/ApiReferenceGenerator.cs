using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace TeklaMcp.Scripting;

/// <summary>
/// Writes the Markdown Tekla Open API reference that <see cref="ApiReference"/> searches: one
/// <c>Full.Type.Name.md</c> per public type (constructors, properties, methods, fields, enum values,
/// XML-doc summaries) plus an <c>INDEX.md</c>. Shared by the <c>tools/TeklaApiDoc</c> CLI and the
/// server, which builds it on first use from the installed Tekla (backlog §5).
///
/// Metadata only, via <see cref="MetadataLoadContext"/>: nothing is loaded into the calling process
/// and no Tekla code runs, so it is safe inside the live server next to the real connection, and
/// it reads net48 assemblies from net8 too. Tekla content never leaves the machine it was
/// generated on — the output is a local cache, not something to publish.
/// </summary>
public static class ApiReferenceGenerator
{
    public static readonly IReadOnlyList<string> DefaultNamespaces = new[] { "Tekla.Structures" };

    /// <summary>
    /// Bump when the output changes: cached references carry it in their folder name.
    /// 3: the live source also searches bin\Net48Runtime — a Tekla 2026 reference cached as r2 lacks
    /// Tekla.Structures (Geometry3d) and must not be served again (issue #17).
    /// </summary>
    public const int FormatVersion = 3;

    /// <summary>Generates into <paramref name="outDir"/>; returns the number of documented types.</summary>
    public static int Generate(
        IEnumerable<string> assemblyPaths,
        IEnumerable<string>? xmlDirectories,
        string outDir,
        IEnumerable<string>? namespaces = null,
        Action<string>? log = null,
        IEnumerable<string>? dependencyDirectories = null)
    {
        var dlls = assemblyPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dlls.Count == 0) throw new ArgumentException("No assemblies to document.", nameof(assemblyPaths));
        var nsFilters = (namespaces ?? DefaultNamespaces).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (nsFilters.Count == 0) nsFilters.AddRange(DefaultNamespaces);

        // Resolver: the targets, their sibling DLLs (dependencies) and the runtime's own DLLs (the
        // core assembly — mscorlib on .NET Framework, System.Private.CoreLib on .NET 8).
        var resolverPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddPath(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!resolverPaths.ContainsKey(name)) resolverPaths[name] = path;
        }
        foreach (var path in dlls) AddPath(path);
        foreach (var dir in dlls.Select(Path.GetDirectoryName).Where(d => !string.IsNullOrEmpty(d)).Distinct())
            foreach (var path in SafeFiles(dir!, "*.dll")) AddPath(path);
        foreach (var dir in dependencyDirectories ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(dir)) foreach (var path in SafeFiles(dir, "*.dll")) AddPath(path);
        foreach (var path in SafeFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")) AddPath(path);

        var summaries = LoadXmlSummaries(xmlDirectories, dlls, log);
        Directory.CreateDirectory(outDir);
        var index = new List<(string Full, string Kind, string File)>();

        using (var context = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths.Values)))
        {
            foreach (var dllPath in dlls)
            {
                Assembly assembly;
                try { assembly = context.LoadFromAssemblyPath(dllPath); }
                catch (Exception ex) { log?.Invoke($"skip assembly {Path.GetFileName(dllPath)}: {ex.Message}"); continue; }

                Type[] types;
                try { types = assembly.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                catch (Exception ex) { log?.Invoke($"skip types {Path.GetFileName(dllPath)}: {ex.Message}"); continue; }

                foreach (var type in types)
                {
                    if (type?.Namespace is null) continue;
                    if (!nsFilters.Any(f => type.Namespace == f || type.Namespace.StartsWith(f + ".", StringComparison.Ordinal))) continue;
                    try
                    {
                        var (file, kind) = EmitType(type, outDir, summaries);
                        index.Add((type.FullName ?? type.Name, kind, file));
                    }
                    catch (Exception ex) { log?.Invoke($"skip type {type.FullName}: {ex.Message}"); }
                }
            }
        }

        WriteIndex(outDir, index, nsFilters);
        return index.Count;
    }

    private static IEnumerable<string> SafeFiles(string directory, string pattern)
    {
        try { return Directory.GetFiles(directory, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static (string File, string Kind) EmitType(Type t, string outDir, IReadOnlyDictionary<string, string> summaries)
    {
        var kind = t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsInterface ? "interface" : "class";
        var dotted = Dotted(t.FullName ?? t.Name);
        var fileName = dotted + ".md";
        var sb = new StringBuilder();

        sb.Append("# ").Append(dotted).Append("  *(").Append(kind).Append(")*").AppendLine().AppendLine();
        if (summaries.TryGetValue("T:" + dotted, out var typeSummary))
            sb.AppendLine(typeSummary).AppendLine();

        if (t.BaseType?.FullName != null && t.BaseType.FullName != "System.Object")
            sb.Append("**Inherits:** `").Append(Friendly(t.BaseType)).Append("`  ").AppendLine();
        var interfaces = SafeInterfaces(t);
        if (interfaces.Length > 0)
            sb.Append("**Implements:** ").AppendLine(string.Join(", ", interfaces.Select(i => "`" + Friendly(i) + "`")));
        sb.AppendLine();

        if (t.IsEnum)
        {
            sb.AppendLine("## Values").AppendLine();
            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                object? value = null;
                try { value = field.GetRawConstantValue(); } catch { /* keep the name */ }
                sb.Append("- `").Append(field.Name).Append('`');
                if (value != null) sb.Append(" = ").Append(value);
                sb.AppendLine();
            }
            sb.AppendLine();
            return WriteFile(outDir, fileName, sb, kind);
        }

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var constructors = t.GetConstructors(Flags);
        if (constructors.Length > 0)
        {
            sb.AppendLine("## Constructors").AppendLine();
            foreach (var constructor in constructors)
                sb.Append("- `").Append(t.Name).Append('(').Append(Params(constructor.GetParameters())).Append(")`").AppendLine();
            sb.AppendLine();
        }

        var properties = t.GetProperties(Flags);
        if (properties.Length > 0)
        {
            sb.AppendLine("## Properties").AppendLine();
            foreach (var property in properties.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var accessors = (property.GetMethod is { IsPublic: true } ? "get; " : "") +
                                (property.SetMethod is { IsPublic: true } ? "set; " : "");
                sb.Append("- `").Append(Friendly(property.PropertyType)).Append(' ').Append(property.Name)
                  .Append(" { ").Append(accessors.Trim()).Append(" }`");
                AppendSummary(sb, summaries, "P:" + dotted + "." + property.Name);
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        var methods = t.GetMethods(Flags)
            .Where(m => !m.IsSpecialName) // hide get_/set_/add_/remove_/op_
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (methods.Length > 0)
        {
            sb.AppendLine("## Methods").AppendLine();
            foreach (var method in methods)
            {
                sb.Append("- `").Append(Friendly(method.ReturnType)).Append(' ').Append(method.Name).Append(Generics(method))
                  .Append('(').Append(Params(method.GetParameters())).Append(")`");
                AppendMethodSummary(sb, summaries, dotted, method.Name);
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        var fields = t.GetFields(Flags).Where(f => !f.IsSpecialName).ToArray();
        if (fields.Length > 0)
        {
            sb.AppendLine("## Fields").AppendLine();
            foreach (var field in fields.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("- `").Append(field.IsLiteral ? "const " : field.IsStatic ? "static " : "").Append(Friendly(field.FieldType))
                  .Append(' ').Append(field.Name).Append('`');
                AppendSummary(sb, summaries, "F:" + dotted + "." + field.Name);
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        return WriteFile(outDir, fileName, sb, kind);
    }

    private static (string, string) WriteFile(string outDir, string fileName, StringBuilder sb, string kind)
    {
        File.WriteAllText(Path.Combine(outDir, fileName), sb.ToString());
        return (fileName, kind);
    }

    private static string Params(ParameterInfo[] parameters) => string.Join(", ", parameters.Select(FormatParam));

    private static string FormatParam(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var modifier = "";
        if (type.IsByRef)
        {
            type = type.GetElementType()!;
            modifier = parameter.IsOut ? "out " : "ref ";
        }
        return modifier + Friendly(type) + " " + parameter.Name;
    }

    private static string Generics(MethodInfo method) =>
        method.IsGenericMethodDefinition ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">" : "";

    private static string Friendly(Type t)
    {
        try
        {
            if (t.IsByRef) return Friendly(t.GetElementType()!);
            if (t.IsArray) return Friendly(t.GetElementType()!) + "[]";
            if (t.IsGenericType)
            {
                var name = t.Name;
                var tick = name.IndexOf('`');
                if (tick >= 0) name = name.Substring(0, tick);
                return name + "<" + string.Join(", ", t.GetGenericArguments().Select(Friendly)) + ">";
            }
            return t.Name;
        }
        catch { return "?"; }
    }

    private static Type[] SafeInterfaces(Type t)
    {
        try { return t.GetInterfaces().Where(i => i.IsPublic || i.IsNestedPublic).ToArray(); }
        catch { return Array.Empty<Type>(); }
    }

    private static void AppendSummary(StringBuilder sb, IReadOnlyDictionary<string, string> summaries, string id)
    {
        if (summaries.TryGetValue(id, out var summary) && summary.Length > 0) sb.Append(" — ").Append(summary);
    }

    private static void AppendMethodSummary(StringBuilder sb, IReadOnlyDictionary<string, string> summaries, string dottedType, string method)
    {
        var prefix = "M:" + dottedType + "." + method;
        foreach (var entry in summaries)
        {
            if (entry.Key == prefix || entry.Key.StartsWith(prefix + "(", StringComparison.Ordinal))
            {
                if (entry.Value.Length > 0) sb.Append(" — ").Append(entry.Value);
                return;
            }
        }
    }

    private static void WriteIndex(string outDir, List<(string Full, string Kind, string File)> index, List<string> nsFilters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Tekla Open API reference (generated)").AppendLine();
        sb.Append("Namespaces: ").AppendLine(string.Join(", ", nsFilters.Select(n => "`" + n + "`")));
        sb.AppendLine().AppendLine("Generated by TeklaMcp.Scripting.ApiReferenceGenerator from the local Tekla install. " +
                                   "Do not edit by hand; do not commit or publish (Trimble docs).").AppendLine();
        sb.AppendLine("| Type | Kind | File |").AppendLine("|---|---|---|");
        foreach (var row in index.OrderBy(r => r.Full, StringComparer.OrdinalIgnoreCase))
            sb.Append("| `").Append(row.Full).Append("` | ").Append(row.Kind).Append(" | [")
              .Append(row.File).Append("](").Append(row.File).Append(") |").AppendLine();
        File.WriteAllText(Path.Combine(outDir, "INDEX.md"), sb.ToString());
    }

    private static Dictionary<string, string> LoadXmlSummaries(IEnumerable<string>? xmlDirectories, List<string> dlls, Action<string>? log)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<string>();
        var dllNames = new HashSet<string>(dlls.Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
        foreach (var dir in xmlDirectories ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                files.AddRange(SafeFiles(dir, "*.xml").Where(f => dllNames.Contains(Path.GetFileNameWithoutExtension(f))));
        foreach (var dll in dlls)
        {
            var xml = Path.ChangeExtension(dll, ".xml");
            if (File.Exists(xml)) files.Add(xml);
        }

        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var doc = XDocument.Load(file);
                foreach (var member in doc.Descendants("member"))
                {
                    var name = member.Attribute("name")?.Value;
                    if (string.IsNullOrEmpty(name)) continue;
                    var summary = member.Element("summary")?.Value;
                    if (summary is null) continue;
                    var text = string.Join(" ", summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    if (text.Length > 0 && !result.ContainsKey(name!)) result[name!] = text;
                }
            }
            catch (Exception ex) { log?.Invoke($"skip xml {Path.GetFileName(file)}: {ex.Message}"); }
        }
        return result;
    }

    private static string Dotted(string fullName) => fullName.Replace('+', '.');
}
