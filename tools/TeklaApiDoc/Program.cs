using TeklaMcp.Scripting;

// -----------------------------------------------------------------------------
// Tekla Open API reference generator — CLI over TeklaMcp.Scripting.ApiReferenceGenerator.
//
// The server generates the same reference by itself on first use (cached per Tekla build under
// %LOCALAPPDATA%\TeklaMcp\api-reference); this CLI is for a repository copy (reference/tekla-api)
// or for machines where the server should read a prepared folder (TEKLA_MCP_API_REF_DIR).
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
var xmlDirs = new List<string>();
var nsFilters = new List<string>();
var outDir = "reference/tekla-api";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dll": dlls.Add(args[++i]); break;
        case "--dll-dir": dllDirs.Add(args[++i]); break;
        case "--xml-dir": xmlDirs.Add(args[++i]); break;
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

foreach (var dir in dllDirs)
{
    if (!Directory.Exists(dir)) { Console.Error.WriteLine($"--dll-dir not found: {dir}"); return 2; }
    dlls.AddRange(Directory.GetFiles(dir, "*.dll"));
}
if (dlls.Count == 0) { Console.Error.WriteLine("No DLLs given. Use --dll or --dll-dir."); PrintUsage(); return 2; }

var count = ApiReferenceGenerator.Generate(
    dlls, xmlDirs, outDir, nsFilters.Count == 0 ? null : nsFilters, message => Console.Error.WriteLine(message));
Console.Error.WriteLine($"Documented {count} types -> {outDir}");
return 0;

static void PrintUsage()
{
    Console.Error.WriteLine(@"TeklaApiDoc — generate Markdown API reference from assemblies (metadata-only).

  --dll <path>         add one assembly (repeatable)
  --dll-dir <dir>      add all *.dll in a directory (repeatable)
  --xml-dir <dir>      directory with XML doc files (optional, repeatable; *.xml next to DLLs are read too)
  --namespace <ns>     only document this namespace prefix (repeatable; default: Tekla.Structures)
  --out <dir>          output directory (default: reference/tekla-api)

Example:
  dotnet run --project tools/TeklaApiDoc -- \
    --dll-dir ~/.nuget/packages/tekla.structures.model/2023.0.1/lib/net40 \
    --dll-dir ~/.nuget/packages/tekla.structures/2023.0.1/lib/net40 \
    --namespace Tekla.Structures --out reference/tekla-api");
}
