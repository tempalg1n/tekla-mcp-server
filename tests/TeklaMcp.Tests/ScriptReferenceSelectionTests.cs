using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using TeklaMcp.Scripting;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// DEV-005 field report (Tekla 2021): connection and model summary worked, but
/// tekla_check_csharp could not resolve Tekla.Structures.Model, Geometry3d, Filtering or
/// TeklaStructuresInfo. The core API was bound from the GAC while the script references came
/// only from a folder holding unrelated Tekla.Structures.*.dll. These tests build small fake
/// Tekla assemblies with Roslyn so the real selection and compile path runs without Tekla.
/// </summary>
public class ScriptReferenceSelectionTests : IDisposable
{
    private const string BaseSource =
        "namespace Tekla.Structures { public static class TeklaStructuresInfo { " +
        "public static string GetCurrentProgramVersion() => \"2021.0\"; } }\n" +
        "namespace Tekla.Structures.Geometry3d { public class Point { public double X; } }\n" +
        "namespace Tekla.Structures.Filtering { public class BinaryFilterExpression { } }";

    private const string ModelSource =
        "namespace Tekla.Structures.Model { public class Model { public bool GetConnectionStatus() => true; } }\n" +
        "namespace Tekla.Structures.Model.UI { public class ModelObjectSelector { } }";

    private const string ServiceSource =
        "namespace Tekla.Structures.Service { public class ServiceHost { } }";

    private const string DrawingSource =
        "namespace Tekla.Structures.Drawing { public class DrawingHandler { } }";

    /// <summary>The shape of the DEV-005 read-only identity probe that never compiled.</summary>
    private const string IdentityProbe =
        "var model = new Model();\n" +
        "var origin = new Point();\n" +
        "var filter = new BinaryFilterExpression();\n" +
        "TeklaStructuresInfo.GetCurrentProgramVersion()";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tekla-mcp-ref-selection-" + Guid.NewGuid().ToString("N"));

    public ScriptReferenceSelectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Folder_with_only_unrelated_tekla_dlls_reproduces_the_2021_failure()
    {
        // What the old code compiled against when TEKLA_BIN_DIR pointed at 2021's nt\bin.
        var service = Emit("nt-bin", "Tekla.Structures.Service", "2021.0.0.0", ServiceSource);

        var selection = ScriptReferenceSelector.Select(null, new[] { service }, 2021);

        Assert.False(selection.HasCoreApi);
        Assert.Contains("MISSING Tekla.Structures and Tekla.Structures.Model", selection.Summary);
        Assert.Contains(Compile(IdentityProbe, selection), error => error.Contains("Model"));
        Assert.Contains("server installation problem",
            ScriptReferenceSelector.CompileFailureGuidance(selection));
    }

    [Fact]
    public void Loaded_core_is_referenced_even_when_the_folder_holds_only_unrelated_dlls()
    {
        var coreModel = Emit("gac", "Tekla.Structures.Model", "2021.0.0.0", ModelSource);
        var coreBase = Emit("gac", "Tekla.Structures", "2021.0.0.0", BaseSource);
        var service = Emit("nt-bin", "Tekla.Structures.Service", "2021.0.0.0", ServiceSource);

        var selection = ScriptReferenceSelector.Select(
            new[] { coreModel, coreBase }, new[] { service }, 2021);

        Assert.True(selection.HasCoreApi);
        Assert.Equal(new[] { coreModel, coreBase, service }, selection.Paths);
        Assert.All(selection.Report, entry => Assert.True(entry.Included));
        Assert.Equal(ScriptReferenceSelector.OriginLoaded, Entry(selection, coreModel).Origin);
        Assert.Equal(ScriptReferenceSelector.OriginDirectory, Entry(selection, service).Origin);
        Assert.Empty(Compile(IdentityProbe, selection));
        Assert.StartsWith("3 Tekla reference(s) for Tekla 2021: 2 loaded", selection.Summary);
    }

