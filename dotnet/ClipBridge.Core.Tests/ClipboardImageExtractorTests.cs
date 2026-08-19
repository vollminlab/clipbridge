using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ClipboardImageExtractorTests
{
    [Fact]
    public void Prefers_png_over_dib_when_both_present()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var dib = DibFixtures.Build(1, 1, 10, 20, 30);
        Assert.Same(png, ClipboardImageExtractor.Resolve(png, dib));
    }

    [Fact]
    public void Falls_back_to_dib_when_no_png()
    {
        var dib = DibFixtures.Build(1, 1, 10, 20, 30);
        var result = ClipboardImageExtractor.Resolve(null, dib);
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, result![..8]);
    }

    [Fact]
    public void Returns_null_when_neither_present()
    {
        Assert.Null(ClipboardImageExtractor.Resolve(null, null));
    }
}
