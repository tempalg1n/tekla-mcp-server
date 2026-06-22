using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

// -----------------------------------------------------------------------------
// Tekla Open API reference generator.
//
// Usage:
//   dotnet run --project tools/TeklaApiDoc -- \
//       --dll-dir <dir-with-tekla-dlls> [--dll <one.dll> ...] \
//       [--xml-dir <dir-with-xml-docs>] \
//       [--namespace Tekla.Structures] [--namespace ...] \
//       --out reference/tekla-api
//
// Reads metadata only (never executes Tekla code) via MetadataLoadContext, so it can
// read net48 Tekla assemblies from this net8 tool on any OS.
// -----------------------------------------------------------------------------

var dlls = new List<string>();
var dllDirs = new List<string>();
var nsFilters = new List<string>();
string? xmlDir = null;
var outDir = "reference/tekla-api";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dll": dlls.Add(args[++i]); break;
        case "--dll-dir": dllDirs.Add(args[++i]); break;
        case "--xml-dir": xmlDir = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--namespace": nsFilters.Add(args[++i]); break;
        case "-h":
        case "--help": PrintUsage(); return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            return 2;
    }
}

if (nsFilters.Count == 0) nsFilters.Add("Tekla.Structures");

foreach (var dir in dllDirs)
{
    if (!Directory.Exists(dir)) { Console.Error.WriteLine($"--dll-dir not found: {dir}"); return 2; }
    dlls.AddRange(Directory.GetFiles(dir, "*.dll"));
}

dlls = dlls.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
if (dlls.Count == 0) { Console.Error.WriteLine("No DLLs given. Use --dll or --dll-dir."); PrintUsage(); return 2; }

// Resolver: target DLLs + their sibling DLLs (dependencies) + the net8 runtime DLLs
// (provides the core assembly so net48 metadata resolves). Dedup by simple name.
var resolverPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
void AddPath(string p)
{
    var name = Path.GetFileNameWithoutExtension(p);
    if (!resolverPaths.ContainsKey(name)) resolverPaths[name] = p;
}
foreach (var p in dlls) AddPath(p);
foreach (var dir in dlls.Select(Path.GetDirectoryName).Where(d => d != null).Distinct())
    foreach (var p in Directory.GetFiles(dir!, "*.dll")) AddPath(p);
