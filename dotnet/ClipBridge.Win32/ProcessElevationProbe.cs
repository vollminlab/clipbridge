using System.Runtime.InteropServices;
using ClipBridge.Core;

namespace ClipBridge.Win32;

// Reads TokenElevation for this process and for the foreground window's
// process. Runs on a thread-pool timer, never on the pump thread, so a slow
// OpenProcess cannot starve the keyboard hook (gotcha #11).
public static class ProcessElevationProbe
{
    public static bool IsCurrentProcessElevated() =>
        Query(NativeMethods.GetCurrentProcess(), closeProcess: false) == ProcessElevation.Elevated;

    // Returns false when nothing has focus or the process has already exited.
    public static bool TryGetForeground(out int pid, out string? processName, out ProcessElevation elevation)
    {
        pid = 0;
        processName = null;
        elevation = ProcessElevation.Unreadable;

        var hWnd = NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hWnd, out var rawPid);
        if (rawPid == 0) return false;
        pid = (int)rawPid;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return false;
        }

        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, rawPid);
        elevation = handle == IntPtr.Zero ? ProcessElevation.Unreadable : Query(handle, closeProcess: true);
        return true;
    }

    private static ProcessElevation Query(IntPtr processHandle, bool closeProcess)
    {
        try
        {
            if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out var token))
            {
                return ProcessElevation.Unreadable;
            }
            try
            {
                return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out var elevated, sizeof(uint), out _)
                    ? (elevated != 0 ? ProcessElevation.Elevated : ProcessElevation.NotElevated)
                    : ProcessElevation.Unreadable;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            if (closeProcess) NativeMethods.CloseHandle(processHandle);
        }
    }
}
