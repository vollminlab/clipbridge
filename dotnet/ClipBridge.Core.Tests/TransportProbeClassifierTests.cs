using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class TransportProbeClassifierTests
{
    [Fact]
    public void Reports_exe_not_found_when_the_exe_was_not_found()
    {
        Assert.Equal(ProbeOutcome.ExeNotFound, TransportProbeClassifier.Classify(false, -1, ""));
    }

    [Fact]
    public void Reports_authenticated_on_a_clean_exit_0()
    {
        Assert.Equal(ProbeOutcome.Authenticated, TransportProbeClassifier.Classify(true, 0, ""));
    }

    [Fact]
    public void Reports_permission_denied_when_stderr_says_so()
    {
        var outcome = TransportProbeClassifier.Classify(true, 255, "user@devsbx01: Permission denied (publickey).");
        Assert.Equal(ProbeOutcome.PermissionDenied, outcome);
    }

    [Fact]
    public void Reports_timeout_on_a_connection_timeout()
    {
        var outcome = TransportProbeClassifier.Classify(true, 255, "ssh: connect to host devsbx01 port 22: Connection timed out");
        Assert.Equal(ProbeOutcome.Timeout, outcome);
    }

    [Fact]
    public void Reports_other_failure_for_anything_else()
    {
        var outcome = TransportProbeClassifier.Classify(true, 255, "ssh: Could not resolve hostname devsbx01: Name or service not known");
        Assert.Equal(ProbeOutcome.OtherFailure, outcome);
    }

    [Fact]
    public void Names_the_1password_agent_when_both_transports_are_denied()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.PermissionDenied, ProbeOutcome.PermissionDenied, "devsbx01");
        Assert.Contains("1Password", msg);
        Assert.Contains("ssh.exe and wsl.exe", msg);
    }

    [Fact]
    public void Names_the_1password_agent_and_which_transport_when_only_one_is_denied()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.PermissionDenied, ProbeOutcome.ExeNotFound, "devsbx01");
        Assert.Contains("1Password", msg);
        Assert.StartsWith("ssh.exe", msg);
    }

    [Fact]
    public void Names_wsl_exe_e_ssh_specifically_when_that_is_the_one_denied()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.ExeNotFound, ProbeOutcome.PermissionDenied, "devsbx01");
        Assert.Contains("wsl.exe -e ssh", msg);
    }

    [Fact]
    public void Reports_a_timeout_distinctly_without_blaming_1password()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.Timeout, ProbeOutcome.Timeout, "devsbx01");
        Assert.Contains("timed out", msg);
        Assert.DoesNotContain("1Password", msg);
    }

    [Fact]
    public void Reports_both_executables_missing_distinctly()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.ExeNotFound, ProbeOutcome.ExeNotFound, "devsbx01");
        Assert.Contains("PATH", msg);
        Assert.DoesNotContain("1Password", msg);
    }

    [Fact]
    public void Falls_back_to_a_generic_message_for_an_unmatched_combination()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.OtherFailure, ProbeOutcome.OtherFailure, "devsbx01");
        Assert.Contains("OtherFailure", msg);
        Assert.Contains("devsbx01", msg);
    }

    [Fact]
    public void Picks_ssh_when_ssh_authenticated()
    {
        Assert.Equal("ssh", TransportProbeClassifier.SelectTransport(ProbeOutcome.Authenticated, ProbeOutcome.NotProbed, "devsbx01"));
    }

    [Fact]
    public void Picks_wsl_when_ssh_failed_but_wsl_authenticated()
    {
        Assert.Equal("wsl", TransportProbeClassifier.SelectTransport(ProbeOutcome.PermissionDenied, ProbeOutcome.Authenticated, "devsbx01"));
    }

    [Fact]
    public void Prefers_ssh_over_wsl_when_both_authenticate()
    {
        Assert.Equal("ssh", TransportProbeClassifier.SelectTransport(ProbeOutcome.Authenticated, ProbeOutcome.Authenticated, "devsbx01"));
    }

    [Fact]
    public void Throws_the_locked_agent_message_when_both_are_denied()
    {
        var ex = Assert.Throws<ClipbridgeConfigException>(
            () => TransportProbeClassifier.SelectTransport(ProbeOutcome.PermissionDenied, ProbeOutcome.PermissionDenied, "devsbx01"));
        Assert.Contains("1Password", ex.Message);
    }

    // --- Real OpenSSH 9.6p1 stderr, captured live against this box (Task 9 probe 1). ---
    // `ssh -o BatchMode=yes -o ConnectTimeout=2 -p 1 127.0.0.1 true`
    [Fact]
    public void Real_stderr_connection_refused_classifies_as_OtherFailure()
    {
        var outcome = TransportProbeClassifier.Classify(true, 255, "ssh: connect to host 127.0.0.1 port 1: Connection refused");
        Assert.Equal(ProbeOutcome.OtherFailure, outcome);
    }

    // `timeout 15 ssh -o BatchMode=yes -o ConnectTimeout=3 10.255.255.1 true` (unroutable address)
    [Fact]
    public void Real_stderr_unroutable_host_classifies_as_Timeout()
    {
        var outcome = TransportProbeClassifier.Classify(true, 255, "ssh: connect to host 10.255.255.1 port 22: Connection timed out");
        Assert.Equal(ProbeOutcome.Timeout, outcome);
    }

    // Real denial against a local sshd (StrictHostKeyChecking + Load-key warnings mixed into the
    // same stderr the way a real installer run would see it).
    [Fact]
    public void Real_stderr_permission_denied_with_surrounding_warning_noise_still_classifies_as_PermissionDenied()
    {
        const string stdErr =
            "Warning: Permanently added '127.0.0.1' (ED25519) to the list of known hosts.\n" +
            "Load key \"/dev/null\": error in libcrypto\n" +
            "vollmin@127.0.0.1: Permission denied (publickey).\n";
        var outcome = TransportProbeClassifier.Classify(true, 255, stdErr);
        Assert.Equal(ProbeOutcome.PermissionDenied, outcome);
    }

    // Real successful auth against a local sshd: exit 0 but stderr is non-empty (known_hosts warning).
    [Fact]
    public void Real_stderr_known_hosts_warning_on_a_successful_exit_still_classifies_as_Authenticated()
    {
        var outcome = TransportProbeClassifier.Classify(true, 0, "Warning: Permanently added '127.0.0.1' (ED25519) to the list of known hosts.\n");
        Assert.Equal(ProbeOutcome.Authenticated, outcome);
    }

    // --- Probe 4: exit code is trusted over stderr content, even adversarially. ---
    [Fact]
    public void Exit_zero_wins_over_a_literal_permission_denied_string_in_stderr()
    {
        // A hostile/unlucky MOTD banner echoing "Permission denied" on an otherwise successful
        // connection must not be misread as a failure - the exit code is authoritative.
        var outcome = TransportProbeClassifier.Classify(true, 0, "Welcome! Note: Permission denied is what you'll see if you mess this up.");
        Assert.Equal(ProbeOutcome.Authenticated, outcome);
    }

    // --- Probe 3: precedence when stderr carries both markers (e.g. concatenated multi-attempt output). ---
    [Fact]
    public void Permission_denied_marker_takes_precedence_over_a_timeout_marker_in_the_same_stderr()
    {
        const string stdErr =
            "ssh: connect to host devsbx01 port 22: Connection timed out\n" +
            "user@devsbx01: Permission denied (publickey).";
        var outcome = TransportProbeClassifier.Classify(true, 255, stdErr);
        Assert.Equal(ProbeOutcome.PermissionDenied, outcome);
    }

    // --- Probe 6: TransportFailureMessage is public and reachable with an Authenticated outcome,
    // even though SelectTransport never calls it in that shape (it returns before throwing).
    // These lock in the CURRENT (misleading) behaviour so a future change is deliberate, not silent.
    [Fact]
    public void Message_is_misleading_when_one_side_actually_authenticated()
    {
        // ssh succeeded, but the message blames wsl for a "Permission denied" as if the whole
        // probe failed - it does not mention that ssh, in fact, authenticated.
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.Authenticated, ProbeOutcome.PermissionDenied, "devsbx01");
        Assert.StartsWith("wsl.exe -e ssh reached devsbx01", msg);
        Assert.DoesNotContain("ssh.exe: Authenticated", msg);
    }

    [Fact]
    public void Message_is_self_contradictory_when_both_sides_actually_authenticated()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.Authenticated, ProbeOutcome.Authenticated, "devsbx01");
        Assert.Equal("No ssh client authenticated to devsbx01. ssh.exe: Authenticated, wsl.exe -e ssh: Authenticated. Fix ssh first, then re-run.", msg);
    }
}