foreach (var p in Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")) AddPath(p);

var summaries = LoadXmlSummaries(xmlDir, dlls);

var resolver = new PathAssemblyResolver(resolverPaths.Values);
using var mlc = new MetadataLoadContext(resolver);

Directory.CreateDirectory(outDir);
var index = new List<(string Full, string Kind, string File)>();
var typeCount = 0;

foreach (var dllPath in dlls)
{
    Assembly asm;
    try { asm = mlc.LoadFromAssemblyPath(dllPath); }
    catch (Exception ex) { Console.Error.WriteLine($"skip assembly {Path.GetFileName(dllPath)}: {ex.Message}"); continue; }

    Type[] types;
    try { types = asm.GetExportedTypes(); }
    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
    catch (Exception ex) { Console.Error.WriteLine($"skip types {Path.GetFileName(dllPath)}: {ex.Message}"); continue; }

    foreach (var t in types)
    {
        if (t?.Namespace is null) continue;
        if (!nsFilters.Any(f => t.Namespace == f || t.Namespace.StartsWith(f + "."))) continue;
        try
        {
            var (file, kind) = EmitType(t, outDir, summaries);
            index.Add((t.FullName ?? t.Name, kind, file));
            typeCount++;
        }
        catch (Exception ex) { Console.Error.WriteLine($"skip type {t.FullName}: {ex.Message}"); }
    }
}

WriteIndex(outDir, index, nsFilters);
Console.Error.WriteLine($"Documented {typeCount} types -> {outDir}");
return 0;

// -----------------------------------------------------------------------------

static (string file, string kind) EmitType(Type t, string outDir, IReadOnlyDictionary<string, string> summaries)
{
    var kind = t.IsEnum ? "enum"
        : t.IsValueType ? "struct"
        : t.IsInterface ? "interface"
        : "class";

    var dotted = Dotted(t.FullName ?? t.Name);
    var fileName = dotted + ".md";
    var sb = new StringBuilder();

    sb.Append("# ").Append(dotted).Append("  *(").Append(kind).Append(")*").AppendLine().AppendLine();
    if (summaries.TryGetValue("T:" + dotted, out var typeSummary))
        sb.AppendLine(typeSummary).AppendLine();

    if (t.BaseType is not null && t.BaseType.FullName is not null && t.BaseType.FullName != "System.Object")
        sb.Append("**Inherits:** `").Append(Friendly(t.BaseType)).Append("`  ").AppendLine();
    var ifaces = SafeInterfaces(t);
    if (ifaces.Length > 0)
        sb.Append("**Implements:** ").AppendLine(string.Join(", ", ifaces.Select(i => "`" + Friendly(i) + "`")));
    sb.AppendLine();

    if (t.IsEnum)
    {
        sb.AppendLine("## Values").AppendLine();
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object? val = null;
            try { val = f.GetRawConstantValue(); } catch { }
            sb.Append("- `").Append(f.Name).Append('`');
            if (val is not null) sb.Append(" = ").Append(val);
            sb.AppendLine();
        }
        sb.AppendLine();
        return WriteFile(outDir, fileName, sb, kind);
    }

    const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    var ctors = t.GetConstructors(Flags);
    if (ctors.Length > 0)
    {
        sb.AppendLine("## Constructors").AppendLine();
        foreach (var c in ctors)
            sb.Append("- `").Append(t.Name).Append('(').Append(Params(c.GetParameters())).Append(")`").AppendLine();
        sb.AppendLine();
    }

    var props = t.GetProperties(Flags);
    if (props.Length > 0)
    {
        sb.AppendLine("## Properties").AppendLine();
        foreach (var p in props.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var acc = (p.GetMethod is { IsPublic: true } ? "get; " : "") + (p.SetMethod is { IsPublic: true } ? "set; " : "");
            sb.Append("- `").Append(Friendly(p.PropertyType)).Append(' ').Append(p.Name)
              .Append(" { ").Append(acc.Trim()).Append(" }`");
            AppendSummary(sb, summaries, "P:" + dotted + "." + p.Name);
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
        foreach (var m in methods)
        {
            sb.Append("- `").Append(Friendly(m.ReturnType)).Append(' ').Append(m.Name).Append(Generics(m))
              .Append('(').Append(Params(m.GetParameters())).Append(")`");
            AppendMethodSummary(sb, summaries, dotted, m.Name);
            sb.AppendLine();
        }
        sb.AppendLine();
    }

    var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(f => !f.IsSpecialName).ToArray();
    if (fields.Length > 0)
    {
        sb.AppendLine("## Fields").AppendLine();
        foreach (var f in fields.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("- `").Append(f.IsLiteral ? "const " : f.IsStatic ? "static " : "").Append(Friendly(f.FieldType))
              .Append(' ').Append(f.Name).Append('`');
            AppendSummary(sb, summaries, "F:" + dotted + "." + f.Name);
            sb.AppendLine();
        }
        sb.AppendLine();
    }

    return WriteFile(outDir, fileName, sb, kind);
}

static (string, string) WriteFile(string outDir, string fileName, StringBuilder sb, string kind)
{
    File.WriteAllText(Path.Combine(outDir, fileName), sb.ToString());
    return (fileName, kind);
}

static string Params(ParameterInfo[] ps) => string.Join(", ", ps.Select(FormatParam));

static string FormatParam(ParameterInfo p)
{
    var pt = p.ParameterType;
    var mod = "";
    if (pt.IsByRef) { pt = pt.GetElementType()!; mod = p.IsOut ? "out " : "ref "; }
    return mod + Friendly(pt) + " " + p.Name;
}

static string Generics(MethodInfo m) =>
    m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";

static string Friendly(Type t)
{
    try
    {
        if (t.IsByRef) return Friendly(t.GetElementType()!);
        if (t.IsArray) return Friendly(t.GetElementType()!) + "[]";
        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            return name + "<" + string.Join(", ", t.GetGenericArguments().Select(Friendly)) + ">";
        }
        return t.Name;
    }
    catch { return "?"; }
}

