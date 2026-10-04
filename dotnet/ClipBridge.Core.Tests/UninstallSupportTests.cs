using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class SshConfigHostBlockRemoverTests
{
    private static readonly string Block = SshConfigBlockBuilder.Build(
        "clipbridge", "devsbx01.vollminlab.com", "vollmin", @"C:\Users\x\.ssh\devsbx01_id_ed25519.pub");

    [Fact]
    public void Undoes_exactly_what_install_appended()
    {
        var before = "Host devsbx01\n    HostName devsbx01.vollminlab.com\n";
        Assert.Equal(before, SshConfigHostBlockRemover.Remove(before + Block, "clipbridge"));
    }

    // Install's leading newline became this file's final newline, so the
    // missing one is not recoverable - and ending in a newline is harmless.
    [Fact]
    public void Undoes_install_on_a_file_with_no_final_newline()
    {
        var before = "Host devsbx01\n    HostName devsbx01.vollminlab.com";
        Assert.Equal(before + "\n", SshConfigHostBlockRemover.Remove(before + Block, "clipbridge"));
    }

    [Fact]
    public void Undoes_install_into_an_empty_file()
    {
        Assert.Equal("", SshConfigHostBlockRemover.Remove(Block, "clipbridge").Trim());
    }

    [Fact]
    public void Keeps_blocks_that_follow_it()
    {
        var config = "Host a\n    User x\n\nHost clipbridge\n    HostName h\n\nHost b\n    User y\n";
        Assert.Equal("Host a\n    User x\n\nHost b\n    User y\n", SshConfigHostBlockRemover.Remove(config, "clipbridge"));
    }

    [Fact]
    public void Stops_at_a_match_block()
    {
        var config = "Host clipbridge\n    HostName h\nMatch host b\n    User y\n";
        Assert.Equal("Match host b\n    User y\n", SshConfigHostBlockRemover.Remove(config, "clipbridge"));
    }

    [Theory]
    [InlineData("Host clipbridge-laptop\n    HostName h\n")]
    [InlineData("Host clipbridge other\n    HostName h\n")]
    [InlineData("Host devsbx01\n    HostName h\n")]
    public void Never_touches_a_block_install_would_not_call_its_own(string config)
    {
        Assert.Same(config, SshConfigHostBlockRemover.Remove(config, "clipbridge"));
    }

    [Fact]
    public void Removes_every_copy()
    {
        var config = "Host clipbridge\n    HostName a\n\nHost keep\n    User k\n\nHost clipbridge\n    HostName b\n";
        Assert.Equal("Host keep\n    User k\n", SshConfigHostBlockRemover.Remove(config, "clipbridge"));
    }

    [Fact]
    public void Preserves_crlf()
    {
        var config = "Host a\r\n    User x\r\n\r\nHost clipbridge\r\n    HostName h\r\n";
        Assert.Equal("Host a\r\n    User x\r\n", SshConfigHostBlockRemover.Remove(config, "clipbridge"));
    }

    [Fact]
    public void Matches_case_insensitively_like_the_inspector()
    {
        Assert.Equal("", SshConfigHostBlockRemover.Remove("host CLIPBRIDGE\n    HostName h\n", "clipbridge").Trim());
    }
}

public class UninstallRegistrationTests
{
    private const string Exe = @"C:\Program Files\clip bridge\ClipBridge.App.exe";
    private static readonly Dictionary<string, object> V =
        UninstallRegistration.Values(Exe, 5_000_000).ToDictionary(v => v.Name, v => v.Value);

    [Fact]
    public void Uninstall_string_quotes_a_path_with_spaces()
    {
        Assert.Equal("\"C:\\Program Files\\clip bridge\\ClipBridge.App.exe\" --uninstall", V["UninstallString"]);
    }

    [Fact]
    public void Quiet_uninstall_string_passes_quiet()
    {
        Assert.Equal("\"C:\\Program Files\\clip bridge\\ClipBridge.App.exe\" --uninstall --quiet", V["QuietUninstallString"]);
    }

    [Fact]
    public void Estimated_size_is_a_dword_in_kib_rounded_up()
    {
        Assert.Equal(4883, Assert.IsType<int>(V["EstimatedSize"]));
    }

    [Fact]
    public void Offers_no_modify_or_repair()
    {
        Assert.Equal(1, V["NoModify"]);
        Assert.Equal(1, V["NoRepair"]);
    }

    [Fact]
    public void Display_name_is_clipbridge()
    {
        Assert.Equal("clipbridge", V["DisplayName"]);
    }

    [Fact]
    public void Lives_under_hkcu_uninstall()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\clipbridge", UninstallRegistration.KeyPath);
    }
}
