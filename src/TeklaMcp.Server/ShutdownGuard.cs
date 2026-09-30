using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;

namespace TeklaMcp.Server;

/// <summary>
/// Makes sure this process ends when its MCP client does (DEV-005 backlog §6: dozens of
/// orphaned servers per machine, each still holding a Tekla connection).
///
/// Closing stdin already stops the host — verified on both TFMs, mock and live backend, 0.2 s —
/// so the orphans are processes that never saw EOF, or whose shutdown hung. Two guards, neither
/// depending on which of those it was:
///
///  - Exit with the parent. The process that launched us is the MCP client (or the shim it runs
///    us through); when it is gone, nobody reads our stdout any more. Watched through a process
///    handle, which also pins the PID against reuse. A parent that is already gone at startup,
///    or started after us (a reused PID), is not watched. Opt out with
///    <c>TEKLA_MCP_EXIT_WITH_PARENT=0</c> for launchers that exit while keeping our pipes open.
///  - A stop that hangs is cut short: a Tekla Open API call has no cancellation, so an in-flight
///    tool can hold the host's graceful shutdown indefinitely.
///
/// Windows only — the only platform with Tekla; the net8 mock elsewhere keeps the SDK default.
/// </summary>
internal static class ShutdownGuard
{
    private const string OptOutVariable = "TEKLA_MCP_EXIT_WITH_PARENT";
    private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(10);
    private static int _hardExitArmed;

    public static void Start(IHostApplicationLifetime lifetime)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;

        lifetime.ApplicationStopping.Register(ArmHardExit);

        if (Environment.GetEnvironmentVariable(OptOutVariable) == "0")
        {
            Log($"{OptOutVariable}=0 — not watching the parent process.");
            return;
        }

        var parent = TryOpenParent(out var description);
        if (parent is null)
        {
            Log("not watching the parent process: " + description);
            return;
        }

        var watcher = new Thread(() =>
        {
            try
            {
                parent.WaitForExit();
            }
            catch (Exception ex)
            {
                Log("parent process watch failed, not watching: " + ex.Message);
                return;
            }
            Log($"parent process {description} exited — shutting down (the MCP client is gone).");
            lifetime.StopApplication();
        })
        {
            IsBackground = true,
            Name = "tekla-mcp parent watch",
        };
        watcher.Start();
    }

    /// <summary>Once shutdown starts, the process ends within <see cref="StopDeadline"/>.</summary>
    private static void ArmHardExit()
    {
        if (Interlocked.Exchange(ref _hardExitArmed, 1) != 0) return;
        var timer = new Thread(() =>
        {
            Thread.Sleep(StopDeadline);
            Log($"shutdown did not finish within {StopDeadline.TotalSeconds:0} s (a Tekla call cannot be " +
                "cancelled) — exiting.");
            Environment.Exit(0);
        })
        {
            IsBackground = true, // a normal exit must not wait for it
            Name = "tekla-mcp stop deadline",
        };
        timer.Start();
    }

    private static Process? TryOpenParent(out string description)
    {
        description = "";
        int parentId;
        try
        {
            using var self = Process.GetCurrentProcess();
            parentId = ParentProcessId(self.Handle);
            if (parentId <= 0)
            {
                description = "no parent process id";
                return null;
            }

            var parent = Process.GetProcessById(parentId);
            string name;
            try { name = parent.ProcessName; } catch { name = "?"; }
            description = $"{name} (PID {parentId})";

            // Opens the handle now; from here on the PID cannot be reused while we hold it.
            if (parent.StartTime > self.StartTime)
            {
                description += " started after this process — the real parent is already gone";
                parent.Dispose();
                return null;
            }
            if (parent.HasExited)
            {
                description += " has already exited";
                parent.Dispose();
                return null;
            }
            return parent;
        }
        catch (ArgumentException)
        {
            description = "the parent process has already exited";
            return null;
        }
        catch (Exception ex)
        {
            description = "cannot open the parent process (" + ex.Message + ")";
            return null;
        }
    }

    private static int ParentProcessId(IntPtr processHandle)
    {
        var info = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            processHandle, 0 /* ProcessBasicInformation */, ref info, Marshal.SizeOf(info), out _);
        return status == 0 ? info.InheritedFromUniqueProcessId.ToInt32() : -1;
    }

    private static void Log(string message) => Console.Error.WriteLine("[server] " + message);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, ref ProcessBasicInformation processInformation,
        int processInformationLength, out int returnLength);
}
