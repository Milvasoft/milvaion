using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Milvasoft.Milvaion.Sdk.Worker.Persistence;

/// <summary>
/// Last-resort guard that terminates the process after a failed worker startup.
/// Uses background threads so it never keeps the process alive by itself.
/// </summary>
/// <remarks>
/// Termination is done in two stages: <see cref="Environment.Exit(int)"/> first, then - if that blocks (e.g. a
/// ProcessExit handler waits for a host that never finishes) - an OS-level exit. <c>Process.Kill()</c> cannot be used
/// for the second stage: inside a container the worker usually runs as PID 1, which ignores SIGKILL sent from within
/// its own PID namespace.
/// </remarks>
internal static class StartupFailureWatchdog
{
    private static readonly TimeSpan _hardExitDelay = TimeSpan.FromSeconds(10);
    private static int _armed;

    private delegate void ExitFunction(int status);

    /// <summary>
    /// Terminates the current process with exit code 1 after <paramref name="gracePeriod"/> unless it has exited by then. Only the first call has an effect.
    /// </summary>
    public static void Arm(TimeSpan gracePeriod)
    {
        if (Interlocked.Exchange(ref _armed, 1) == 1)
            return;

        StartBackgroundThread("Milvaion startup failure watchdog", () =>
        {
            Thread.Sleep(gracePeriod);

            TryWriteError($"Worker did not exit within {gracePeriod.TotalSeconds}s after a failed startup. Terminating process.");

            StartBackgroundThread("Milvaion startup failure hard exit", () =>
            {
                Thread.Sleep(_hardExitDelay);

                TryWriteError("Environment.Exit did not complete. Forcing process exit.");

                HardExit(1);
            });

            Environment.Exit(1);
        });
    }

    private static void HardExit(int exitCode)
    {
        if (!OperatingSystem.IsWindows())
        {
            // _exit(2) ends the process immediately without running any handlers, regardless of PID.
            foreach (var libraryName in new[] { "libc.so.6", "libc.dylib", "libc" })
            {
                if (NativeLibrary.TryLoad(libraryName, out var library) && NativeLibrary.TryGetExport(library, "_exit", out var exitPointer))
                    Marshal.GetDelegateForFunctionPointer<ExitFunction>(exitPointer)(exitCode);
            }
        }

        using var process = Process.GetCurrentProcess();

        process.Kill();
    }

    private static void StartBackgroundThread(string name, ThreadStart action) => new Thread(action) { IsBackground = true, Name = name }.Start();

    private static void TryWriteError(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
            // Best effort only.
        }
    }
}
