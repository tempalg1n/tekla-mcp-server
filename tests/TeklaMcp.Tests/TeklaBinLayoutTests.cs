using System;
using System.Collections.Generic;
using System.IO;
using TeklaMcp.Core;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// DEV-005 field report: on Tekla 2021 the Open API lives in nt\bin\plugins while nt\bin holds
/// seven unrelated Tekla.Structures.*.dll. A TEKLA_BIN_DIR (or process folder) of nt\bin must
/// resolve to the plugins folder, never be taken as-is.
/// </summary>
public class TeklaBinLayoutTests
{
    private static readonly string Install2021 = Path.Combine("C:", "TeklaStructures", "2021.0");
    private static readonly string NtBin2021 = Path.Combine(Install2021, "nt", "bin");
    private static readonly string Plugins2021 = Path.Combine(NtBin2021, "plugins");
    private static readonly string Bin2023 = Path.Combine("C:", "TeklaStructures", "2023.0", "bin");

    private static Func<string, bool> ModelDllIn(params string[] folders)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
            files.Add(Path.Combine(folder, TeklaBinLayout.ModelAssemblyFile));
        return files.Contains;
    }

    [Fact]
    public void Tekla_2021_nt_bin_resolves_to_its_plugins_folder()
    {
        Assert.Equal(Plugins2021, TeklaBinLayout.FindApiDirectory(NtBin2021, ModelDllIn(Plugins2021)));
    }

    [Fact]
    public void Folder_that_holds_the_api_is_used_as_is()
    {
        Assert.Equal(Bin2023, TeklaBinLayout.FindApiDirectory(Bin2023, ModelDllIn(Bin2023)));
        Assert.Equal(Plugins2021, TeklaBinLayout.FindApiDirectory(Plugins2021, ModelDllIn(Plugins2021)));
    }

    [Fact]
    public void Install_root_resolves_to_the_api_folder_of_either_layout()
    {
        Assert.Equal(Plugins2021, TeklaBinLayout.FindApiDirectory(Install2021, ModelDllIn(Plugins2021)));

        var install2023 = Path.GetDirectoryName(Bin2023)!;
        Assert.Equal(Bin2023, TeklaBinLayout.FindApiDirectory(install2023, ModelDllIn(Bin2023)));
    }

    [Fact]
    public void Quoted_and_padded_root_is_accepted()
    {
        Assert.Equal(Plugins2021,
            TeklaBinLayout.FindApiDirectory("  \"" + NtBin2021 + "\" ", ModelDllIn(Plugins2021)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Blank_root_finds_nothing(string? root)
    {
        Assert.Null(TeklaBinLayout.FindApiDirectory(root, _ => true));
    }

    [Fact]
    public void Folder_without_any_api_finds_nothing()
    {
        Assert.Null(TeklaBinLayout.FindApiDirectory(NtBin2021, ModelDllIn(Bin2023)));
    }

    // --- Issue #17: Tekla 2026 splits the Open API over bin and bin\Net48Runtime ----------------

    private static readonly string Bin2026 = Path.Combine("C:", "Program Files", "Tekla Structures", "2026.0", "bin");
    private static readonly string Runtime2026 = Path.Combine(Bin2026, TeklaBinLayout.Net48RuntimeFolder);

    /// <summary>The 2026 layout as listed in the field report.</summary>
    private static Func<string, bool> Files2026()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Bin2026, "Tekla.Structures.Model.dll"),
            Path.Combine(Bin2026, "Trimble.Remoting.dll"),
            Path.Combine(Bin2026, "DotNetKit.dll"),
            Path.Combine(Runtime2026, "Tekla.Structures.dll"),
            Path.Combine(Runtime2026, "Tekla.Structures.Internal.dll"),
        };
        return files.Contains;
    }

    [Fact]
    public void Tekla_2026_api_folder_is_bin_because_the_model_dll_is_there()
    {
        Assert.Equal(Bin2026, TeklaBinLayout.FindApiDirectory(Bin2026, Files2026()));
    }

    [Fact]
    public void Tekla_2026_probes_bin_then_its_Net48Runtime_subfolder()
    {
        var dirs = TeklaBinLayout.ProbeDirectories(Bin2026, dir => dir == Runtime2026);

        Assert.Equal(new[] { Bin2026, Runtime2026 }, dirs);
    }

    [Fact]
    public void Installs_without_Net48Runtime_probe_only_the_api_folder()
    {
        Assert.Equal(new[] { Bin2023 }, TeklaBinLayout.ProbeDirectories(Bin2023, _ => false));
        Assert.Equal(new[] { Plugins2021 }, TeklaBinLayout.ProbeDirectories(Plugins2021, _ => false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_api_folder_probes_nothing(string? dir)
    {
        Assert.Empty(TeklaBinLayout.ProbeDirectories(dir, _ => true));
    }

    [Fact]
    public void Tekla_2026_assemblies_are_found_in_either_folder()
    {
        var dirs = new[] { Bin2026, Runtime2026 };
        var files = Files2026();

        Assert.Equal(Path.Combine(Runtime2026, "Tekla.Structures.dll"),
            TeklaBinLayout.FindAssemblyFile("Tekla.Structures", dirs, files));
        Assert.Equal(Path.Combine(Bin2026, "Trimble.Remoting.dll"),
            TeklaBinLayout.FindAssemblyFile("Trimble.Remoting", dirs, files));
        Assert.Equal(Path.Combine(Bin2026, "DotNetKit.dll"),
            TeklaBinLayout.FindAssemblyFile("DotNetKit", dirs, files));
        Assert.Null(TeklaBinLayout.FindAssemblyFile("Polly", dirs, files));
    }

    [Fact]
    public void A_name_in_both_folders_comes_from_the_api_folder_first()
    {
        var both = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Bin2026, "Shared.dll"),
            Path.Combine(Runtime2026, "Shared.dll"),
        };

        Assert.Equal(Path.Combine(Bin2026, "Shared.dll"),
            TeklaBinLayout.FindAssemblyFile("Shared", new[] { Bin2026, Runtime2026 }, both.Contains));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"..\Tekla.Structures")]
    [InlineData("sub/Tekla.Structures")]
    [InlineData(@"C:\Windows\evil")]
    [InlineData("C:evil")]
    public void Names_that_are_not_plain_file_names_never_reach_outside_the_folders(string? name)
    {
        Assert.Null(TeklaBinLayout.FindAssemblyFile(name, new[] { Bin2026 }, _ => true));
    }
}
