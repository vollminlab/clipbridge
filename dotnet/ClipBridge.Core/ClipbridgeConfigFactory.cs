namespace ClipBridge.Core;

public static class ClipbridgeConfigFactory
{
    // sshHost is deliberately the ssh config ALIAS (e.g. 'clipbridge'), not
    // the real target hostname - the transport passes this straight to
    // ssh.exe / wsl.exe -e ssh, which resolves it through ~/.ssh/config,
    // picking up HostName/User/IdentityFile/IdentitiesOnly from the block
    // above. Passing the real hostname here would bypass that block
    // entirely and lose IdentitiesOnly.
    public static ClipbridgeConfig Create(string hostAlias, string transport)
    {
        if (transport is not ("ssh" or "wsl"))
            throw new ArgumentException($"unknown transport '{transport}' - expected ssh or wsl", nameof(transport));
        return new ClipbridgeConfig(hostAlias, transport);
    }
}

// Hand-written, not System.Text.Json's reflection-based JsonSerializer:
// this project publishes with PublishAot=true, and a reflection-based
// serializer needs a source-generated JsonSerializerContext to stay
// trim-safe. For a two-field object, hand-writing sidesteps that entirely -
// deliberate, not an oversight.
public static class ClipbridgeConfigWriter
{
    public static string ToJson(ClipbridgeConfig config) =>
        $$"""{"sshHost":"{{JsonEncode(config.SshHost)}}","transport":"{{JsonEncode(config.Transport)}}"}""";

    private static string JsonEncode(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
