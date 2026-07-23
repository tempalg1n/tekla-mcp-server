using System;
using System.IO;
using TeklaMcp.Scripting;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Exercises the reference search against a tiny synthetic reference folder (the real one is
/// git-ignored Trimble content). The folder is pointed at via TEKLA_MCP_API_REF_DIR, so these
/// tests must not run in parallel with anything else reading that env var.
/// </summary>
[Collection("ApiReferenceEnv")]
public class ApiReferenceTests : IDisposable
{
    private readonly string _dir;

    public ApiReferenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tekla-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Tekla.Structures.Model.Beam.md"),
            "# Tekla.Structures.Model.Beam  *(class)*\n\nA beam.\n\n## Methods\n\n" +
            "- `Boolean Insert()` — Inserts the beam into the model.\n" +
            "- `Double GetReportProperty(String name)` — Reads a report property.\n");
        File.WriteAllText(Path.Combine(_dir, "Tekla.Structures.Model.ContourPlate.md"),
            "# Tekla.Structures.Model.ContourPlate  *(class)*\n\n## Methods\n\n" +
            "- `void AddContourPoint(ContourPoint point)` — Adds a contour point.\n");
        File.WriteAllText(Path.Combine(_dir, "Tekla.Structures.Model.Part.md"),
            "# Tekla.Structures.Model.Part  *(class)*\n");
        File.WriteAllText(Path.Combine(_dir, "Tekla.Structures.Drawing.Part.md"),
            "# Tekla.Structures.Drawing.Part  *(class)*\n");
        File.WriteAllText(Path.Combine(_dir, "INDEX.md"), "# index\n");
        Environment.SetEnvironmentVariable(ApiReference.DirEnvVar, _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ApiReference.DirEnvVar, null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Finds_type_by_name()
    {
        var result = ApiReference.Search("Beam");
        Assert.True(result.ReferenceAvailable);
        Assert.Contains(result.Hits, h => h.Type == "Tekla.Structures.Model.Beam");
    }

    [Fact]
    public void Finds_member_lines_across_types()
    {
        var result = ApiReference.Search("AddContourPoint");
        var hit = Assert.Single(result.Hits);
        Assert.Equal("Tekla.Structures.Model.ContourPlate", hit.Type);
        Assert.Contains(hit.MatchingMembers, l => l.Contains("AddContourPoint"));
    }

    [Fact]
    public void Type_doc_resolves_short_names_and_suggests_on_miss()
    {
        var doc = ApiReference.GetTypeDoc("Beam");
        Assert.True(doc.Found);
        Assert.Equal("Tekla.Structures.Model.Beam", doc.TypeName);
        Assert.Contains("GetReportProperty", doc.Markdown);

        var miss = ApiReference.GetTypeDoc("Plate");
        Assert.False(miss.Found);
        Assert.Contains("Tekla.Structures.Model.ContourPlate", miss.Suggestions);
    }

    [Fact]
    public void Empty_query_returns_guidance_not_error()
    {
        var result = ApiReference.Search("");
        Assert.True(result.ReferenceAvailable);
        Assert.Empty(result.Hits);
        Assert.NotNull(result.Guidance);
    }

    [Fact]
    public void Status_reports_resolved_reference_directory()
    {
        var status = ApiReference.GetStatus();
        Assert.True(status.Available);
        Assert.Equal(_dir, status.Directory);
        Assert.True(status.TypeCount >= 4);
        Assert.Contains("Model", status.Modules);
        Assert.Contains("Drawing", status.Modules);
        Assert.Empty(status.Warnings);
    }

    [Fact]
    public void Ambiguous_short_name_requires_fully_qualified_type()
    {
        var result = ApiReference.GetTypeDoc("Part");
        Assert.False(result.Found);
        Assert.Contains("Tekla.Structures.Model.Part", result.Suggestions);
        Assert.Contains("Tekla.Structures.Drawing.Part", result.Suggestions);
        Assert.Contains("ambiguous", result.Guidance!, StringComparison.OrdinalIgnoreCase);
    }
}
