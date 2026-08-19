namespace ClipBridge.Core.Tests;

internal static class DibFixtures
{
    // 24bpp BI_RGB, no palette, rows padded to a 4-byte boundary, bottom-up -
    // the standard Windows DIB layout, which is also what ImageSharp's BMP
    // decoder expects once a BITMAPFILEHEADER is prepended.
    public static byte[] Build(int width, int height, byte r, byte g, byte b)
    {
        int rowSize = ((width * 3 + 3) / 4) * 4;
        int pixelDataSize = rowSize * height;
        var header = new byte[40];
        BitConverter.GetBytes(40u).CopyTo(header, 0);          // biSize
        BitConverter.GetBytes(width).CopyTo(header, 4);         // biWidth
        BitConverter.GetBytes(height).CopyTo(header, 8);        // biHeight (positive = bottom-up)
        BitConverter.GetBytes((ushort)1).CopyTo(header, 12);    // biPlanes
        BitConverter.GetBytes((ushort)24).CopyTo(header, 14);   // biBitCount
        BitConverter.GetBytes(0u).CopyTo(header, 16);           // biCompression = BI_RGB
        BitConverter.GetBytes((uint)pixelDataSize).CopyTo(header, 20);

        var pixels = new byte[pixelDataSize];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int i = row * rowSize + col * 3;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; // BGR order
            }
        }
        return header.Concat(pixels).ToArray();
    }

    // Lower-level builder used by the edge-case tests in DibToPngConverterTests
    // that need control over biBitCount / biCompression / biClrUsed / biSize
    // (BITMAPV4HEADER / BITMAPV5HEADER, BI_BITFIELDS, palette variants) that
    // Build() above does not expose.
    public static byte[] Header(int width, int height, ushort bitCount, uint compression, uint clrUsed, uint biSize = 40)
    {
        var header = new byte[biSize];
        BitConverter.GetBytes(biSize).CopyTo(header, 0);
        BitConverter.GetBytes(width).CopyTo(header, 4);
        BitConverter.GetBytes(height).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 12);
        BitConverter.GetBytes(bitCount).CopyTo(header, 14);
        BitConverter.GetBytes(compression).CopyTo(header, 16);
        BitConverter.GetBytes(clrUsed).CopyTo(header, 32);
        return header;
    }
}