    [Fact]
    public void Other_tekla_year_is_rejected_before_compile()
    {
        var coreModel = Emit("gac", "Tekla.Structures.Model", "2021.0.0.0", ModelSource);
        var coreBase = Emit("gac", "Tekla.Structures", "2021.0.0.0", BaseSource);
        var drawing2023 = Emit("bin-2023", "Tekla.Structures.Drawing", "2023.0.0.0", DrawingSource);

        var selection = ScriptReferenceSelector.Select(
            new[] { coreModel, coreBase }, new[] { drawing2023 }, 2021);

        Assert.DoesNotContain(drawing2023, selection.Paths);
        var entry = Entry(selection, drawing2023);
        Assert.False(entry.Included);
        Assert.Equal("2023.0.0.0", entry.Version);
        Assert.Contains("Tekla 2023 assembly in a Tekla 2021 reference set", entry.Reason);
        Assert.Empty(Compile(IdentityProbe, selection));
    }

    [Fact]
    public void Loaded_assembly_wins_over_folder_copies_of_the_same_name()
    {
        var loadedModel = Emit("gac", "Tekla.Structures.Model", "2021.0.0.0", ModelSource);
        var folderModel = Emit("plugins", "Tekla.Structures.Model", "2021.0.0.0", ModelSource);
        var otherModel = Emit("stale", "Tekla.Structures.Model", "2021.0.1.0", ModelSource);

        var selection = ScriptReferenceSelector.Select(
            new[] { loadedModel }, new[] { folderModel, otherModel }, 2021);

        Assert.Equal(new[] { loadedModel }, selection.Paths);
        Assert.Contains("same assembly already referenced from loaded", Entry(selection, folderModel).Reason);
        Assert.Contains("version 2021.0.1.0 conflicts with 2021.0.0.0", Entry(selection, otherModel).Reason);
    }

    [Fact]
    public void Tekla_year_comes_from_the_model_assembly_when_the_build_year_is_unknown()
    {
        var model2023 = Emit("bin-2023", "Tekla.Structures.Model", "2023.0.0.0", ModelSource);
        var base2021 = Emit("bin-2021", "Tekla.Structures", "2021.0.0.0", BaseSource);

        var selection = ScriptReferenceSelector.Select(null, new[] { model2023, base2021 }, null);

        Assert.Equal(2023, selection.TeklaMajor);
        Assert.Equal(new[] { model2023 }, selection.Paths);
        Assert.False(selection.HasCoreApi);
    }

    [Fact]
    public void Assemblies_without_a_year_version_are_kept()
    {
        var coreModel = Emit("gac", "Tekla.Structures.Model", "2021.0.0.0", ModelSource);
        var helper = Emit("plugins", "Tekla.Structures.Helper", "1.2.0.0", ServiceSource);

        var selection = ScriptReferenceSelector.Select(new[] { coreModel }, new[] { helper }, 2021);

        Assert.Contains(helper, selection.Paths);
    }

    [Fact]
    public void Non_managed_files_are_reported_and_skipped()
    {
        var broken = Path.Combine(_root, "Tekla.Structures.Native.Broken.dll");
        File.WriteAllText(broken, "not a portable executable");

        var selection = ScriptReferenceSelector.Select(null, new[] { broken }, 2021);

        Assert.Empty(selection.Paths);
        var entry = Assert.Single(selection.Report);
        Assert.False(entry.Included);
        Assert.Equal("Tekla.Structures.Native.Broken", entry.Name);
        Assert.Equal("", entry.Version);
        Assert.Contains("not a managed assembly", entry.Reason);
    }

    [Fact]
    public void A_file_that_is_both_loaded_and_in_the_folder_is_considered_once()
    {
        var model = Emit("bin", "Tekla.Structures.Model", "2023.0.0.0", ModelSource);

        var selection = ScriptReferenceSelector.Select(new[] { model }, new[] { model }, 2023);

        var entry = Assert.Single(selection.Report);
        Assert.Equal(ScriptReferenceSelector.OriginLoaded, entry.Origin);
        Assert.True(entry.Included);
    }

    private IReadOnlyList<string> Compile(string code, ScriptReferenceSelection selection) =>
        ScriptEngine.Compile(ScriptEngine.Create(
            code, ScriptEngine.BuildReferences(teklaDllPaths: selection.Paths)));

    private static ScriptReferenceInfo Entry(ScriptReferenceSelection selection, string path) =>
        Assert.Single(selection.Report, entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Compiles a tiny stand-in for a Tekla assembly with the given identity.</summary>
    private string Emit(string folder, string assemblyName, string version, string source)
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, assemblyName + ".dll");

