using System.Runtime.CompilerServices;
using TeklaMcp.Core;

namespace TeklaMcp.Tekla;

/// <summary>
/// Creates the live backend only once the located Tekla matches this build.
///
/// <see cref="TeklaModelService"/>'s static initializers bind the Tekla Open API (enum tables such
/// as the parts chain), and a type initializer that failed once stays failed for the whole process
/// — the CLR does not run it again even after the assembly becomes resolvable (verified on .NET
/// Framework 4.8). Constructing the service during a version mismatch therefore kept every tool
/// broken after the right Tekla started, and reported a type-load error instead of the actual
/// problem. The DI container does not keep a singleton factory that threw, so the next tool call
/// simply tries again; until then every call gets the clear "wrong build" message.
/// </summary>
public static class TeklaBackendFactory
{
    public static ITeklaModelService Create()
    {
        TeklaAssemblyResolver.EnsureVersionMatch();
        return CreateService();
    }

    // Kept out of Create(): compiling the method that constructs the service must not happen
    // before the version check has run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ITeklaModelService CreateService() => new TeklaModelService();
}
