using System.Runtime.InteropServices;
using ClipBridge.Core;

namespace ClipBridge.Win32;

// The highest-risk component in this whole design (design spec, Task 17):
// this runs in the input path of every keystroke on the machine.
//
// HookCallback must return within Windows' LowLevelHooksTimeout (5s
// default) or the OS silently unhooks it - no error, no exception,
// clipbridge just stops seeing keystrokes. So the callback does at most: a
// couple of cheap field/state reads, one foreground-process lookup, one
// clipboard format-availability check, and a queue post (via
// SingleThreadDispatcher.Post, injected as postToWorker) - no file I/O, no
// network, no await, ever.
public sealed class KeyboardHook : IKeyboardHook
{
    private static readonly string[] TerminalProcessNames = { "WindowsTerminal" };

    private readonly IForegroundWindow _foregroundWindow;
    private readonly IClipboard _clipboard;
    private readonly Action<Action> _postToWorker;

    // Stored in a field and assigned exactly once (in the constructor),
    // never a local or a per-call lambda. SetWindowsHookExW does not root
    // the delegate on the native side - only the field reference here
    // does. A local/lambda would be eligible for GC as soon as Start()
    // returns, and native code would be left holding a pointer to
    // collected memory: the next keystroke after a GC crashes the process.
    private readonly NativeMethods.LowLevelKeyboardProc _proc;

    private IntPtr _hookHandle;

    public event Action<bool>? PasteRequested;

    public KeyboardHook(IForegroundWindow foregroundWindow, IClipboard clipboard, Action<Action> postToWorker)
    {
        _foregroundWindow = foregroundWindow;
        _clipboard = clipboard;
        _postToWorker = postToWorker;
        _proc = HookCallback;
    }

    public void Start()
    {
        _hookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SetWindowsHookExW failed (GetLastError={error})");
        }
    }

    // Task 20's watchdog calls this unconditionally on a timer, so it must
    // be safe to call at any time, including when nothing is wrong.
    //
    // Installs the NEW hook first and only unhooks the previous handle
    // once the new one is confirmed in place. The plan's original ordering
    // - unhook, then Start() - has a failure window: if the new
    // SetWindowsHookExW call fails, the old (working) hook is already
    // gone, Start() throws, and the process is left with no hook installed
    // at all - exactly the state this watchdog exists to prevent. Doing it
    // the other way round means a failed re-registration leaves the
    // existing hook in place and surfaces the failure instead of silently
    // discarding a working hook.
    public void Rehook()
    {
        var previousHandle = _hookHandle;

        var newHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        if (newHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SetWindowsHookExW failed during Rehook (GetLastError={error}); previous hook left in place");
        }

        _hookHandle = newHandle;

        if (previousHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(previousHandle);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            // Our own synthetic Ctrl+V (Win32PasteSink.SendPaste, via
            // SendInput) is delivered back to this hook exactly like real
            // input. Without this check, a failed transfer - where the
            // clipboard still holds the image because failure paths never
            // overwrite it - would have its own synthetic Ctrl+V swallowed
            // right back, triggering another attempt, another failure,
            // another synthetic Ctrl+V, forever. Comparing dwExtraInfo
            // against our own marker (rather than the LLKHF_INJECTED flag)
            // ignores exactly our own events - LLKHF_INJECTED would also
            // eat legitimate injected input from an on-screen keyboard,
            // remote desktop, or accessibility tool.
            if (data.dwExtraInfo == NativeMethods.ClipbridgeSyntheticMarker)
            {
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            bool isKeyDown = wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN;
            bool isVDown = isKeyDown && data.vkCode == NativeMethods.VK_V;

            if (isVDown)
            {
                // A low-level keyboard hook never reports the generic
                // VK_CONTROL (0x11) / VK_SHIFT (0x10) vkCodes - only the
                // left/right-distinguished ones (VK_LCONTROL/VK_RCONTROL/
                // VK_LSHIFT/VK_RSHIFT). Tracking those generic codes from
                // hook events (as the original plan did) means the
                // modifier flags never go true, ShouldSwallow never fires,
                // and the hook passes every keystroke through untouched -
                // it installs cleanly and silently does nothing.
                //
                // GetAsyncKeyState(VK_CONTROL)/(VK_SHIFT) aggregates left
                // and right for the generic codes, so it is correct here.
                // Querying live state at the moment of a V key-down is
                // also strictly better than event-based tracking: it costs
                // one cheap call only on V key-down instead of bookkeeping
                // on every keystroke, and it cannot desynchronise - which
                // event tracking does if a modifier is already held when
                // the hook installs, or if another hook consumes a key-up
                // before this one sees it.
                bool ctrlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
                bool shiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
                bool forced = ctrlDown && shiftDown;

                var processName = _foregroundWindow.GetForegroundProcessName();
                bool inTerminal = HotkeyDecision.IsForegroundTerminal(processName, TerminalProcessNames);
                // Skip the clipboard call entirely when not in a terminal:
                // it can never change the outcome (ShouldSwallow requires
                // inTerminal), and it's one fewer Win32 call in the
                // hot path of every V keystroke everywhere else.
                bool clipboardHasImage = inTerminal && _clipboard.HasImageAvailable();

                if (HotkeyDecision.ShouldSwallow(ctrlDown, isVDown, inTerminal, forced, clipboardHasImage))
                {
                    _postToWorker(() => PasteRequested?.Invoke(forced));
                    return (IntPtr)1; // swallow
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
