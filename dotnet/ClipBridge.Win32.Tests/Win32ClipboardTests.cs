using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class Win32ClipboardTests
{
    [WindowsFact]
    public void No_image_on_an_empty_clipboard_returns_null()
    {
        NativeMethods.OpenClipboard(IntPtr.Zero);
        NativeMethods.EmptyClipboard();
        NativeMethods.CloseClipboard();

        var clipboard = new Win32Clipboard();
        Assert.False(clipboard.HasImageAvailable());
        Assert.Null(clipboard.TryGetPng());
    }

    [WindowsFact]
    public void Set_path_text_then_capture_and_restore_round_trips_through_real_win32_calls()
    {
        var clipboard = new Win32Clipboard();
        clipboard.SetPathText("/home/vollmin/.clipbridge/20260819-1.png");

        // No exception thrown is the assertion here: Capture/Restore call
        // real OpenClipboard/GetClipboardData/GlobalLock/SetClipboardData
        // and this is the first place any of that is exercised for real.
        var snapshot = clipboard.Capture();
        clipboard.Restore(snapshot);
    }
}
