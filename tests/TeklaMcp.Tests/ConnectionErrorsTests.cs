using System;
using System.IO;
using System.Reflection;
using TeklaMcp.Core;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Which tool exceptions get the lost-connection diagnosis (backlog §6). Only remoting failures
/// and failed Tekla type initializers — never ordinary IO errors from the file-exchange tools.
/// </summary>
public class ConnectionErrorsTests
{
    [Fact]
    public void Remoting_failure_is_a_connection_failure()
    {
        Assert.True(ConnectionErrors.IsTeklaConnectionFailure(
            new System.Runtime.Remoting.RemotingException("Failed to connect to an IPC port")));
    }

    [Fact]
    public void Wrapped_remoting_failure_is_found_in_the_chain()
    {
        var wrapped = new TargetInvocationException(new InvalidOperationException(
            "scan failed", new System.Runtime.Remoting.RemotingException("Requested service not found")));
        Assert.True(ConnectionErrors.IsTeklaConnectionFailure(wrapped));
    }

    [Fact]
    public void Remoting_failure_inside_an_aggregate_is_found()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("first"),
            new System.Runtime.Remoting.RemotingException("second"));
        Assert.True(ConnectionErrors.IsTeklaConnectionFailure(aggregate));
    }

    [Theory]
    [InlineData("Tekla.Structures.ModuleManager")]
    [InlineData("Tekla.Structures.ModelInternal.DelegateProxy")]
    public void Failed_tekla_type_initializer_is_a_connection_failure(string typeName)
    {
        Assert.True(ConnectionErrors.IsTeklaConnectionFailure(
            new TypeInitializationException(typeName, new InvalidOperationException("channel"))));
    }

    [Fact]
    public void Failed_non_tekla_type_initializer_is_not()
    {
        Assert.False(ConnectionErrors.IsTeklaConnectionFailure(
            new TypeInitializationException("TeklaMcp.Core.Something", new InvalidOperationException("bug"))));
    }

    [Fact]
    public void Io_errors_alone_are_not_connection_failures()
    {
        // tekla_export_parts_file raises these for a locked or missing file.
        Assert.False(ConnectionErrors.IsTeklaConnectionFailure(new IOException("file in use")));
        Assert.False(ConnectionErrors.IsTeklaConnectionFailure(new InvalidOperationException("no connection")));
        Assert.False(ConnectionErrors.IsTeklaConnectionFailure(null));
    }
}
