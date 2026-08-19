using System.Text.Json;

namespace ClipBridge.Core;

public sealed record ClipbridgeConfig(string SshHost, string Transport);

public sealed class ClipbridgeConfigException : Exception
{
    public ClipbridgeConfigException(string message) : base(message) { }
}

// Unlike v1's PowerShell parameter-default gotcha (CLAUDE.md Gotcha #2 - a
// default expression touching $env:LOCALAPPDATA throws at parameter-bind
// time on Linux, before the script body or an early return ever runs),
// C# has no equivalent eager-bind-time evaluation trap: configDir is an
// ordinary method argument, resolved by the caller (App/Program.cs) at the
// point of the call, not as a class-level default. That whole class of bug
// does not reproduce here.
public static class ClipbridgeConfigReader
{
    public static ClipbridgeConfig Load(string configDir)
    {
        var path = Path.Combine(configDir, "config.json");
        if (!File.Exists(path))
        {
            throw new ClipbridgeConfigException($"clipbridge config not found at {path} - run clipbridge.exe --install");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ClipbridgeConfigException($"clipbridge config at {path} is not valid JSON - {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var transport = root.TryGetProperty("transport", out var t) ? t.GetString() : null;
            if (transport is not ("ssh" or "wsl"))
            {
                throw new ClipbridgeConfigException($"clipbridge config has an unknown transport '{transport}' - expected ssh or wsl");
            }
            var sshHost = root.TryGetProperty("sshHost", out var h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(sshHost))
            {
                throw new ClipbridgeConfigException("clipbridge config has no sshHost");
            }
            return new ClipbridgeConfig(sshHost, transport);
        }
    }
}
