using System.Runtime.InteropServices;

namespace ClipBridge.Win32;

internal static partial class NativeMethods
{
    public const uint CF_BITMAP = 2;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    // Returns NULL on success (the handle is freed), non-NULL on failure -
    // opposite polarity from most of this file. Used only to release a
    // GlobalAlloc block that failed to make it onto the clipboard via
    // SetClipboardData; once SetClipboardData succeeds, the system owns
    // the handle and this must not be called on it.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalFree(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Beep(uint dwFreq, uint dwDuration);

    // A WinExe (GUI subsystem) process has no console of its own, so Console.Out
    // goes nowhere. --install is the one path a human runs from a terminal and
    // wants to read, so it attaches to the launching shell's console first.
    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(uint dwProcessId);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Task 17 verified LibraryImport marshals a managed delegate parameter
    // cleanly on net10.0 (and is IsAotCompatible-clean too) - these three
    // were on classic DllImport under a note suspecting narrower delegate
    // support; that suspicion didn't hold here, so they're on
    // LibraryImport like everything else in this file now.
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // High-order bit set = key currently down. Aggregates left/right for
    // the generic VK_CONTROL/VK_SHIFT codes - unlike the hook's per-event
    // vkCode, which only ever reports the left/right-distinguished codes
    // (VK_LCONTROL/VK_RCONTROL/VK_LSHIFT/VK_RSHIFT), never the generic
    // ones. See KeyboardHook for why this replaced event-based modifier
    // tracking.
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    public static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int VK_CONTROL = 0x11;
    public const int VK_SHIFT = 0x10;
    public const int VK_V = 0x56;

