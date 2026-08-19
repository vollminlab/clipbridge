using Xunit;

namespace ClipBridge.Win32.Tests;

// Reports Skipped on a non-Windows host instead of silently passing. An
// early `if (!OperatingSystem.IsWindows()) return;` inside the body would
// report Passed while executing nothing - see CLAUDE.md gotcha #5, where
// exactly that pattern hid a test that never ran anywhere, including in
// the Windows CI it was written for.
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: exercises real user32/kernel32 calls";
        }
    }
}
