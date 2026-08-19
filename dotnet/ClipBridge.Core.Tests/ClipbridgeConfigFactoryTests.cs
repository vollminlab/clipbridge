using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ClipbridgeConfigFactoryTests
{
    [Fact]
    public void Sets_sshhost_to_the_alias_not_the_real_hostname()
    {
        Assert.Equal("clipbridge", ClipbridgeConfigFactory.Create("clipbridge", "ssh").SshHost);
    }

    [Fact]
    public void Carries_the_transport_through()
    {
        Assert.Equal("wsl", ClipbridgeConfigFactory.Create("clipbridge", "wsl").Transport);
    }

    [Fact]
    public void Rejects_a_transport_outside_ssh_wsl()
    {
        Assert.Throws<ArgumentException>(() => ClipbridgeConfigFactory.Create("clipbridge", "carrier-pigeon"));
    }

    [Fact]
    public void Round_trips_through_json_with_the_shape_the_reader_expects()
    {
        var cfg = ClipbridgeConfigFactory.Create("clipbridge", "ssh");
        var json = ClipbridgeConfigWriter.ToJson(cfg);

        var dir = Directory.CreateTempSubdirectory("clipbridge-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), json);
            var roundTripped = ClipbridgeConfigReader.Load(dir);
            Assert.Equal("clipbridge", roundTripped.SshHost);
            Assert.Equal("ssh", roundTripped.Transport);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Round_trips_a_non_ascii_host_alias()
    {
        // ToJson only escapes backslash and double-quote, not \uXXXX - but raw
        // UTF-8 bytes outside the ASCII control range are legal inside a JSON
        // string per RFC 8259, and File.WriteAllText/JsonDocument.Parse both
        // default to UTF-8, so this round-trips even though the encoder never
        // special-cases it. (Contrast: control characters do NOT round-trip -
        // see the write-up in the task report; not asserted here because that
        // is a known, accepted gap in a hand-rolled encoder that only ever
        // receives the literal "clipbridge" in production.)
        var cfg = ClipbridgeConfigFactory.Create("clipbrïdge", "ssh");
        var json = ClipbridgeConfigWriter.ToJson(cfg);

        var dir = Directory.CreateTempSubdirectory("clipbridge-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), json);
            var roundTripped = ClipbridgeConfigReader.Load(dir);
            Assert.Equal("clipbrïdge", roundTripped.SshHost);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
