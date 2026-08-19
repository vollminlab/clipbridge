using System.Runtime.InteropServices;
using ClipBridge.Core;

namespace ClipBridge.Win32;

public sealed class Win32Clipboard : IClipboard
{
    private static readonly uint PngFormat = NativeMethods.RegisterClipboardFormat("PNG");

    public bool HasImageAvailable() =>
        NativeMethods.IsClipboardFormatAvailable(PngFormat) ||
        NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB);

    public byte[]? TryGetPng()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("OpenClipboard failed");
        try
        {
            var png = ReadGlobal(PngFormat);
            var dib = png is null ? ReadGlobal(NativeMethods.CF_DIB) : null;
            return ClipboardImageExtractor.Resolve(png, dib);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public ClipboardSnapshot Capture()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("OpenClipboard failed");
        Dictionary<uint, byte[]> data;
        try
        {
            data = new Dictionary<uint, byte[]>();
            var png = ReadGlobal(PngFormat);
            if (png is not null) data[PngFormat] = png;
            var dib = ReadGlobal(NativeMethods.CF_DIB);
            if (dib is not null) data[NativeMethods.CF_DIB] = dib;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
        return new ClipboardSnapshot(data.Count > 0, data);
    }

    public void Restore(ClipboardSnapshot snapshot)
    {
        // Guards both `HadData == false` (nothing was on the clipboard at
        // capture time) and `default(ClipboardSnapshot)` (HadData defaults
        // to false, and FormatsToData defaults to null - see Task 10). The
        // foreach below is therefore never reached with a null
        // FormatsToData, but the null-conditional is kept anyway as a
        // second, independent guard: a future caller constructing
        // `snapshot with { HadData = true }` on top of a default instance
        // (or any other way a mismatched HadData/FormatsToData pair could
        // arise) must not turn into a NullReferenceException here.
        if (!snapshot.HadData || snapshot.FormatsToData is null) return;
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("OpenClipboard failed");
        try
        {
            NativeMethods.EmptyClipboard();
            foreach (var (format, bytes) in snapshot.FormatsToData)
            {
                WriteGlobal(format, bytes);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public void SetPathText(string path)
    {
        // A trailing space, same as clipbridge.ahk's A_Clipboard := path . " ".
        var bytes = System.Text.Encoding.Unicode.GetBytes(path + " \0");
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("OpenClipboard failed");
        try
        {
            NativeMethods.EmptyClipboard();
            WriteGlobal(NativeMethods.CF_UNICODETEXT, bytes);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    // Caller must already hold the clipboard open.
    private static byte[]? ReadGlobal(uint format)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format)) return null;
        var hGlobal = NativeMethods.GetClipboardData(format);
        if (hGlobal == IntPtr.Zero) return null;
        var ptr = NativeMethods.GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var size = (int)NativeMethods.GlobalSize(hGlobal);
            if (size == 0) return null;
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            NativeMethods.GlobalUnlock(hGlobal);
        }
    }

    // Caller must already hold the clipboard open (and have called
    // EmptyClipboard once per open, not once per format).
    //
    // Every Win32 return value here is checked before it is dereferenced.
    // GlobalAlloc can return NULL (out of memory); GlobalLock on a NULL or
    // otherwise-invalid handle also returns NULL. Skipping either check
    // and calling Marshal.Copy(bytes, 0, IntPtr.Zero, len) writes through a
    // null pointer, which in an AOT binary is an access violation that
    // kills the whole process - the same process the user's Ctrl+V depends
    // on, not a recoverable managed exception.
    //
    // Ownership: on a successful SetClipboardData, the system takes
    // ownership of hGlobal and it must NOT be freed here. On failure the
    // caller (this method) still owns it and must GlobalFree it or the
    // block leaks for the life of the process.
    private static void WriteGlobal(uint format, byte[] bytes)
    {
        var hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)bytes.Length);
        if (hGlobal == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"GlobalAlloc failed (Win32 error {Marshal.GetLastWin32Error()})");
        }

        var ptr = NativeMethods.GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.GlobalFree(hGlobal);
            throw new InvalidOperationException($"GlobalLock failed (Win32 error {error})");
        }

        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(hGlobal);
        }

        if (NativeMethods.SetClipboardData(format, hGlobal) == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.GlobalFree(hGlobal);
            throw new InvalidOperationException($"SetClipboardData failed (Win32 error {error})");
        }
        // Success: the system now owns hGlobal. Do not free it.
    }
}
