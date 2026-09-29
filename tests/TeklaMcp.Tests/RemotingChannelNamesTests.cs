using TeklaMcp.Core;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Channel alignment rules behind TeklaRemotingChannel (DEV-005 follow-up). Pipe names are the
/// 2021–2023 format "{Assembly}-{SESSIONNAME}:{AssemblyVersion}"; a second instance of the same
/// version publishes "…-Console-{PID}:…".
/// </summary>
public class RemotingChannelNamesTests
{
    private static readonly string[] Tekla2023Pipes =
    {
        "Tekla.Macros.Akit-2232-1",
        "Tekla.Structures.Model-Console:2023.0.0.0",
        "Tekla.Structures-Console:2023.0.0.0",
        "Tekla.Structures.Catalogs-Console:2023.0.0.0",
        "Tekla.Structures.Drawing-Console:2023.0.0.0",
    };

    [Theory]
    [InlineData(2021, false)]
    [InlineData(2023, false)]
    [InlineData(2024, true)]
    [InlineData(2026, true)]
    [InlineData(null, false)]
    public void Only_2024_and_later_name_their_own_channels(int? major, bool expected)
    {
        Assert.Equal(expected, RemotingChannelNames.NamesItsOwnChannels(major));
    }

    [Fact]
    public void Another_versions_pipes_give_nothing_to_align_to()
    {
        // A 2021 build next to a running 2023: aligning to 2023's session used to end alignment
        // for the process (and a 2024+ build would have been poisoned the same way).
        Assert.Null(RemotingChannelNames.DeriveSessionSuffix(Tekla2023Pipes, 2021, null, out var note));
        Assert.Null(note);
    }

    [Fact]
    public void Own_versions_pipes_give_their_session()
    {
        Assert.Equal("Console", RemotingChannelNames.DeriveSessionSuffix(Tekla2023Pipes, 2023, null, out _));
    }

    [Fact]
    public void Unknown_build_version_accepts_any_version()
    {
        Assert.Equal("Console", RemotingChannelNames.DeriveSessionSuffix(Tekla2023Pipes, null, null, out _));
    }

    [Fact]
    public void Second_instance_suffix_is_seen_and_the_choice_is_explained()
    {
        var pipes = new[]
        {
            "Tekla.Structures.Model-Console:2023.0.0.0",
            "Tekla.Structures.Model-Console-22296:2023.0.0.0",
        };

        var suffix = RemotingChannelNames.DeriveSessionSuffix(pipes, 2023, null, out var note);

        Assert.Equal("Console", suffix);
        Assert.NotNull(note);
        Assert.Contains("Console-22296", note);
        Assert.Contains("TEKLA_MCP_CHANNEL", note);
    }

    [Fact]
    public void Current_session_wins_among_several()
    {
        var pipes = new[]
        {
            "Tekla.Structures.Model-Console:2023.0.0.0",
            "Tekla.Structures.Model-RDP-Tcp#3:2023.0.0.0",
        };

        Assert.Equal("RDP-Tcp#3",
            RemotingChannelNames.DeriveSessionSuffix(pipes, 2023, "RDP-Tcp#3", out var note));
        Assert.Null(note);
    }

    [Fact]
    public void Unset_session_is_a_valid_empty_suffix()
    {
        Assert.Equal("", RemotingChannelNames.DeriveSessionSuffix(
            new[] { "Tekla.Structures.Model-:2021.0.0.0" }, 2021, null, out _));
    }

    [Fact]
    public void Unrelated_pipes_alone_give_nothing()
    {
        Assert.Null(RemotingChannelNames.DeriveSessionSuffix(
            new[] { "Tekla.Macros.Akit-2232-1", "Tekla.Structures.Catalogs-Console:2023.0.0.0" },
            2023, null, out _));
    }

    [Theory]
    [InlineData("Tekla.Structures.Model-Console:2023.0.0.0", "Console")]
    [InlineData("Tekla.Structures-Console-4711:2023.0.0.0", "Console-4711")]
    [InlineData("Tekla.Structures.Drawing-RDP-Tcp#47:2021.0.0.0", "RDP-Tcp#47")]
    [InlineData("Tekla.Structures.Catalogs-Console:2023.0.0.0", null)]
    [InlineData("", null)]
    public void Session_suffix_of_a_channel(string channel, string? expected)
    {
        Assert.Equal(expected, RemotingChannelNames.SessionSuffixOf(channel));
    }

    [Theory]
    [InlineData("Tekla.Structures.Model-Console:2023.0.0.0", "Tekla.Structures",
        "Tekla.Structures-Console:2023.0.0.0")]
    [InlineData("Tekla.Structures.Model-TeklaStructures-Console:2024.0.4.0", "Tekla.Structures",
        "Tekla.Structures-TeklaStructures-Console:2024.0.4.0")]
    [InlineData("Tekla.Structures.Model-TeklaStructures-Console-5120:2026.0.3.0", "Tekla.Structures.Drawing",
        "Tekla.Structures.Drawing-TeklaStructures-Console-5120:2026.0.3.0")]
    public void Channel_for_another_assembly_keeps_session_product_and_version(
        string modelChannel, string assembly, string expected)
    {
        Assert.Equal(expected,
            RemotingChannelNames.WithAssembly(modelChannel, "Tekla.Structures.Model", assembly));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Tekla.Structures-Console:2023.0.0.0")]
    [InlineData("something else")]
    public void Channel_of_another_assembly_is_not_rewritten(string? channel)
    {
        Assert.Null(RemotingChannelNames.WithAssembly(channel, "Tekla.Structures.Model", "Tekla.Structures"));
    }
}
