using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class Win32ForegroundWindowTests
{
    [WindowsFact]
    public void Returns_a_non_empty_process_name_when_something_has_focus()
    {
        var fg = new Win32ForegroundWindow();
        var name = fg.GetForegroundProcessName();

        // On a windows-latest runner some window always has focus (even if
        // it's the test host itself), so this should never be null in CI.
        Assert.False(string.IsNullOrWhiteSpace(name));
    }
}
