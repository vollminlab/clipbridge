namespace ClipBridge.Core;

// Picks the lossless PNG clipboard stream when present, falls back to
// converting the DIB bitmap otherwise. Mirrors Save-ClipboardPng's
// preference order in Send-Clip.ps1, minus the Win32 extraction itself,
// which lives in ClipBridge.Win32.Win32Clipboard (Task 13).
public static class ClipboardImageExtractor
{
    public static byte[]? Resolve(byte[]? pngBytes, byte[]? dibBytes)
    {
        if (pngBytes is { Length: > 0 }) return pngBytes;
        if (dibBytes is { Length: > 0 }) return DibToPngConverter.Convert(dibBytes);
        return null;
    }
}
