using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ClipbridgeLoggerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clipbridge-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Appends_a_timestamped_line()
    {
        ClipbridgeLogger.Append(_dir, "ssh exploded");
        var line = File.ReadAllLines(Path.Combine(_dir, "clipbridge.log")).Last();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", line);
        Assert.Contains("ssh exploded", line);
    }

    [Fact]
    public void Drops_lines_older_than_7_days_and_keeps_fresh_ones()
    {
        var logPath = Path.Combine(_dir, "clipbridge.log");
        var stale = DateTime.Now.AddDays(-10).ToString("yyyy-MM-ddTHH:mm:ss");
        var fresh = DateTime.Now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ss");
        File.WriteAllLines(logPath, new[] { $"{stale}  old event", $"{fresh}  recent event" });

        ClipbridgeLogger.Append(_dir, "new event");

        var text = string.Join("\n", File.ReadAllLines(logPath));
        Assert.DoesNotContain("old event", text);
        Assert.Contains("recent event", text);
        Assert.Contains("new event", text);
    }

    [Fact]
    public void Keeps_a_line_stamped_exactly_at_the_7_day_cutoff()
    {
        // The filter uses >= against the cutoff stamp, so the boundary is
        // inclusive: a line exactly 7 days old survives one more write.
        var logPath = Path.Combine(_dir, "clipbridge.log");
        var now = new DateTime(2026, 8, 19, 12, 0, 0);
        var cutoffStamp = now.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ss");
        File.WriteAllLines(logPath, new[] { $"{cutoffStamp}  exactly at cutoff" });

        ClipbridgeLogger.Append(_dir, "new event", now);

        var text = string.Join("\n", File.ReadAllLines(logPath));
        Assert.Contains("exactly at cutoff", text);
    }

    // Every other test uses a directory that already exists
    // (Directory.CreateTempSubdirectory), so Directory.CreateDirectory(configDir)
    // inside Append is never exercised - deleting that line left all logger
    // tests green. This covers the case where the config directory itself is
    // missing (e.g. first run before install has created it).
    [Fact]
    public void Creates_the_config_directory_if_it_does_not_exist()
    {
        var missingDir = Path.Combine(_dir, "does-not-exist-yet");
        Assert.False(Directory.Exists(missingDir));

        ClipbridgeLogger.Append(missingDir, "first run");

        Assert.True(Directory.Exists(missingDir));
        var line = File.ReadAllLines(Path.Combine(missingDir, "clipbridge.log")).Last();
        Assert.Contains("first run", line);
    }

    // Append writes "{stamp}  {message}" - two spaces. Existing assertions
    // (a stamp-prefix regex and Contains(message)) are indifferent to
    // separator width; changing it to one space left all tests green.
    // v1 was independently verified to use two spaces (bytes 19 and 20 are
    // both 0x20), so this pins the implementation, not just presence.
    [Fact]
    public void Separates_the_stamp_from_the_message_with_exactly_two_spaces()
    {
        ClipbridgeLogger.Append(_dir, "ssh exploded");
        var line = File.ReadAllLines(Path.Combine(_dir, "clipbridge.log")).Last();

        // Stamp format "yyyy-MM-ddTHH:mm:ss" is exactly 19 characters.
        Assert.Equal(' ', line[19]);
        Assert.Equal(' ', line[20]);
        Assert.NotEqual(' ', line[21]);
    }

    [Fact]
    public void Ordinal_stamp_comparison_orders_correctly_across_a_year_rollover()
    {
        // The comment claims a plain ordinal compare reproduces chronological
        // order because the stamp is fixed-width and zero-padded. Verify it
        // holds across a year boundary, where a naive numeric-suffix compare
        // could get this backwards.
        var logPath = Path.Combine(_dir, "clipbridge.log");
        var now = new DateTime(2026, 1, 2, 0, 0, 0);
        File.WriteAllLines(logPath, new[]
        {
            "2025-12-31T23:59:59  before rollover",
            "2026-01-01T00:00:01  after rollover",
        });

        ClipbridgeLogger.Append(_dir, "new event", now);

        var text = string.Join("\n", File.ReadAllLines(logPath));
        Assert.Contains("before rollover", text);
        Assert.Contains("after rollover", text);
        Assert.Contains("new event", text);
    }
}
