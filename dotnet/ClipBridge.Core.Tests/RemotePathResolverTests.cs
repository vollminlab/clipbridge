using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class RemotePathResolverTests
{
    [Fact]
    public void Returns_the_whole_path_for_one_line_not_its_first_character()
    {
        var r = RemotePathResolver.Resolve("/home/vollmin/.clipbridge/20260819-032734.png\n");
        Assert.Equal("/home/vollmin/.clipbridge/20260819-032734.png", r.Path);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Survives_crlf()
    {
        var r = RemotePathResolver.Resolve("/home/vollmin/.clipbridge/20260819-032734.png\r\n");
        Assert.Equal("/home/vollmin/.clipbridge/20260819-032734.png", r.Path);
    }

    [Fact]
    public void Handles_output_with_no_trailing_newline()
    {
        var r = RemotePathResolver.Resolve("/home/vollmin/.clipbridge/20260819-032734.png");
        Assert.Equal("/home/vollmin/.clipbridge/20260819-032734.png", r.Path);
    }

    [Fact]
    public void Rejects_two_real_lines_and_says_how_many_it_saw()
    {
        var r = RemotePathResolver.Resolve("/home/vollmin/.clipbridge/a.png\n/home/vollmin/.clipbridge/b.png\n");
        Assert.Null(r.Path);
        Assert.Contains("2 non-blank line", r.Reason);
    }

    [Fact]
    public void Rejects_a_relative_path()
    {
        Assert.Null(RemotePathResolver.Resolve("clipbridge/x.png\n").Path);
    }

    [Fact]
    public void Rejects_empty_output()
    {
        Assert.Null(RemotePathResolver.Resolve("").Path);
    }

    [Fact]
    public void Non_blank_lines_returns_every_line_not_just_the_first()
    {
        var lines = RemotePathResolver.NonBlankLines("/home/vollmin/.clipbridge/x.png\n/another/line\n");
        Assert.Equal(2, lines.Count);
        Assert.Equal("/another/line", lines[1]);
    }

    [Fact]
    public void Non_blank_lines_drops_blank_lines()
    {
        Assert.Single(RemotePathResolver.NonBlankLines("\n/home/vollmin/.clipbridge/x.png\n\n"));
    }
}
