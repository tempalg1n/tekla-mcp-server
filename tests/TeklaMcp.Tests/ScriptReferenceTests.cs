using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using TeklaMcp.Scripting;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Issue #15: on Tekla 2025 the script escape hatch could not compile ANY script, because the
/// Tekla bin holds a native DLL (Tekla.Structures.Native.DbvDatabase.dll) that matches the
/// Tekla.Structures*.dll glob. MetadataReference.CreateFromFile accepts such a file without
/// complaint; the failure only surfaces at compile time as CS0009, so the old try/catch around
/// CreateFromFile never fired. BuildReferences must drop anything that is not a managed assembly.
/// </summary>
public class ScriptReferenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tekla-mcp-script-refs-" + Guid.NewGuid().ToString("N"));

    public ScriptReferenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Crafted_native_image_is_a_valid_pe_without_metadata()
    {
        // Guards the fixture itself: the file must parse as a PE image so the test below
        // exercises the "no managed metadata" branch, not just the "not a PE file" one.
        var native = WriteNativeDll("Tekla.Structures.Native.DbvDatabase.dll");
        using var stream = File.OpenRead(native);
        using var pe = new PEReader(stream);

        Assert.Equal((ushort)0x014C, (ushort)pe.PEHeaders.CoffHeader.Machine);
        Assert.False(pe.HasMetadata);
    }

    [Fact]
    public void Only_managed_assemblies_are_referenceable()
    {
        Assert.True(ScriptEngine.IsReferenceableAssembly(typeof(ScriptEngine).Assembly.Location));
        Assert.False(ScriptEngine.IsReferenceableAssembly(WriteNativeDll("native.dll")));
        Assert.False(ScriptEngine.IsReferenceableAssembly(WriteGarbage("garbage.dll")));
        Assert.False(ScriptEngine.IsReferenceableAssembly(Path.Combine(_dir, "missing.dll")));
    }

    [Fact]
    public void Real_native_system_dll_is_rejected_when_present()
    {
        // Windows-only extra evidence with a genuine native image; skipped silently elsewhere.
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        if (!OperatingSystem.IsWindows() || !File.Exists(kernel32)) return;

        Assert.False(ScriptEngine.IsReferenceableAssembly(kernel32));
    }

    [Fact]
    public void Build_references_skips_native_and_garbage_files()
    {
        var managed = typeof(ScriptReferenceTests).Assembly.Location;
        var native = WriteNativeDll("Tekla.Structures.Native.DbvDatabase.dll");
        var garbage = WriteGarbage("Tekla.Structures.Broken.dll");

        var refs = ScriptEngine.BuildReferences(teklaDllPaths: new[] { native, garbage, managed });
        var paths = refs.OfType<PortableExecutableReference>().Select(r => r.FilePath).ToList();

        Assert.Contains(managed, paths);
        Assert.DoesNotContain(native, paths);
        Assert.DoesNotContain(garbage, paths);
    }

    [Fact]
    public void Script_compiles_when_the_reference_folder_contains_a_native_dll()
    {
        var refs = ScriptEngine.BuildReferences(teklaDllPaths: new[]
        {
            WriteNativeDll("Tekla.Structures.Native.DbvDatabase.dll"),
            typeof(ScriptReferenceTests).Assembly.Location,
        });

        var errors = ScriptEngine.Compile(CreateScript("1 + 1", refs));

        Assert.Empty(errors);
    }

    [Fact]
    public void Roslyn_accepts_a_native_reference_and_fails_later_with_CS0009()
    {
        // Characterization of the behaviour that made the filter necessary: the reference is
        // created without an exception, and the compile reports CS0009. If a future Roslyn
        // validates eagerly this test fails — the filter stays correct either way.
        var refs = ScriptEngine.BuildReferences().ToList();
        refs.Add(MetadataReference.CreateFromFile(WriteNativeDll("native.dll")));

        var errors = ScriptEngine.Compile(CreateScript("1 + 1", refs));

        Assert.Contains(errors, error => error.Contains("CS0009"));
    }

    private static Script<object> CreateScript(string code, System.Collections.Generic.IEnumerable<MetadataReference> refs) =>
        CSharpScript.Create<object>(
            code,
            ScriptOptions.Default.WithReferences(refs),
            typeof(ScriptGlobals));

    private string WriteGarbage(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes("this is not a portable executable"));
        return path;
    }

    /// <summary>
    /// Minimal PE32 DLL with no sections and no CLI header — structurally what a native
    /// C/C++ DLL looks like to a metadata reader.
    /// </summary>
    private string WriteNativeDll(string name)
    {
        const int peOffset = 0x40;
        const int optionalHeaderSize = 0xE0;
        var bytes = new byte[peOffset + 4 + 20 + optionalHeaderSize];

        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(peOffset).CopyTo(bytes, 0x3C);

        bytes[peOffset] = (byte)'P';
        bytes[peOffset + 1] = (byte)'E';

        var coff = peOffset + 4;
        BitConverter.GetBytes((ushort)0x014C).CopyTo(bytes, coff);                  // Machine: i386
        BitConverter.GetBytes((ushort)0).CopyTo(bytes, coff + 2);                   // NumberOfSections
        BitConverter.GetBytes((ushort)optionalHeaderSize).CopyTo(bytes, coff + 16); // SizeOfOptionalHeader
        BitConverter.GetBytes((ushort)0x2102).CopyTo(bytes, coff + 18);             // EXECUTABLE | 32BIT | DLL

        var optional = coff + 20;
        BitConverter.GetBytes((ushort)0x010B).CopyTo(bytes, optional);              // Magic: PE32
        BitConverter.GetBytes(0x1000).CopyTo(bytes, optional + 32);                 // SectionAlignment
        BitConverter.GetBytes(0x200).CopyTo(bytes, optional + 36);                  // FileAlignment
        BitConverter.GetBytes(16).CopyTo(bytes, optional + 92);                     // NumberOfRvaAndSizes
        // All 16 data directories stay zero — in particular the CLI header (index 14).

        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
