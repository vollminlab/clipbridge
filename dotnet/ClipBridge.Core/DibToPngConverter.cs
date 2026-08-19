using SixLabors.ImageSharp;

namespace ClipBridge.Core;

// Converts a raw CF_DIB clipboard payload (BITMAPINFOHEADER + optional
// color table + pixel data, no BITMAPFILEHEADER) into PNG bytes. Windows
// hands us the DIB without a file header; ImageSharp's BMP decoder requires
// one, so this synthesizes a minimal 14-byte BITMAPFILEHEADER and lets
// ImageSharp do the rest. Pure byte transform, no Win32 API involved.
public static class DibToPngConverter
{
    public static byte[] Convert(byte[] dibBytes)
    {
        if (dibBytes.Length < 40)
            throw new InvalidDataException($"DIB payload too small to hold a BITMAPINFOHEADER: {dibBytes.Length} bytes");

        uint biSize = BitConverter.ToUInt32(dibBytes, 0);
        ushort biBitCount = BitConverter.ToUInt16(dibBytes, 14);
        uint biClrUsed = BitConverter.ToUInt32(dibBytes, 32);

        int paletteEntries = biClrUsed != 0 ? (int)biClrUsed : (biBitCount <= 8 ? 1 << biBitCount : 0);
        int paletteBytes = paletteEntries * 4;

        uint offBits = (uint)(14 + biSize + paletteBytes);
        uint fileSize = (uint)(14 + dibBytes.Length);

        using var bmp = new MemoryStream();
        bmp.WriteByte((byte)'B');
        bmp.WriteByte((byte)'M');
        bmp.Write(BitConverter.GetBytes(fileSize));
        bmp.Write(BitConverter.GetBytes((ushort)0)); // reserved1
        bmp.Write(BitConverter.GetBytes((ushort)0)); // reserved2
        bmp.Write(BitConverter.GetBytes(offBits));
        bmp.Write(dibBytes);
        bmp.Position = 0;

        using var image = Image.Load(bmp);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        return png.ToArray();
    }
}
