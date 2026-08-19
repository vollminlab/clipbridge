using System.Collections.Concurrent;

namespace ClipBridge.Win32;

// The keyboard hook callback (KeyboardHook.HookCallback, Task 17) must
// return to Windows within LowLevelHooksTimeout (5s default) or the OS
// silently unhooks it - no exception, no log line, clipbridge just stops
// working on the next keystroke. PasteOrchestrator.Handle does file I/O
// and an ssh round-trip, so it can never run on the hook thread. This
// class is the queue that moves that work off it: Post() is the only
// thing the hook thread calls, and it returns immediately.
//
// A single dedicated background thread (not the ThreadPool) so posted
// work is strictly ordered - one paste attempt finishes before the next
// one starts, even if the user mashes Ctrl+V - and so the thread has a
// name that shows up in a debugger/dump instead of an anonymous pool
// worker.
public sealed class SingleThreadDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public SingleThreadDispatcher()
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "clipbridge-worker",
        };
        _thread.Start();
    }

    public void Post(Action action) => _queue.Add(action);

    private void RunLoop()
    {
        // GetConsumingEnumerable ends (and the thread exits) once
        // CompleteAdding is called from Dispose - no separate cancellation
        // token needed.
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch
            {
                // Swallow and continue: one posted action throwing must
                // never take down the dispatcher, or every hotkey press
                // after the first failure would silently stop being
                // handled at all. PasteOrchestrator.Handle already
                // guarantees it does not throw for the paths it knows
                // about (see its own invariant comment); this is the
                // backstop for anything it doesn't.
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
    }
}
