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
            // JsonElement.TryGetProperty/GetString throw InvalidOperationException
            // (not JsonException) for shape mismatches that aren't syntax errors -
            // a non-object root, or a field present with the wrong JSON type. Those
            // are still "this config file is malformed" from the user's point of
            // view, so they're funneled through the same named exception as the
            // syntax-error and missing-field cases, always naming the file path.
            string? transport;
            string? sshHost;
            try
            {
                var root = doc.RootElement;
                transport = root.TryGetProperty("transport", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                sshHost = root.TryGetProperty("sshHost", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            }
            catch (InvalidOperationException ex)
            {
                throw new ClipbridgeConfigException($"clipbridge config at {path} has an unexpected shape - {ex.Message}");
            }

            if (transport is not ("ssh" or "wsl"))
            {
                throw new ClipbridgeConfigException($"clipbridge config at {path} has an unknown transport '{transport}' - expected ssh or wsl");
            }
            if (string.IsNullOrWhiteSpace(sshHost))
            {
                throw new ClipbridgeConfigException($"clipbridge config at {path} has no sshHost");
            }
            return new ClipbridgeConfig(sshHost, transport);
        }
    }
}
