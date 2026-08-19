using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class SshArgumentBuilderTests
{
    [Fact]
    public void Ssh_transport_uses_ssh_exe_with_no_prefix_ending_in_the_remote_command()
    {
        var inv = SshArgumentBuilder.Build("ssh", "clipbridge");
        Assert.Equal("ssh.exe", inv.Exe);
        Assert.Equal(new[] { "clipbridge", "/home/vollmin/.local/bin/clipbridge-recv" }, inv.Arguments);
    }

    [Fact]
    public void Wsl_transport_prefixes_with_dash_e_ssh_ending_in_the_remote_command()
    {
        var inv = SshArgumentBuilder.Build("wsl", "clipbridge");
        Assert.Equal("wsl.exe", inv.Exe);
        Assert.Equal(new[] { "-e", "ssh", "clipbridge", "/home/vollmin/.local/bin/clipbridge-recv" }, inv.Arguments);
    }

    [Theory]
    [InlineData("ssh")]
    [InlineData("wsl")]
    public void Custom_remote_command_is_appended_last_for_both_transports(string transport)
    {
        var inv = SshArgumentBuilder.Build(transport, "clipbridge", "/opt/custom/clipbridge-recv");
        Assert.Equal("/opt/custom/clipbridge-recv", inv.Arguments[^1]);
    }

    [Fact]
    public void Unknown_transport_throws()
    {
        Assert.Throws<ArgumentException>(() => SshArgumentBuilder.Build("carrier-pigeon", "clipbridge"));
    }
}
