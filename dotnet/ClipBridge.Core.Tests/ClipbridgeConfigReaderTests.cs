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

    // The table below is a malformed-shape sweep found by mutation testing:
    // JsonElement.TryGetProperty/GetString throw InvalidOperationException
    // (not JsonException) when the root isn't an object, or a field exists
    // but isn't a string. Every row must surface as ClipbridgeConfigException
    // naming the config path - never the raw InvalidOperationException.

    [Fact]
    public void Throws_named_error_when_root_is_a_json_array()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, "[]");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains(cfgPath, ex.Message);
    }

    [Fact]
    public void Throws_named_error_when_root_is_a_json_string()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, "\"just a string\"");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains(cfgPath, ex.Message);
    }

    [Fact]
    public void Throws_named_error_when_root_is_json_null()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, "null");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains(cfgPath, ex.Message);
    }

    [Fact]
    public void Throws_named_error_when_transport_is_a_number()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, """{"transport": 5, "sshHost": "x"}""");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains(cfgPath, ex.Message);
    }

    // Deliberate divergence from v1: v1's PowerShell coerced a numeric
    // sshHost into a string and used it. v2 does not reproduce that
    // coercion - a numeric sshHost is a clean named error, not a crash.
    [Fact]
    public void Throws_named_error_when_sshhost_is_a_number()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, """{"sshHost": 5, "transport": "ssh"}""");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains(cfgPath, ex.Message);
    }

    [Fact]
    public void Throws_named_error_when_sshhost_and_transport_are_both_null()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, """{"sshHost": null, "transport": null}""");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains(cfgPath, ex.Message);
    }

    // Mutation-tested: swapping the order of the transport check and the
    // sshHost check in Load leaves all the single-invalid-field tests green,
    // because none of them exercise a config that is invalid in BOTH ways
    // at once. This pins that the transport check wins, matching v1.
    [Fact]
    public void When_transport_and_sshhost_are_both_invalid_the_transport_error_wins()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "sshHost": "", "transport": "carrier-pigeon" }""");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains("carrier-pigeon", ex.Message);
        Assert.DoesNotContain("no sshHost", ex.Message);
    }

    [Fact]
    public void Accepts_extra_unknown_keys()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"sshHost": "clipbridge", "transport": "ssh", "somethingElse": 123}""");
        var cfg = ClipbridgeConfigReader.Load(_dir);
        Assert.Equal("clipbridge", cfg.SshHost);
        Assert.Equal("ssh", cfg.Transport);
    }
}
