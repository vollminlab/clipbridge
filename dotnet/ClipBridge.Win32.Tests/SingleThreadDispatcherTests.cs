using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class SingleThreadDispatcherTests
{
    [Fact]
    public void Runs_posted_work_off_the_calling_thread()
    {
        using var dispatcher = new SingleThreadDispatcher();
        var callingThreadId = Environment.CurrentManagedThreadId;
        var seenThreadId = -1;
        var done = new ManualResetEventSlim();

        dispatcher.Post(() =>
        {
            seenThreadId = Environment.CurrentManagedThreadId;
            done.Set();
        });

        Assert.True(done.Wait(TimeSpan.FromSeconds(2)), "posted work never ran");
        Assert.NotEqual(callingThreadId, seenThreadId);
    }

    [Fact]
    public void An_exception_in_posted_work_does_not_kill_the_dispatcher()
    {
        using var dispatcher = new SingleThreadDispatcher();
        var secondRan = new ManualResetEventSlim();

        dispatcher.Post(() => throw new InvalidOperationException("boom"));
        dispatcher.Post(() => secondRan.Set());

        Assert.True(secondRan.Wait(TimeSpan.FromSeconds(2)), "dispatcher died after the first posted action threw");
    }
}
