namespace ClipBridge.Core;

// What clipbridge could learn about the foreground process's token.
// Unreadable is its own state, not folded into either answer: a
// standard-user process is often refused TOKEN_QUERY on an elevated
// process, so "could not read" is itself evidence of elevation - but it is
// weaker evidence, and the log line says which one it was.
public enum ProcessElevation { NotElevated, Elevated, Unreadable }

// Decides when to warn that clipbridge cannot see Ctrl+V in the foreground
// terminal.
//
// Windows' UIPI does not deliver an elevated window's keystrokes to a
// lower-integrity process's low-level keyboard hook. So with Windows
// Terminal "Run as administrator" and clipbridge at standard permissions,
// HookCallback never runs for that window: nothing is swallowed, nothing is
// logged, and the terminal receives an image-only clipboard and pastes
// nothing. The hook cannot detect a keystroke it is never shown, so this
// is driven by polling the foreground window instead.
//
// Warns at most once per terminal process id - a per-focus warning would
// fire every time the user alt-tabs back to a window they already know
// about.
public sealed class ElevationWarningPolicy
{
    private readonly HashSet<int> _warnedPids = new();

    public string? Evaluate(bool selfElevated, string? processName, int pid, ProcessElevation targetElevation)
    {
        if (selfElevated) return null; // an elevated clipbridge sees every window's input
        if (!HotkeyDecision.IsForegroundTerminal(processName, HotkeyDecision.TerminalProcessNames)) return null;
        if (targetElevation == ProcessElevation.NotElevated) return null;
        if (!_warnedPids.Add(pid)) return null;

        var evidence = targetElevation == ProcessElevation.Elevated
            ? "is running as administrator"
            : "is probably running as administrator (its token could not be read)";
        return $"{processName} (pid {pid}) {evidence} and clipbridge is not, so Windows hides that " +
               "window's Ctrl+V from clipbridge and image pastes there will do nothing. " +
               "Run the terminal without elevation, or run clipbridge elevated.";
    }
}
