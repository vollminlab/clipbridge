using ClipBridge.Core;

namespace ClipBridge.Win32;

public sealed class Win32ForegroundWindow : IForegroundWindow
{
    public string? GetForegroundProcessName()
    {
        var hWnd = NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Process exited between GetForegroundWindow and GetProcessById.
            return null;
        }
    }
}
