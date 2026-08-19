namespace ClipBridge.Core;

// The pure decision behind the keyboard hook's callback (Task 17). Kept
// separate from the Win32 P/Invoke plumbing so the highest-risk logic in
// this whole design - the swallow/pass-through call - is unit-tested on
// Linux instead of only ever reachable by actually pressing keys on
// Windows.
public static class HotkeyDecision
{
    public static bool IsForegroundTerminal(string? processName, IReadOnlyCollection<string> terminalProcessNames) =>
        processName is not null && terminalProcessNames.Contains(processName);

    // True = swallow the keystroke and hand off to the worker thread.
    // False = call CallNextHookEx immediately, letting the keystroke
    // through untouched.
    public static bool ShouldSwallow(bool ctrlDown, bool isVKeyDown, bool inTerminal, bool forced, bool clipboardHasImage) =>
        ctrlDown && isVKeyDown && inTerminal && (forced || clipboardHasImage);
}
