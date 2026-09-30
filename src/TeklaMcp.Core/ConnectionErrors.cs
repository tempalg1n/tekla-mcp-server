using System;

namespace TeklaMcp.Core;

/// <summary>
/// Recognizes a lost or dead Tekla Open API connection in an arbitrary tool exception, so the
/// tool-error path can replace the bare remoting text with the cause-and-action diagnosis the
/// live backend builds (<c>TeklaRemotingChannel.DiagnoseConnectionFailure</c>).
///
/// Matched by exception TYPE, never by message: the .NET remoting messages are localized (RU
/// installs). Type names rather than types because <c>RemotingException</c> exists only on
/// .NET Framework and this project is netstandard2.0.
/// </summary>
public static class ConnectionErrors
{
    private const string RemotingExceptionType = "System.Runtime.Remoting.RemotingException";

    /// <summary>
    /// True when the chain holds a <c>RemotingException</c> (the Tekla process behind an
    /// established connection is gone) or a type initializer of a Tekla type that failed (a proxy
    /// that was created against a missing channel — cached by the CLR). IO and socket errors
    /// alone do not count: the file-exchange tools raise those for ordinary file problems.
    /// </summary>
    public static bool IsTeklaConnectionFailure(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (string.Equals(current.GetType().FullName, RemotingExceptionType, StringComparison.Ordinal))
                return true;
            if (current is TypeInitializationException typeInit &&
                (typeInit.TypeName ?? "").StartsWith("Tekla.Structures", StringComparison.Ordinal))
                return true;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    if (!ReferenceEquals(inner, current.InnerException) && IsTeklaConnectionFailure(inner))
                        return true;
        }
        return false;
    }
}
