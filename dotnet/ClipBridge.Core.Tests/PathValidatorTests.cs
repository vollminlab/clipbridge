using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class PathValidatorTests
{
    [Theory]
    [InlineData("/home/vollmin/.clipbridge/20260818-041500.png", true)]
    [InlineData(null, false)]                                              // null guard
    [InlineData("", false)]
    [InlineData("clipbridge/x.png", false)]                                // relative
    [InlineData("/home/vollmin/my screenshots/x.png", false)]              // space
    [InlineData("/home/vollmin/.clipbridge/x.png\n", false)]               // trailing LF
    [InlineData("/home/vollmin/.clipbridge/x.png\r", false)]               // trailing CR
    [InlineData("/home/vollmin/.clipbridge/x.png\r\n", false)]             // trailing CRLF
    [InlineData("/home/vollmin/.clipbridge/x.png\n/another/line", false)]  // embedded newline
    public void Validates_against_v1_cases(string? path, bool expected)
    {
        Assert.Equal(expected, PathValidator.IsValid(path));
    }

    [Fact]
    public void Rejects_non_ascii()
    {
        Assert.False(PathValidator.IsValid("/home/vollmin/.clipbridge/café-x.png"));
    }

    [Fact]
    public void Rejects_a_control_character()
    {
        Assert.False(PathValidator.IsValid("/home/vollmin/.clipbridge/x\u0007.png"));
    }
}
