using ClipBridge.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ClipBridge.Core.Tests;

public class DibToPngConverterTests
{
    [Fact]
    public void Converts_a_2x2_dib_to_a_valid_png()
    {
        var dib = DibFixtures.Build(2, 2, 200, 100, 50);
        var png = DibToPngConverter.Convert(dib);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        using var image = Image.Load<Rgba32>(png);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        var pixel = image[0, 0];
        Assert.Equal(200, pixel.R);
        Assert.Equal(100, pixel.G);
        Assert.Equal(50, pixel.B);
    }

    [Fact]
    public void Rejects_a_payload_too_small_to_hold_a_header()
    {
        Assert.Throws<InvalidDataException>(() => DibToPngConverter.Convert(new byte[10]));
    }

    // A negative biHeight means the DIB is stored top-down (row 0 = the
    // topmost displayed row) rather than the usual bottom-up layout. This is
    // a real clipboard shape, not a hypothetical one. The two rows use
    // DIFFERENT colours deliberately -- a uniform fill cannot distinguish a
    // correct decode from a vertically flipped one.
    [Fact]
    public void Converts_a_top_down_dib_without_flipping_the_image()
    {
        const int width = 1, height = 2;
        int rowSize = ((width * 3 + 3) / 4) * 4;
        var header = DibFixtures.Header(width, height, 24, 0, 0);
        BitConverter.GetBytes(-height).CopyTo(header, 8); // negative = top-down
        var pixels = new byte[rowSize * height];
        pixels[0] = 0; pixels[1] = 0; pixels[2] = 255;                 // row 0 (top) = red, BGR
        pixels[rowSize + 0] = 255; pixels[rowSize + 1] = 0; pixels[rowSize + 2] = 0; // row 1 (bottom) = blue
        var dib = header.Concat(pixels).ToArray();

        var png = DibToPngConverter.Convert(dib);
        using var image = Image.Load<Rgba32>(png);

        var top = image[0, 0];
        var bottom = image[0, 1];
        Assert.Equal((255, 0, 0), (top.R, top.G, top.B));
        Assert.Equal((0, 0, 255), (bottom.R, bottom.G, bottom.B));
    }

    // BITMAPV4HEADER (108 bytes) and BITMAPV5HEADER (124 bytes) are what
    // modern Windows clipboards commonly publish for CF_DIB/CF_DIBV5. The
    // extra header bytes must not break offBits/palette-offset math, which
    // is keyed off biSize rather than a hardcoded 40.
    [Theory]
    [InlineData(108u)]
    [InlineData(124u)]
    public void Converts_a_dib_with_a_v4_or_v5_header_size(uint biSize)
    {
        const int width = 2, height = 2;
        int rowSize = ((width * 3 + 3) / 4) * 4;
        var header = DibFixtures.Header(width, height, 24, 0, 0, biSize);
        var pixels = new byte[rowSize * height];
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                int i = row * rowSize + col * 3;
                pixels[i] = 10; pixels[i + 1] = 20; pixels[i + 2] = 30; // BGR
            }
        var dib = header.Concat(pixels).ToArray();

