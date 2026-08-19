using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipBridge.Win32;

// Design decision #4 (design spec): minimal tray UI, no settings window -
// config.json stays the configuration surface. Raw Shell_NotifyIcon, not
// System.Windows.Forms.NotifyIcon, keeps this consistent with the rest of
// the Win32 layer and avoids pulling in the WinForms message-loop/control
// model this design otherwise avoids.
//
// Verified manually only (design spec's uninstrumentable tier) - there is
// nothing here for a test to assert against without a real desktop session
// and a mouse. That makes every return-value check below load-bearing: a
// silent failure here means the tray icon simply never appears, with
// nothing in the process to say why.
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAYICON = 0x8000 + 1;
    // Distinct WM_APP id, not reused with WM_APP_TRAYICON - Shell_NotifyIcon
    // delivers WM_APP_TRAYICON with mouse-event info packed into lParam
    // (see WndProc's `(int)lParam == WM_RBUTTONUP` check), and the rehook
    // request carries no such payload. Sharing one id would mean either
    // message could be misread as the other.
    private const int WM_APP_REHOOK = 0x8000 + 2;
    private const int ID_OPEN_LOG = 1001;
    private const int ID_REINSTALL = 1002;
    private const int ID_EXIT = 1003;
    private const string ClassName = "ClipBridgeTrayWindow";
    private const uint NotifyIconId = 1;

    // RegisterClassW's documented failure code when the class name is
    // already registered in this process. Create() is not guaranteed to
    // run only once over the process lifetime (a future caller on the
    // Reinstall path could plausibly call it again), so this one failure
    // code is treated as non-fatal - the existing class registration is
    // still perfectly usable. Every other RegisterClassW failure is fatal.
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    private readonly string _logPath;
    private readonly Action _onExit;
    private readonly Action _onReinstall;
    private readonly Action? _onRehook;

    // Pinned for the same GC-collection reason as KeyboardHook's _proc: a
    // collected delegate means native code (the window procedure) calls a
    // freed function pointer on the next message.
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private IntPtr _hwnd;

    public TrayIcon(string logPath, Action onExit, Action onReinstall, Action? onRehook = null)
    {
        _logPath = logPath;
        _onExit = onExit;
        _onReinstall = onReinstall;
        _onRehook = onRehook;
        _wndProc = WndProc;
    }

    // Called by Program.cs's watchdog Timer callback, which runs on a
    // thread-pool thread - never call onRehook (KeyboardHook.Rehook)
    // directly from there. SetWindowsHookExW's docs state a low-level hook
    // "can be called on the thread that installed the hook" and that
    // "the hooking application must continue to pump messages" on that
    // thread; a thread-pool thread has no message loop, so re-installing
    // the hook there leaves it permanently undeliverable. PostMessageW
    // queues WM_APP_REHOOK onto this window's message loop (the real pump,
    // in Program.Main) and returns immediately - cheap and thread-safe -
    // so the actual Rehook() call happens on WndProc, on the pump thread.
    public void RequestRehook() => NativeMethods.PostMessageW(_hwnd, WM_APP_REHOOK, IntPtr.Zero, IntPtr.Zero);

    public void Create()
    {
        var hInstance = NativeMethods.GetModuleHandleW(null);
        var wc = new NativeMethods.WNDCLASS
        {
            lpfnWndProc = _wndProc,
            lpszClassName = ClassName,
            hInstance = hInstance,
        };

        // RegisterClassW returns 0 on failure. Left unchecked, a genuine
        // failure (anything other than "already registered") means the
        // window can never be created, the tray callback can never
        // arrive, and the icon never appears - with nothing anywhere
        // saying why.
        if (NativeMethods.RegisterClassW(ref wc) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new InvalidOperationException($"RegisterClassW failed (GetLastError={error})");
            }
            // Already registered from a previous Create() call in this
            // process - not an error, the existing registration works.
        }

        _hwnd = NativeMethods.CreateWindowExW(0, ClassName, "clipbridge",
            0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        // CreateWindowExW returns NULL on failure. Without this check,
        // every subsequent call below runs against a zero hWnd, and the
        // tray callback that ShowContextMenu/WndProc depend on can never
        // be delivered.
        if (_hwnd == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"CreateWindowExW failed (GetLastError={error})");
        }

        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = (int)NotifyIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = WM_APP_TRAYICON,
            hIcon = NativeMethods.LoadIconW(IntPtr.Zero, NativeMethods.IDI_APPLICATION),
            szTip = "clipbridge",
        };

        // Shell_NotifyIconW returns FALSE on failure. This is the single
        // most likely silent failure in this file: cbSize is computed via
        // Marshal.SizeOf on a NOTIFYICONDATA struct that is truncated
        // relative to the real Win32 NOTIFYICONDATAW, and Shell_NotifyIcon
        // rejects any cbSize that doesn't match one of its known struct
        // versions - see the finding recorded on NativeMethods.NOTIFYICONDATA.
        // Before this check existed, that rejection was invisible: the call
        // returns, Create() returns, and the tray icon simply never
        // appears.
        if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref nid))
        {
            throw new InvalidOperationException(
                $"Shell_NotifyIconW(NIM_ADD) failed (GetLastError={Marshal.GetLastWin32Error()}, " +
                $"cbSize={nid.cbSize}); cbSize may not match a NOTIFYICONDATAW version the shell " +
                "recognizes - see the finding recorded on NativeMethods.NOTIFYICONDATA");
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAYICON && (int)lParam == NativeMethods.WM_RBUTTONUP)
        {
            ShowContextMenu();
            return IntPtr.Zero;
        }
        if (msg == WM_APP_REHOOK)
        {
            _onRehook?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WM_COMMAND)
        {
            var id = (int)wParam & 0xFFFF;
            if (id == ID_OPEN_LOG)
            {
                OpenLog();
            }
            else if (id == ID_REINSTALL)
            {
                _onReinstall();
            }
            else if (id == ID_EXIT)
            {
                _onExit();
            }
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // Process.Start(UseShellExecute = true) throws (typically Win32Exception,
    // e.g. ERROR_FILE_NOT_FOUND) when the target path does not exist - which
    // is exactly the state of a fresh install that hasn't logged anything
    // yet. This process owns the user's Ctrl+V: an unhandled exception
    // raised from inside WndProc here would take down the tray window (and
    // with it, every future right-click) over nothing worse than clicking
    // "Open log" too early. The catch is intentionally broad rather than
    // pinned to Win32Exception - opening a log file is not a critical
    // operation, and getting the exact exception type wrong across Windows
    // versions/shell configurations is a worse failure mode than swallowing
    // one extra category of error here.
    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing to surface to the user without a UI beyond the tray
            // itself; swallow rather than crash the process for a menu
            // click on a file that doesn't exist yet.
        }
    }

    private void ShowContextMenu()
    {
        var hMenu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, ID_OPEN_LOG, "Open log");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, ID_REINSTALL, "Reinstall");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, ID_EXIT, "Exit");
        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.SetForegroundWindow(_hwnd); // required, or the menu won't dismiss on an outside click
        NativeMethods.TrackPopupMenu(hMenu, NativeMethods.TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(hMenu);
    }

    public void Dispose()
    {
        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = (int)NotifyIconId,
        };

        // Best-effort by design: Dispose must not throw (standard .NET
        // guidance, and the caller is almost always already on an exit
        // path where an exception here has nowhere useful to go). If
        // cbSize fails validation the same way it can in Create(), the
        // worst outcome is a stale icon left in the tray until Explorer
        // next repaints the notification area - cosmetic, not fatal. Still
        // surfaced (rather than fully discarded) so it shows up if anyone
        // is watching stderr/the log, without escalating to an exception.
        if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid))
        {
            Console.Error.WriteLine(
                $"TrayIcon.Dispose: Shell_NotifyIconW(NIM_DELETE) failed (GetLastError={Marshal.GetLastWin32Error()})");
        }
    }
}
