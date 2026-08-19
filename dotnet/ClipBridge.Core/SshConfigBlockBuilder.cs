using System.Text.RegularExpressions;

namespace ClipBridge.Core;

public static class SshConfigBlockBuilder
{
    // No dedicated, restricted clipbridge key any more (see design spec,
    // "No new SSH key" - a restrict,command= key put in the shared
    // 1Password agent locked the user out of his own machine via WSL/mosh).
    // Authenticates with the user's existing devsbx01 key; IdentitiesOnly
    // pins it out of ~27 agent keys; ForwardAgent no because clipbridge
    // never authenticates onward from devsbx01.
    public static string Build(string hostAlias, string targetHost, string targetUser, string identityFile) =>
        $"""

        Host {hostAlias}
            HostName {targetHost}
            User {targetUser}
            IdentityFile {identityFile}
            IdentitiesOnly yes
            ForwardAgent no

        """;
}

public static class SshConfigInspector
{
    // Matched per-line, anchored on both ends, case-insensitive: 'Host
    // clipbridge' matches but 'Host clipbridge-laptop' must not, or a
    // second run would add a second, shadowing block next to one it wrongly
    // believes is already present.
    public static bool HasHostBlock(string existingConfig, string hostAlias)
    {
        var pattern = new Regex(@"^\s*Host\s+" + Regex.Escape(hostAlias) + @"\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return existingConfig.Split(["\r\n", "\n"], StringSplitOptions.None).Any(line => pattern.IsMatch(line));
    }
}

public sealed record ClipbridgePaths(string SshConfigPath, string ConfigJsonPath)
{
    public static ClipbridgePaths From(string sshDir, string configDir) =>
        new(Path.Combine(sshDir, "config"), Path.Combine(configDir, "config.json"));
}