    // Stamped into KEYBDINPUT.dwExtraInfo by Win32PasteSink on every
    // synthetic Ctrl+V it sends, and checked by KeyboardHook.HookCallback
    // to ignore those events. Injected input (SendInput) is delivered to
    // low-level keyboard hooks exactly like real input - that's what
    // LLKHF_INJECTED exists to flag - so without this marker our own
    // synthesized Ctrl+V re-enters our own hook. On a failed transfer the
    // clipboard still holds the image (failure paths never overwrite it),
    // so the re-entrant Ctrl+V would swallow again, retry, fail again, and
    // loop forever. A marker on OUR OWN dwExtraInfo value is used instead
    // of testing LLKHF_INJECTED because the flag can't distinguish our
    // synthetic input from any other injected input (on-screen keyboard,
    // remote desktop, accessibility tools) - testing it would also
    // silently eat those legitimate keystrokes. This ignores exactly our
    // own events and nothing else.
    // IntPtr (not nuint/const) because dwExtraInfo on both KEYBDINPUT and
    // KBDLLHOOKSTRUCT is IntPtr, and comparing/assigning through a shared
    // static readonly IntPtr avoids re-deriving an unchecked nuint->IntPtr
    // conversion (which overflows CS8778's compile-time nint range check
    // for a value this large) at every call site.
    public static readonly IntPtr ClipbridgeSyntheticMarker = unchecked((IntPtr)(nint)0x_C11B_B21DUL);
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // All three members are declared even though clipbridge only ever sends
    // keyboard input, because Windows sizes the INPUT union by its LARGEST
    // member (MOUSEINPUT, 32 bytes on x64) and SendInput rejects any cbSize
    // that is not exactly sizeof(INPUT) = 40. Declaring only KEYBDINPUT
    // yields a 32-byte INPUT, so SendInput returns 0 with
    // ERROR_INVALID_PARAMETER and silently synthesizes nothing - which would
    // leave Ctrl+V dead on every paste. Measured, not assumed.
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // --- message loop (Task 20) ---
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    // Six fields, 48 bytes on x64 - deliberately NOT seven. The learn.microsoft.com
    // page for MSG lists a trailing `DWORD lPrivate`, which looks like a field we are
    // missing and would mean GetMessageW writing past the end of our buffer. It is not:
    // the real winuser.h guards that field with `#ifdef _MAC`, so it exists only on
    // Macintosh builds. The docs generator flattens the ifdef away - the tell is that
    // lPrivate is the one member listed there with no description. Verified against the
    // header, not inferred from the docs page.
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int nExitCode);

    // Used by TrayIcon.RequestRehook (Task 20) to hop the watchdog timer's
    // callback - which fires on a thread-pool thread - back onto the
    // message-pump thread, where KeyboardHook.Rehook() must run. PostMessage
    // queues and returns immediately without waiting for WndProc to process
    // it, so it is safe to call from any thread, including a thread-pool
    // timer callback.
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // --- tray icon (Task 18) ---
    public const int WM_COMMAND = 0x0111;
    public const int WM_RBUTTONUP = 0x0205;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_DELETE = 0x00000002;
    public const uint MF_STRING = 0x00000000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public static readonly IntPtr IDI_APPLICATION = (IntPtr)32512;

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    // FINDING (Task 18): this struct is truncated relative to the real
    // Win32 NOTIFYICONDATAW - it declares only the fields TrayIcon's
    // NIF_MESSAGE | NIF_ICON | NIF_TIP flags actually read, and is missing
    // dwState, dwStateMask, szInfo[256], the uTimeout/uVersion union,
    // szInfoTitle[64], dwInfoFlags, guidItem, and hBalloonIcon.
    //
    // Measured on x64: Marshal.SizeOf<NOTIFYICONDATA>() = 296 bytes. That
    // does NOT match any of the sizes Shell_NotifyIcon actually validates
    // cbSize against (source: learn.microsoft.com, NOTIFYICONDATAW page,
    // "Remarks" table, cross-checked against the ReactOS shellapi.h mirror
    // of the SDK header for the FIELD_OFFSET expressions):
    //   NOTIFYICONDATAW_V1_SIZE = FIELD_OFFSET(..., szTip[64])   = 168
    //   NOTIFYICONDATAW_V2_SIZE = FIELD_OFFSET(..., guidItem)    = 952
    //   NOTIFYICONDATAW_V3_SIZE = FIELD_OFFSET(..., hBalloonIcon)= 968
    //   sizeof(NOTIFYICONDATAW)  (current/"V4")                  = 976
    // 296 is simply "cbSize through szTip[128], nothing after" - a
    // boundary that only exists in this truncated struct, not in any
    // shell-recognized version. Shell_NotifyIcon returns FALSE for a
    // cbSize it doesn't recognize, so as declared, TrayIcon's
    // Shell_NotifyIconW(NIM_ADD) call fails every time - the icon never
    // appears. TrayIcon.Create()/Dispose() now check that return value
    // (they didn't before this finding), so the failure surfaces instead
    // of vanishing, but the struct itself has deliberately NOT been
    // resized - that's a design call for whoever picks it up next.
    // Options, in order of least to most invasive:
    //   (a) Shrink szTip's SizeConst from 128 to 64. This lands cbSize
    //       exactly on NOTIFYICONDATAW_V1_SIZE (168) with no other
    //       change. The tray tip text is "clipbridge" (10 chars), far
    //       under the 63-char usable limit of a 64-char buffer, so this
    //       costs nothing functionally today.
    //   (b) Declare the additional fields through dwInfoFlags to reach
    //       NOTIFYICONDATAW_V2_SIZE (952), keeping the 128-char tip.
    //   (c) Declare the full modern struct through hBalloonIcon to reach
    //       sizeof(NOTIFYICONDATAW) (976) - most future-proof, most
    //       unused surface area.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        // szTip is 64, not 128. Shell_NotifyIcon validates cbSize against the sizes of
        // its known struct versions and returns FALSE - silently - if it matches none.
        // These seven fields are exactly NOTIFYICONDATAW's V1 field set, but with
        // szTip[128] the struct measures 296 bytes, which is V1's fields at V2's tip
        // length and therefore matches no version at all: V1=168, V2=952, V3=968,
        // current=976. At szTip[64] it measures exactly 168 = NOTIFYICONDATAW_V1_SIZE.
        // V1 supports NIF_MESSAGE|NIF_ICON|NIF_TIP, which is all this tray icon uses
        // (no balloon, no GUID), and the tip is the literal "clipbridge" - 10 of the
        // 63 usable characters. Measured, not derived.
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szTip;
    }

    // Kept on DllImport alongside the hook calls: RegisterClassW takes a
    // struct containing a managed delegate field (WNDCLASS.lpfnWndProc),
    // same marshalling-reliability reasoning as SetWindowsHookExW above.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    // CharSet.Unicode is REQUIRED, not decorative. DllImport defaults to
    // CharSet.Ansi, so without it these `string` parameters marshal as ANSI
    // into the *W* (wide) entry point: the class name arrives as mojibake and
    // CreateWindowExW fails with ERROR_CANNOT_FIND_WND_CLASS (1407). It fails
    // only here and not in RegisterClassW because WNDCLASS.lpszClassName
    // carries an explicit [MarshalAs(UnmanagedType.LPWStr)], so the class is
    // registered under the correct UTF-16 name and then looked up under a
    // mangled ANSI one. Caught by the windows-latest CI smoke test, which is
    // the only place this code runs at all.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    public static partial IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    // LoadIcon/LoadImage take an RT_GROUP_ICON name, not an RT_ICON one. Measured
    // on the built apphost: RT_ICON ids are 1..7 (one per size in the .ico) while
    // the single RT_GROUP_ICON is id 32512 - the same numeric value as
    // IDI_APPLICATION, which is how the apphost makes its icon replace the default.
    // Passing 1 here silently fails and falls back to the generic system icon.
    public static readonly IntPtr AppIconGroupId = (IntPtr)32512;

    public const uint IMAGE_ICON = 1;
    public const uint LR_DEFAULTCOLOR = 0;
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
    public static partial IntPtr LoadImageW(IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    // NOTIFYICONDATA contains a ByValTStr string field, which the
    // LibraryImport source generator does not support (SYSLIB1051: "The
    // type 'NOTIFYICONDATA' is not supported by source-generated
    // P/Invokes" - confirmed by building this file). CharSet is a
    // DllImport-era concept the generator ignores entirely, so there is no
    // LibraryImport attribute spelling that fixes this; classic DllImport
    // is required here, same reasoning as RegisterClassW/CreateWindowExW
    // above (a struct field the generator can't marshal).
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);
}
