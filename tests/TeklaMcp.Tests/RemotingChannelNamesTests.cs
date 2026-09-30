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

    // --- Connection guard (backlog §1.1): may block ONLY on a complete 2021–2023 pipe listing ---

    [Fact]
    public void Guard_sees_the_exact_channel_published()
    {
        Assert.Equal(ChannelPublication.Published, RemotingChannelNames.CheckPublished(
            "Tekla.Structures.Model-Console:2023.0.0.0", Tekla2023Pipes, listingComplete: true, compiledMajor: 2023));
    }

    [Fact]
    public void Guard_matches_pipe_names_case_insensitively()
    {
        Assert.Equal(ChannelPublication.Published, RemotingChannelNames.CheckPublished(
            "tekla.structures.drawing-console:2023.0.0.0", Tekla2023Pipes, true, 2023));
    }

    [Fact]
    public void Guard_reports_a_missing_tekla_as_not_published()
    {
        // Server started first: nothing Tekla-related is published yet.
        Assert.Equal(ChannelPublication.NotPublished, RemotingChannelNames.CheckPublished(
            "Tekla.Structures.Model-Console:2021.0.0.0", new[] { "Tekla.Macros.Akit-2232-1" }, true, 2021));
    }

    [Fact]
    public void Guard_ignores_another_tekla_versions_channels()
    {
        // A 2021 build next to a running 2023: the 2021 channel is still not published.
        Assert.Equal(ChannelPublication.NotPublished, RemotingChannelNames.CheckPublished(
            "Tekla.Structures.Model-Console:2021.0.0.0", Tekla2023Pipes, true, 2021));
    }

    [Fact]
    public void Guard_sees_the_same_tekla_under_another_suffix()
    {
        // The first instance closed; the second one publishes Console-<PID>.
        var pipes = new[] { "Tekla.Structures.Model-Console-22296:2023.0.0.0" };
        Assert.Equal(ChannelPublication.PublishedUnderOtherName, RemotingChannelNames.CheckPublished(
            "Tekla.Structures.Model-Console:2023.0.0.0", pipes, true, 2023));
    }

    [Fact]
    public void Guard_does_not_count_another_assembly_as_the_same_channel()
    {
        // Only the base channel of this session is up — the Drawing channel is not.
        var pipes = new[] { "Tekla.Structures-Console:2023.0.0.0" };
        Assert.Equal(ChannelPublication.NotPublished, RemotingChannelNames.CheckPublished(
            "Tekla.Structures.Drawing-Console:2023.0.0.0", pipes, true, 2023));
    }

    [Theory]
    [InlineData("Tekla.Structures.Model-Console:2023.0.0.0", false, 2023)] // listing broke off
    [InlineData("Tekla.Structures.Model-TeklaStructures-Console:2024.0.4.0", true, 2024)] // no pipes on 2024+
    [InlineData(null, true, 2023)] // Remoter field not found
    [InlineData("", true, 2023)]
    [InlineData("Tekla.Structures.Catalogs-Console:2023.0.0.0", true, 2023)] // not a guarded assembly
    public void Guard_never_blocks_on_missing_evidence(string? channel, bool complete, int major)
    {
        Assert.Equal(ChannelPublication.Unknown, RemotingChannelNames.CheckPublished(
            channel, new[] { "Tekla.Macros.Akit-2232-1" }, complete, major));
    }

    // --- Which Tekla process a write went to (backlog §4) ---

    [Fact]
    public void Second_instance_is_named_by_its_channel()
    {
        var pid = RemotingChannelNames.ResolveInstancePid(
            "Tekla.Structures.Model-Console-22296:2023.0.0.0", null, new[] { 1000, 22296 }, out var note);
        Assert.Equal(22296, pid);
        Assert.Null(note);
    }

    [Fact]
    public void Plain_channel_is_the_instance_no_other_channel_names()
    {
        var pipes = new[]
        {
            "Tekla.Structures.Model-Console:2023.0.0.0",
            "Tekla.Structures.Model-Console-22296:2023.0.0.0",
        };
        Assert.Equal(1000, RemotingChannelNames.ResolveInstancePid(
            "Tekla.Structures.Model-Console:2023.0.0.0", pipes, new[] { 1000, 22296 }, out _));
    }

    [Fact]
    public void Single_running_instance_is_it()
    {
        Assert.Equal(56432, RemotingChannelNames.ResolveInstancePid(
            "Tekla.Structures.Model-Console:2023.0.0.0", null, new[] { 56432 }, out _));
    }

    [Fact]
    public void Trimble_remoting_name_carries_the_pid_too()
    {
        Assert.Equal(5120, RemotingChannelNames.ResolveInstancePid(
            "Tekla.Structures.Model-TeklaStructures-Console-5120:2026.0.3.0", null, new[] { 77, 5120 }, out _));
    }

    [Fact]
    public void Ambiguity_is_reported_not_guessed()
    {
        // Two instances, neither publishing a PID-suffixed channel we can see (e.g. 2024+, no pipes).
        var pid = RemotingChannelNames.ResolveInstancePid(
            "Tekla.Structures.Model-TeklaStructures-Console:2024.0.4.0", null, new[] { 2, 1 }, out var note);
        Assert.Null(pid);
        Assert.Contains("PIDs 1, 2", note);
    }

    [Fact]
    public void Session_names_with_dashes_are_not_pids()
    {
        var pid = RemotingChannelNames.ResolveInstancePid(
            "Tekla.Structures.Model-RDP-Tcp#47:2023.0.0.0", null, new[] { 1, 2 }, out var note);
        Assert.Null(pid);
        Assert.NotNull(note);
    }

    [Fact]
    public void A_pid_that_is_not_running_is_not_claimed()
    {
        var pid = RemotingChannelNames.ResolveInstancePid(
            "Tekla.Structures.Model-Console-999:2023.0.0.0", null, new[] { 1000 }, out var note);
        Assert.Null(pid);
        Assert.Contains("999", note);
    }

    [Fact]
    public void No_candidates_no_pid()
    {
        Assert.Null(RemotingChannelNames.ResolveInstancePid(
            "Tekla.Structures.Model-Console:2023.0.0.0", null, Array.Empty<int>(), out var note));
        Assert.NotNull(note);
    }

    [Fact]
    public void Guard_with_no_listing_is_unknown()
    {
        Assert.Equal(ChannelPublication.Unknown, RemotingChannelNames.CheckPublished(
            "Tekla.Structures.Model-Console:2023.0.0.0", null, true, 2023));
    }
}
