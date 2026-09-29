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
}