        var tree = CSharpSyntaxTree.ParseText(
            $"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]\n" + source);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            ScriptEngine.BuildReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emitted = compilation.Emit(path);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return path;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>
/// The mock backend compiles against TEKLA_MCP_SCRIPT_REF_DIR through the same selector and must
/// report the reference set like the live backend. Runs alone: it sets a process-wide variable.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class MockScriptReferenceReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tekla-mcp-mock-refs-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("TEKLA_MCP_SCRIPT_REF_DIR");

    public MockScriptReferenceReportTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("TEKLA_MCP_SCRIPT_REF_DIR", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TEKLA_MCP_SCRIPT_REF_DIR", _previous);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Compile_only_check_reports_every_reference_decision()
    {
        EmitInto("Tekla.Structures", "2023.0.0.0",
            "namespace Tekla.Structures { public static class TeklaStructuresInfo { } }\n" +
            "namespace Tekla.Structures.Geometry3d { public class Point { } }\n" +
            "namespace Tekla.Structures.Filtering { public class BinaryFilterExpression { } }");
        EmitInto("Tekla.Structures.Model", "2023.0.0.0",
            "namespace Tekla.Structures.Model { public class Model { } }\n" +
            "namespace Tekla.Structures.Model.UI { public class ModelObjectSelector { } }");
        EmitInto("Tekla.Structures.Drawing", "2021.0.0.0",
            "namespace Tekla.Structures.Drawing { public class DrawingHandler { } }");

        var result = new MockTeklaModelService().ExecuteScript(
            "var model = new Model();\nnew Point()", compileOnly: true);

        Assert.True(result.Compiled, string.Join("\n", result.CompileErrors));
        Assert.StartsWith("2 Tekla reference(s) for Tekla 2023", result.ReferenceSummary);
        Assert.Equal(3, result.References.Count);
        var drawing = Assert.Single(result.References, r => r.Name == "Tekla.Structures.Drawing");
        Assert.False(drawing.Included);
        Assert.Contains("Tekla 2021 assembly in a Tekla 2023 reference set", drawing.Reason);
    }

    [Fact]
    public void Normal_run_keeps_only_the_summary()
    {
        // Every default script import must exist, or even "1 + 1" fails to compile.
        EmitInto("Tekla.Structures", "2023.0.0.0",
            "namespace Tekla.Structures { public static class TeklaStructuresInfo { } }\n" +
            "namespace Tekla.Structures.Geometry3d { public class Point { } }\n" +
            "namespace Tekla.Structures.Filtering { public class BinaryFilterExpression { } }");
        EmitInto("Tekla.Structures.Model", "2023.0.0.0",
            "namespace Tekla.Structures.Model { public class Model { } }\n" +
            "namespace Tekla.Structures.Model.UI { public class ModelObjectSelector { } }");

        var result = new MockTeklaModelService().ExecuteScript("1 + 1");

        Assert.True(result.Compiled, string.Join("\n", result.CompileErrors));
        Assert.StartsWith("2 Tekla reference(s) for Tekla 2023", result.ReferenceSummary);
        Assert.Empty(result.References);
    }

    [Fact]
    public void Compile_failure_on_a_broken_reference_folder_points_at_the_installation()
    {
        EmitInto("Tekla.Structures.Service", "2021.0.0.0",
            "namespace Tekla.Structures.Service { public class ServiceHost { } }");

        var result = new MockTeklaModelService().ExecuteScript("var model = new Model();\nmodel");

        Assert.False(result.Compiled);
        Assert.NotEmpty(result.CompileErrors);
        Assert.Contains("MISSING Tekla.Structures and Tekla.Structures.Model", result.ReferenceSummary);
        Assert.Contains("server installation problem", result.Guidance);
        Assert.Single(result.References);
    }

    private void EmitInto(string assemblyName, string version, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            $"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]\n" + source);
        var emitted = CSharpCompilation.Create(
                assemblyName,
                new[] { tree },
                ScriptEngine.BuildReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .Emit(Path.Combine(_dir, assemblyName + ".dll"));
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
    }
}