static Type[] SafeInterfaces(Type t)
{
    try { return t.GetInterfaces().Where(i => i.IsPublic || i.IsNestedPublic).ToArray(); }
    catch { return Array.Empty<Type>(); }
}

static void AppendSummary(StringBuilder sb, IReadOnlyDictionary<string, string> summaries, string id)
{
    if (summaries.TryGetValue(id, out var s) && s.Length > 0) sb.Append(" — ").Append(s);
}

static void AppendMethodSummary(StringBuilder sb, IReadOnlyDictionary<string, string> summaries, string dottedType, string method)
{
    var prefix = "M:" + dottedType + "." + method;
    foreach (var kv in summaries)
    {
        if (kv.Key == prefix || kv.Key.StartsWith(prefix + "(", StringComparison.Ordinal))
        {
            if (kv.Value.Length > 0) sb.Append(" — ").Append(kv.Value);
            return;
        }
    }
}

static void WriteIndex(string outDir, List<(string Full, string Kind, string File)> index, List<string> nsFilters)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Tekla Open API reference (generated)").AppendLine();
    sb.Append("Namespaces: ").AppendLine(string.Join(", ", nsFilters.Select(n => "`" + n + "`")));
    sb.AppendLine().AppendLine("Generated by `tools/TeklaApiDoc`. Do not edit by hand; do not commit (Trimble docs).").AppendLine();
    sb.AppendLine("| Type | Kind | File |").AppendLine("|---|---|---|");
    foreach (var row in index.OrderBy(r => r.Full, StringComparer.OrdinalIgnoreCase))
        sb.Append("| `").Append(row.Full).Append("` | ").Append(row.Kind).Append(" | [")
          .Append(row.File).Append("](").Append(row.File).Append(") |").AppendLine();
    File.WriteAllText(Path.Combine(outDir, "INDEX.md"), sb.ToString());
}

static Dictionary<string, string> LoadXmlSummaries(string? xmlDir, List<string> dlls)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var files = new List<string>();
    if (xmlDir is not null && Directory.Exists(xmlDir)) files.AddRange(Directory.GetFiles(xmlDir, "*.xml"));
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
                if (text.Length > 0 && !result.ContainsKey(name)) result[name] = text;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"skip xml {Path.GetFileName(file)}: {ex.Message}"); }
    }
    return result;
}

static string Dotted(string fullName) => fullName.Replace('+', '.');

static void PrintUsage()
{
    Console.Error.WriteLine(@"TeklaApiDoc — generate Markdown API reference from assemblies (metadata-only).

  --dll <path>         add one assembly (repeatable)
  --dll-dir <dir>      add all *.dll in a directory (repeatable)
  --xml-dir <dir>      directory with XML doc files (optional; also reads *.xml next to DLLs)
  --namespace <ns>     only document this namespace prefix (repeatable; default: Tekla.Structures)
  --out <dir>          output directory (default: reference/tekla-api)

Example:
  dotnet run --project tools/TeklaApiDoc -- \
    --dll-dir ~/.nuget/packages/tekla.structures.model/2023.0.0/lib/net48 \
    --dll-dir ~/.nuget/packages/tekla.structures/2023.0.0/lib/net48 \
    --namespace Tekla.Structures --out reference/tekla-api");
}
