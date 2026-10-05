using System.Runtime.InteropServices;
using ClipBridge.Core;
using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class ProcessElevationProbeTests
{
    // Shell_NotifyIcon silently rejects a cbSize matching no known version
    // (gotcha #13). The balloon struct must be exactly V2. Marshal.SizeOf is
    // pure layout arithmetic, so this one runs on Linux too.
    [Fact]
    public void Balloon_struct_is_exactly_NOTIFYICONDATAW_V2_SIZE()
    {
        Assert.Equal(NativeMethods.NOTIFYICONDATAW_V2_SIZE, Marshal.SizeOf<NativeMethods.NOTIFYICONDATA_BALLOON>());
    }

    // The test host is the foreground process's peer at best, but its own
    // token is always readable, so this proves the token query path works
    // and agrees with itself.
    [WindowsFact]
    public void Foreground_probe_reports_a_readable_answer_for_something_with_focus()
    {
        Assert.True(ProcessElevationProbe.TryGetForeground(out var pid, out var name, out _));
        Assert.True(pid > 0);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [WindowsFact]
    public void Current_process_elevation_query_does_not_throw()
    {
        // windows-latest runs elevated; either answer is fine, a throw is not.
        _ = ProcessElevationProbe.IsCurrentProcessElevated();
    }
}
