using System.Reflection;
using TeklaMcp.Core;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Issue #17: on Tekla 2026 the resolver must supply Trimble.Remoting and DotNetKit — names outside
/// Tekla* — from the Tekla install, without turning into a loader for any DLL that happens to sit in
/// Tekla's folders (every failed bind of the process reaches it).
/// </summary>
public class TeklaAssemblyProbeTests
{
    private const string RemotingToken = "a70cba4ef557ee03";
    private const string TeklaToken = "2f04dbe497b71114";
    private const string NewtonsoftToken = "30ad4fe6b2a6aeed";

    private static AssemblyName Name(string fullName) => new AssemblyName(fullName);

    [Fact]
    public void The_requested_dependency_from_the_install_is_accepted()
    {
        var remoting = "Trimble.Remoting, Version=4.0.0.0, Culture=neutral, PublicKeyToken=" + RemotingToken;

        Assert.True(TeklaAssemblyProbe.Accepts(Name(remoting), Name(remoting), out var reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void A_newer_file_is_accepted_as_a_binding_redirect_would()
    {
        Assert.True(TeklaAssemblyProbe.Accepts(
            Name("Newtonsoft.Json, Version=12.0.0.0, Culture=neutral, PublicKeyToken=" + NewtonsoftToken),
            Name("Newtonsoft.Json, Version=13.0.0.0, Culture=neutral, PublicKeyToken=" + NewtonsoftToken),
            out _));
    }

    [Fact]
    public void An_older_file_is_refused()
    {
        Assert.False(TeklaAssemblyProbe.Accepts(
            Name("DotNetKit, Version=2026.0.0.0, Culture=neutral, PublicKeyToken=" + TeklaToken),
            Name("DotNetKit, Version=2025.0.0.0, Culture=neutral, PublicKeyToken=" + TeklaToken),
            out var reason));
        Assert.Contains("older", reason);
    }

    [Fact]
    public void A_file_signed_by_someone_else_is_refused()
    {
        Assert.False(TeklaAssemblyProbe.Accepts(
            Name("Trimble.Remoting, Version=4.0.0.0, Culture=neutral, PublicKeyToken=" + RemotingToken),
            Name("Trimble.Remoting, Version=4.0.0.0, Culture=neutral, PublicKeyToken=" + TeklaToken),
            out var reason));
        Assert.Contains("public key token " + TeklaToken + " instead of " + RemotingToken, reason);
    }

    [Fact]
    public void An_unsigned_file_never_answers_a_signed_request()
    {
        Assert.False(TeklaAssemblyProbe.Accepts(
            Name("Trimble.Remoting, Version=4.0.0.0, Culture=neutral, PublicKeyToken=" + RemotingToken),
            Name("Trimble.Remoting, Version=4.0.0.0, Culture=neutral, PublicKeyToken=null"),
            out var reason));
        Assert.Contains("public key token null", reason);
    }

    [Fact]
    public void A_file_holding_another_assembly_is_refused()
    {
        Assert.False(TeklaAssemblyProbe.Accepts(
            Name("DotNetKit, Version=2026.0.0.0, Culture=neutral, PublicKeyToken=" + TeklaToken),
            Name("Something.Else, Version=2026.0.0.0, Culture=neutral, PublicKeyToken=" + TeklaToken),
            out var reason));
        Assert.Contains("Something.Else", reason);
    }

    [Fact]
    public void A_request_by_simple_name_takes_the_installed_file()
    {
        Assert.True(TeklaAssemblyProbe.Accepts(
            Name("DotNetKit"),
            Name("DotNetKit, Version=2026.0.0.0, Culture=neutral, PublicKeyToken=" + TeklaToken),
            out _));
    }

    [Theory]
    [InlineData("Tekla.Structures.Model.resources", true)]
    [InlineData("DotNetKit.resources", true)]
    [InlineData("Tekla.Structures", false)]
    [InlineData("Trimble.Remoting", false)]
    public void Satellite_resource_assemblies_are_recognized(string name, bool satellite)
    {
        Assert.Equal(satellite, TeklaAssemblyProbe.IsSatellite(name));
    }

    [Theory]
    [InlineData("Tekla.Structures", true)]
    [InlineData("tekla.common.geometry", true)]
    [InlineData("Trimble.Remoting", false)]
    [InlineData("DotNetKit", false)]
    [InlineData("TeklaMcp.Tekla", false)]
    [InlineData("TeklaMcp.Core", false)]
    public void Tekla_names_exclude_the_servers_own_assemblies(string name, bool tekla)
    {
        Assert.Equal(tekla, TeklaAssemblyProbe.IsTeklaName(name));
    }

    [Theory]
    [InlineData("TeklaMcp.Scripting", true)]
    [InlineData("teklamcp.server", true)]
    [InlineData("Tekla.Structures", false)]
    public void The_servers_own_assemblies_are_recognized(string name, bool own)
    {
        Assert.Equal(own, TeklaAssemblyProbe.IsServerAssembly(name));
    }

    [Theory]
    // The Open API and its own dependencies: reported.
    [InlineData("Tekla.Structures", null, false, true)]
    [InlineData("Trimble.Remoting", null, false, true)]
    [InlineData("DotNetKit", "Tekla.Structures", false, true)]
    [InlineData("Polly", "Trimble.Remoting", false, true)]
    [InlineData("SemanticDb.NET", "SomeTeklaHelper", true, true)]
    // Everything else that fails to bind in this process: not Tekla's problem.
    [InlineData("Polly", null, false, false)]
    [InlineData("System.Text.Json.SourceGeneration", "TeklaMcp.Server", false, false)]
    [InlineData("Tekla.Structures.Model.XmlSerializers", "System.Xml", false, false)]
    [InlineData("Tekla.Structures.Model.XmlSerializers", "Tekla.Structures.Model", true, false)]
    public void Only_binds_of_the_open_api_are_reported(string requested, string? requester, bool requesterInInstall, bool reported)
    {
        Assert.Equal(reported, TeklaAssemblyProbe.ConcernsTekla(requested, requester, requesterInInstall));
    }

    [Fact]
    public void A_miss_names_the_assembly_who_needed_it_and_why()
    {
        var line = TeklaAssemblyProbe.DescribeMiss(
            Name("Trimble.Remoting, Version=4.0.0.0, Culture=neutral, PublicKeyToken=" + RemotingToken),
            "Tekla.Structures",
            @"not found in C:\TS\bin, C:\TS\bin\Net48Runtime");

        Assert.Equal(@"Trimble.Remoting 4.0.0.0 (needed by Tekla.Structures): not found in C:\TS\bin, C:\TS\bin\Net48Runtime", line);
    }

    [Fact]
    public void A_miss_without_version_or_requester_stays_readable()
    {
        Assert.Equal("Polly: not found", TeklaAssemblyProbe.DescribeMiss(Name("Polly"), null, "not found"));
    }
}
