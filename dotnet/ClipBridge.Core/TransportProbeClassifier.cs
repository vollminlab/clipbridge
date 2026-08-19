using System.Text.RegularExpressions;

namespace ClipBridge.Core;

public enum ProbeOutcome { ExeNotFound, Authenticated, PermissionDenied, Timeout, OtherFailure, NotProbed }

public static class TransportProbeClassifier
{
    public static ProbeOutcome Classify(bool exeFound, int exitCode, string stdErr)
    {
        if (!exeFound) return ProbeOutcome.ExeNotFound;
        if (exitCode == 0) return ProbeOutcome.Authenticated;
        if (stdErr.Contains("Permission denied")) return ProbeOutcome.PermissionDenied;
        if (Regex.IsMatch(stdErr, "timed out|timeout", RegexOptions.IgnoreCase)) return ProbeOutcome.Timeout;
        return ProbeOutcome.OtherFailure;
    }

    // Ordered so the most actionable, most likely diagnosis wins.
    // "Permission denied" on this laptop's known setup (keys in the
    // 1Password agent, not on disk) is overwhelmingly a locked/stopped
    // agent, not a misconfigured server.
    public static string TransportFailureMessage(ProbeOutcome sshOutcome, ProbeOutcome wslOutcome, string targetHost)
    {
        const string agentHint =
            "This almost always means the 1Password SSH agent is locked or not running - " +
            "it offers every key it holds on unlock, so a locked or stopped agent offers " +
            "none and the server correctly reports no valid key. Unlock 1Password (or " +
            "start it) so it can serve your key, then re-run this script. If the key " +
            "genuinely is not authorized, confirm the clipbridge public key is present in ";

        if (sshOutcome == ProbeOutcome.PermissionDenied && wslOutcome == ProbeOutcome.PermissionDenied)
            return $"Both ssh.exe and wsl.exe -e ssh reached {targetHost} and were told 'Permission denied (publickey)'. {agentHint}~/.ssh/authorized_keys on {targetHost}.";

        if (sshOutcome == ProbeOutcome.PermissionDenied || wslOutcome == ProbeOutcome.PermissionDenied)
        {
            var which = sshOutcome == ProbeOutcome.PermissionDenied ? "ssh.exe" : "wsl.exe -e ssh";
            return $"{which} reached {targetHost} and was told 'Permission denied (publickey)'. {agentHint}~/.ssh/authorized_keys on {targetHost}.";
        }

        if (sshOutcome == ProbeOutcome.Timeout || wslOutcome == ProbeOutcome.Timeout)
            return $"Connection to {targetHost} timed out - the box may be unreachable, powered off, or on a different network than this laptop. This is a connectivity problem, not an authentication one. Fix connectivity to {targetHost} first, then re-run.";

        if (sshOutcome == ProbeOutcome.ExeNotFound && wslOutcome == ProbeOutcome.ExeNotFound)
            return "Neither ssh.exe nor wsl.exe was found on PATH. Install the OpenSSH client (Settings > Apps > Optional Features > OpenSSH Client) or WSL, then re-run.";

        return $"No ssh client authenticated to {targetHost}. ssh.exe: {sshOutcome}, wsl.exe -e ssh: {wslOutcome}. Fix ssh first, then re-run.";
    }

    public static string SelectTransport(ProbeOutcome sshOutcome, ProbeOutcome wslOutcome, string targetHost)
    {
        if (sshOutcome == ProbeOutcome.Authenticated) return "ssh";
        if (wslOutcome == ProbeOutcome.Authenticated) return "wsl";
        throw new ClipbridgeConfigException(TransportFailureMessage(sshOutcome, wslOutcome, targetHost));
    }
}
