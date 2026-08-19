using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class SshConfigBlockBuilderTests
{
    private static readonly string Block = SshConfigBlockBuilder.Build(
        "clipbridge", "devsbx01", "vollmin", "/home/x/.ssh/devsbx01_id_ed25519.pub");

    [Fact]
    public void Includes_identities_only_yes()
    {
        Assert.Matches(@"IdentitiesOnly\s+yes", Block);
    }

    [Fact]
    public void Includes_forward_agent_no()
    {
        Assert.Matches(@"ForwardAgent\s+no", Block);
    }

    [Fact]
    public void Names_the_host_alias()
    {
        Assert.Matches(@"Host\s+clipbridge", Block);
    }

    [Fact]
    public void Sets_hostname_to_the_real_target()
    {
        Assert.Matches(@"HostName\s+devsbx01", Block);
    }

    [Fact]
    public void Sets_user()
    {
        Assert.Matches(@"User\s+vollmin", Block);
    }

    [Fact]
    public void Points_identity_file_at_the_given_key_path()
    {
        Assert.Contains("IdentityFile /home/x/.ssh/devsbx01_id_ed25519.pub", Block);
    }
}

public class SshConfigInspectorTests
{
    [Fact]
    public void Returns_false_on_an_empty_config()
    {
        Assert.False(SshConfigInspector.HasHostBlock("", "clipbridge"));
    }

    [Fact]
    public void Returns_false_when_the_alias_is_absent()
    {
        Assert.False(SshConfigInspector.HasHostBlock("Host github.com\n    User git\n", "clipbridge"));
    }

    [Fact]
    public void Returns_true_when_the_exact_host_line_is_present()
    {
        var cfg = "Host github.com\n    User git\n\nHost clipbridge\n    HostName devsbx01\n";
        Assert.True(SshConfigInspector.HasHostBlock(cfg, "clipbridge"));
    }

    [Fact]
    public void Round_trips_with_a_block_generated_by_the_builder()
    {
        var block = SshConfigBlockBuilder.Build("clipbridge", "devsbx01", "vollmin", "/home/x/.ssh/devsbx01_id_ed25519.pub");
        Assert.True(SshConfigInspector.HasHostBlock(block, "clipbridge"));
    }

    [Fact]
    public void Tolerates_leading_whitespace_before_host()
    {
        Assert.True(SshConfigInspector.HasHostBlock("  Host clipbridge\n", "clipbridge"));
    }

    [Fact]
    public void Does_not_false_positive_on_an_alias_that_merely_starts_the_same()
    {
        Assert.False(SshConfigInspector.HasHostBlock("Host clipbridge-laptop\n    User someone\n", "clipbridge"));
    }

    [Fact]
    public void Tolerates_a_tab_between_host_and_the_alias()
    {
        // Real ssh_config tokenizes on any whitespace, tab included - verified
        // against OpenSSH 9.6 (`ssh -F <cfg> -G clipbridge` resolves correctly
        // through a tab-separated "Host\tclipbridge" line).
        Assert.True(SshConfigInspector.HasHostBlock("Host\tclipbridge\n", "clipbridge"));
    }

    [Fact]
    public void Does_not_false_positive_on_a_commented_out_host_line()
    {
        // A "# Host clipbridge" line means the real block is ABSENT - returning
        // true here would make the installer skip writing a block the user
        // actually needs.
        Assert.False(SshConfigInspector.HasHostBlock("# Host clipbridge\n", "clipbridge"));
    }

    [Fact]
    public void Detects_the_alias_on_a_line_following_an_include_directive()
    {
        // Include doesn't nest or consume subsequent lines from the current
        // file - "Host clipbridge" right after it is still an ordinary
        // top-level line.
        Assert.True(SshConfigInspector.HasHostBlock("Include other.config\nHost clipbridge\n", "clipbridge"));
    }

    [Fact]
    public void Detects_the_alias_on_an_indented_line_after_a_match_block()
    {
        // ssh_config has no real nesting - indentation under "Match" is purely
        // cosmetic, so an indented "Host clipbridge" line is still a normal,
        // independent top-level Host declaration that ssh will parse as such.
        Assert.True(SshConfigInspector.HasHostBlock("Match host foo\n    Host clipbridge\n", "clipbridge"));
    }

    [Fact]
    public void Is_case_insensitive_matching_v1s_powershell_default()
    {
        // Deliberate faithful port of v1's PowerShell `-match` (case-insensitive
        // by default) - see Install-Clipbridge.ps1's Test-SshConfigHasHostBlock.
        // NOTE: real OpenSSH Host *pattern* matching is case-SENSITIVE (only the
        // "Host" keyword itself is case-insensitive) - verified against OpenSSH
        // 9.6, where "HOST CLIPBRIDGE" does NOT resolve a lookup for alias
        // "clipbridge". This only over-matches against a hand-authored ALL-CAPS
        // block we would never generate ourselves, so it is accepted as-is.
        Assert.True(SshConfigInspector.HasHostBlock("HOST CLIPBRIDGE\n", "clipbridge"));
    }
}

public class ClipbridgePathsTests
{
    [Fact]
    public void Joins_ssh_and_config_paths_under_the_given_directories()
    {
        var p = ClipbridgePaths.From("/home/x/.ssh", "/home/x/.config/clipbridge");
        Assert.Equal(Path.Combine("/home/x/.ssh", "config"), p.SshConfigPath);
        Assert.Equal(Path.Combine("/home/x/.config/clipbridge", "config.json"), p.ConfigJsonPath);
    }
}