        var png = DibToPngConverter.Convert(dib);
        using var image = Image.Load<Rgba32>(png);
        var pixel = image[0, 0];
        Assert.Equal((30, 20, 10), (pixel.R, pixel.G, pixel.B));
    }

    // The palette-size arithmetic has two branches: biClrUsed explicitly set,
    // and biClrUsed == 0 (meaning "use the full 2^biBitCount palette", per
    // the BMP spec). Both must land on the correct pixel offset.
    [Theory]
    [InlineData(256u)]
    [InlineData(0u)]
    public void Converts_an_8bpp_paletted_dib_regardless_of_biClrUsed(uint clrUsed)
    {
        const int width = 2, height = 1;
        var header = DibFixtures.Header(width, height, 8, 0, clrUsed);
        var palette = new byte[256 * 4];
        palette[0 * 4 + 2] = 255; // index 0 -> red (B,G,R,0)
        palette[1 * 4 + 1] = 255; // index 1 -> green
        int rowSize = ((width + 3) / 4) * 4;
        var pixels = new byte[rowSize * height];
        pixels[0] = 0; pixels[1] = 1;
        var dib = header.Concat(palette).Concat(pixels).ToArray();

        var png = DibToPngConverter.Convert(dib);
        using var image = Image.Load<Rgba32>(png);
        Assert.Equal((255, 0, 0), (image[0, 0].R, image[0, 0].G, image[0, 0].B));
        Assert.Equal((0, 255, 0), (image[1, 0].R, image[1, 0].G, image[1, 0].B));
    }

    // 32bpp BI_RGB with a genuinely varying 4th byte is treated as real
    // alpha by ImageSharp's BMP decoder.
    [Fact]
    public void Preserves_alpha_in_a_32bpp_dib_with_nonzero_alpha_byte()
    {
        var header = DibFixtures.Header(1, 1, 32, 0, 0);
        var pixels = new byte[] { 50, 60, 70, 128 }; // B,G,R,A
        var dib = header.Concat(pixels).ToArray();

        var png = DibToPngConverter.Convert(dib);
        using var image = Image.Load<Rgba32>(png);
        var p = image[0, 0];
        Assert.Equal((70, 60, 50, 128), (p.R, p.G, p.B, p.A));
    }

    // The 4th byte of 32bpp BI_RGB is documented as unused/reserved padding,
    // and real-world screenshot tools commonly leave it at 0. If that byte
    // were read as alpha literally, every such screenshot would decode fully
    // transparent. ImageSharp's decoder specifically guards against this:
    // an all-zero alpha channel is treated as "no alpha data" and forced
    // fully opaque. This test locks in that (desirable) behaviour so a
    // future ImageSharp upgrade that removes the guard fails loudly here
    // instead of shipping invisible screenshots.
    [Fact]
    public void Treats_a_zero_alpha_byte_in_32bpp_bi_rgb_as_fully_opaque()
    {
        var header = DibFixtures.Header(1, 1, 32, 0, 0);
        var pixels = new byte[] { 50, 60, 70, 0 }; // B,G,R, reserved=0
        var dib = header.Concat(pixels).ToArray();

        var png = DibToPngConverter.Convert(dib);
        using var image = Image.Load<Rgba32>(png);
        Assert.Equal(255, image[0, 0].A);
    }

    // BI_BITFIELDS (compression = 3) inserts three 4-byte colour masks
    // between the header and the pixel data.
    [Fact]
    public void Converts_a_32bpp_bi_bitfields_dib()
    {
        var header = DibFixtures.Header(1, 1, 32, 3, 0);
        var masks = new byte[12];
        BitConverter.GetBytes(0x00FF0000u).CopyTo(masks, 0); // R
        BitConverter.GetBytes(0x0000FF00u).CopyTo(masks, 4); // G
        BitConverter.GetBytes(0x000000FFu).CopyTo(masks, 8); // B
        var pixels = new byte[] { 70, 60, 50, 0 }; // B,G,R,pad
        var dib = header.Concat(masks).Concat(pixels).ToArray();

        var png = DibToPngConverter.Convert(dib);
        using var image = Image.Load<Rgba32>(png);
        var p = image[0, 0];
        Assert.Equal((50, 60, 70), (p.R, p.G, p.B));
    }

    // A header claiming a large image but with the pixel bytes truncated
    // away entirely. Must fail cleanly, not throw an unrelated/opaque error.
    [Fact]
    public void Throws_on_truncated_pixel_data()
    {
        var header = DibFixtures.Header(100, 100, 24, 0, 0);
        var dib = header.Concat(new byte[20]).ToArray();
        Assert.Throws<InvalidImageContentException>(() => DibToPngConverter.Convert(dib));
    }

    // Exactly 40 bytes: passes the DibToPngConverter length guard (>= 40)
    // but has zero bytes of pixel data. Boundary case for that guard.
    [Fact]
    public void Throws_when_header_is_present_but_pixel_data_is_entirely_missing()
    {
        var dib = DibFixtures.Header(4, 4, 24, 0, 0);
        Assert.Equal(40, dib.Length);
        Assert.Throws<InvalidImageContentException>(() => DibToPngConverter.Convert(dib));
    }

    // Declared dimensions large enough to imply a multi-gigabyte bitmap, with
    // far too few bytes actually supplied. Must fail fast (ImageSharp has its
    // own degenerate-dimensions guard) rather than attempt a huge allocation
    // or hang.
    [Fact]
    public async Task Rejects_absurdly_large_declared_dimensions_without_hanging()
    {
        var header = DibFixtures.Header(65535, 65535, 24, 0, 0);
        var dib = header.Concat(new byte[100]).ToArray();

        // The concrete exception type ImageSharp raises here is not stable
        // (observed both InvalidImageContentException, wrapping a
        // "possibly degenerate dimensions" guard, and a raw
        // InvalidMemoryOperationException from the allocator's own 4GiB
        // limit, across otherwise-identical runs). What matters for this
        // fallback path is only that it fails fast rather than hanging or
        // silently succeeding with garbage, so this test pins that and
        // nothing more specific.
        //
        // DibToPngConverter.Convert has no async/cancellable overload, so the
        // call still runs on a background thread via Task.Run - but the test
        // itself awaits the result with a timeout instead of blocking with
        // .Wait(), keeping the calling thread (and any synchronization
        // context) free rather than risking a deadlock.
        var task = Task.Run(() => DibToPngConverter.Convert(dib));

        Exception? caught = null;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Assert.Fail("DibToPngConverter did not return within 10s for an absurd declared size");
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
    }
}
