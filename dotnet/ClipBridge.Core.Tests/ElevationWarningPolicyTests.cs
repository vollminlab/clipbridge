using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ElevationWarningPolicyTests
{
    [Fact]
    public void Warns_for_an_elevated_terminal_when_clipbridge_is_not_elevated()
    {
        var message = new ElevationWarningPolicy().Evaluate(false, "WindowsTerminal", 42, ProcessElevation.Elevated);

        Assert.NotNull(message);
        Assert.Contains("pid 42", message);
        Assert.Contains("is running as administrator", message);
    }

    [Fact]
    public void An_unreadable_token_warns_but_says_the_evidence_is_weaker()
    {
        var message = new ElevationWarningPolicy().Evaluate(false, "WindowsTerminal", 42, ProcessElevation.Unreadable);

        Assert.NotNull(message);
        Assert.Contains("probably running as administrator", message);
    }

    [Theory]
    [InlineData(true, "WindowsTerminal", ProcessElevation.Elevated)]   // elevated clipbridge sees everything
    [InlineData(false, "WindowsTerminal", ProcessElevation.NotElevated)]
    [InlineData(false, "mmc", ProcessElevation.Elevated)]              // elevated non-terminal: hook ignores it anyway
    [InlineData(false, null, ProcessElevation.Unreadable)]
    public void Stays_silent_when_the_hook_would_not_have_acted_or_can_see_the_window(
        bool selfElevated, string? processName, ProcessElevation targetElevation)
    {
        Assert.Null(new ElevationWarningPolicy().Evaluate(selfElevated, processName, 42, targetElevation));
    }

    [Fact]
    public void Warns_once_per_terminal_process_not_once_per_focus()
    {
        var policy = new ElevationWarningPolicy();

        Assert.NotNull(policy.Evaluate(false, "WindowsTerminal", 42, ProcessElevation.Elevated));
        Assert.Null(policy.Evaluate(false, "WindowsTerminal", 42, ProcessElevation.Elevated));
        Assert.NotNull(policy.Evaluate(false, "WindowsTerminal", 43, ProcessElevation.Elevated));
    }

    [Fact]
    public void A_non_elevated_sighting_does_not_use_up_the_warning_for_that_pid()
    {
        var policy = new ElevationWarningPolicy();

        Assert.Null(policy.Evaluate(false, "WindowsTerminal", 42, ProcessElevation.NotElevated));
        Assert.NotNull(policy.Evaluate(false, "WindowsTerminal", 42, ProcessElevation.Elevated));
    }
}
