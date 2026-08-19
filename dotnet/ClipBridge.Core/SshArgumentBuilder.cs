namespace ClipBridge.Core;

public static class SshArgumentBuilder
{
    // Absolute path: a non-interactive ssh command does not reliably have
    // ~/.local/bin on PATH even though an interactive login shell does.
    public const string DefaultRemoteCommand = "/home/vollmin/.local/bin/clipbridge-recv";

    public static SshInvocation Build(string transport, string sshHost, string remoteCommand = DefaultRemoteCommand) =>
        transport switch
        {
            "wsl" => new SshInvocation("wsl.exe", new[] { "-e", "ssh", sshHost, remoteCommand }),
            "ssh" => new SshInvocation("ssh.exe", new[] { sshHost, remoteCommand }),
            _ => throw new ArgumentException($"unknown transport '{transport}' - expected ssh or wsl", nameof(transport)),
        };
}

public sealed record SshInvocation(string Exe, IReadOnlyList<string> Arguments);
