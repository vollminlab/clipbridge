using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ClipbridgeConfigReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clipbridge-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Reads_sshhost_and_transport_from_config_json()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "sshHost": "clipbridge", "transport": "ssh" }""");
        var cfg = ClipbridgeConfigReader.Load(_dir);
        Assert.Equal("clipbridge", cfg.SshHost);
        Assert.Equal("ssh", cfg.Transport);
    }

    [Fact]
    public void Throws_a_named_error_when_config_json_is_missing()
    {
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Throws_when_transport_is_not_ssh_or_wsl()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "sshHost": "clipbridge", "transport": "carrier-pigeon" }""");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains("carrier-pigeon", ex.Message);
    }

    [Fact]
    public void Throws_when_sshhost_is_blank()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "sshHost": "", "transport": "ssh" }""");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains("no sshHost", ex.Message);
    }

    [Fact]
    public void Names_the_config_path_when_it_is_not_valid_json()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, """{ "sshHost": "clipbridge", """);
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains(cfgPath, ex.Message);
    }
}
