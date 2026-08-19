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
}
