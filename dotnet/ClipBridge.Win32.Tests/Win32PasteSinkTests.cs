using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class Win32PasteSinkTests
{
    [WindowsFact]
    public void Send_paste_does_not_throw_and_reports_all_four_events_sent()
    {
        // SendInput's return value (events actually accepted by the input
        // queue) is the only signal available without a foreground window
        // to actually receive the paste - a full receive-side assertion
        // needs a real terminal and is covered by the manual test tier
        // (design spec's "Manual, on the laptop" section), not CI.
        var sink = new Win32PasteSink();
        sink.SendPaste(); // throws InvalidOperationException on a short send
    }
}
