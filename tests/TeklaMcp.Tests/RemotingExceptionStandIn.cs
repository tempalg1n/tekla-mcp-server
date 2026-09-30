using System;

namespace System.Runtime.Remoting
{
    /// <summary>
    /// Stand-in for the .NET Framework type, which .NET 8 does not have: the classifier matches
    /// by full type name precisely so that it works from netstandard2.0.
    /// </summary>
    internal sealed class RemotingException : Exception
    {
        public RemotingException(string message) : base(message) { }
    }
}
