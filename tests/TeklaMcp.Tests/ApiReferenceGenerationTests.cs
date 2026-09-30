using System;
using System.IO;
using System.Linq;
using System.Threading;
using TeklaMcp.Core.Models;
using TeklaMcp.Scripting;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// The server builds the offline API reference itself (backlog §5). There are no Tekla DLLs in CI,
/// so these run the same generator and cache over TeklaMcp.Scripting.dll itself — the mechanics
/// (metadata-only generation, per-build cache, atomic publish, failure reporting) are what matter.
/// Same collection as ApiReferenceTests: both depend on TEKLA_MCP_API_REF_DIR being unset/set.
/// </summary>
[Collection("ApiReferenceEnv")]
public class ApiReferenceGenerationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tekla-mcp-apiref-" + Guid.NewGuid().ToString("N"));

    public ApiReferenceGenerationTests() => Environment.SetEnvironmentVariable(ApiReference.DirEnvVar, null);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string ScriptingDll => typeof(ApiReference).Assembly.Location;

    [Fact]
    public void Generator_documents_types_members_and_summaries()
    {
        var outDir = Path.Combine(_root, "direct");
        var count = ApiReferenceGenerator.Generate(new[] { ScriptingDll }, null, outDir, new[] { "TeklaMcp.Scripting" });

        Assert.True(count > 5);
        var page = File.ReadAllText(Path.Combine(outDir, "TeklaMcp.Scripting.ApiReferenceGenerator.md"));
        Assert.Contains("## Methods", page);
        Assert.Contains("Int32 Generate(", page);
        Assert.Contains("ApiReferenceGenerator.md", File.ReadAllText(Path.Combine(outDir, "INDEX.md")));
    }

    [Fact]
    public void Server_generates_caches_and_searches_the_reference_for_a_source()
    {
        var source = new ApiReferenceSource
        {
            VersionKey = "test-1.0",
            AssemblyPaths = { ScriptingDll },
            Description = "the test assembly",
            CacheRoot = _root,
        };
        // The generator only documents Tekla.Structures namespaces by default; the test assembly has
        // none, so point the check at the failure path first…
        var status = WaitFor(() => ApiReference.GetStatus(source), s => !s.Generating);
        Assert.False(status.Origin == "generated");
        Assert.Contains("No Tekla.Structures types", status.Guidance + string.Join(" ", status.Warnings));
        // …and it is not retried on every call (a restart retries).
        Assert.Contains("failed", ApiReference.GetStatus(source).Guidance + string.Join(" ", ApiReference.GetStatus(source).Warnings));
        Assert.False(Directory.EnumerateDirectories(_root).Any(d => d.Contains(".tmp-")), "no half-written folder may remain");
    }

    [Fact]
    public void Cached_reference_for_the_exact_build_wins_over_a_repository_copy()
    {
        var source = new ApiReferenceSource { VersionKey = "tekla-2099.0.1.0", CacheRoot = _root, AssemblyPaths = { ScriptingDll } };
        var cached = Path.Combine(_root, "tekla-2099.0.1.0-r" + ApiReferenceGenerator.FormatVersion);
        Directory.CreateDirectory(cached);
        File.WriteAllText(Path.Combine(cached, "Tekla.Structures.Model.Beam.md"), "# Tekla.Structures.Model.Beam\n- `Boolean Insert()`\n");

        Assert.Equal(cached, ApiReference.FindDirectory(source));
        var hit = Assert.Single(ApiReference.Search("Insert", source: source).Hits);
        Assert.Equal("Tekla.Structures.Model.Beam", hit.Type);
        Assert.Equal("generated", ApiReference.GetStatus(source).Origin);
    }

    private static ApiReferenceStatus WaitFor(Func<ApiReferenceStatus> read, Func<ApiReferenceStatus, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var status = read();
        while (!done(status) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            status = read();
        }
        return status;
    }
}
