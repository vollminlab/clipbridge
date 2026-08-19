using System.Runtime.InteropServices;

namespace ClipBridge.Win32;

internal static partial class NativeMethods
{
    public const uint CF_BITMAP = 2;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    // LibraryImport (source-generated marshalling) is used everywhere it
    // works cleanly. SetWindowsHookExW takes a managed delegate parameter;
    // LibraryImport's source generator has narrower, less-documented
    // support for delegate marshalling than classic DllImport, so that one
    // call (and its two companions, which share the same hook handle type)
    // stays on DllImport - both are fully AOT-compatible, this is a
    // marshalling-reliability choice, not an AOT-compatibility one. Verify
    // during Task 17 whether LibraryImport also works for it; if so this
    // note can be deleted and the three calls converted.

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

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Beep(uint dwFreq, uint dwDuration);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // --- kept on DllImport: see note above ---
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    // --- end DllImport block ---

    [LibraryImport("user32.dll")]
    public static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int VK_CONTROL = 0x11;
    public const int VK_SHIFT = 0x10;
    public const int VK_V = 0x56;
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
