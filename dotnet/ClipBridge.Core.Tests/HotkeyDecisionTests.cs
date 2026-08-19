using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class HotkeyDecisionTests
{
    // Mirrors the design spec's 3-step hook callback decision exactly:
    // 1. not Ctrl+V, or foreground process not a configured terminal -> pass through
    // 2. unforced Ctrl+V with no image available -> pass through (the common,
    //    must-stay-instant case)
    // 3. otherwise -> swallow
    [Theory]
    [InlineData(true, true, true, false, true, true)]    // plain Ctrl+V, terminal, image present -> swallow
    [InlineData(true, true, true, false, false, false)]  // plain Ctrl+V, terminal, no image -> pass through
    [InlineData(true, true, true, true, false, true)]    // forced Ctrl+Shift+V, terminal, no image -> still swallow
    [InlineData(true, true, false, false, true, false)]  // not in a terminal -> pass through regardless of image
    [InlineData(false, true, true, false, true, false)]  // Ctrl not down -> pass through
    // Permanent additions from the Step 5 truth-table probe (32 combinations checked;
    // only 3 swallow). These two encode real safety properties, not just coverage:
    [InlineData(true, true, false, true, true, false)]   // forced binding outside a terminal, image present -> MUST pass through.
                                                           // A forced Ctrl+Shift+V that escapes terminal scope steals
                                                           // paste-as-plain-text in every browser/editor - this was the v1 bug.
    [InlineData(true, true, false, true, false, false)]  // same, but with no image either -> still must pass through
    [InlineData(true, false, true, true, true, false)]   // V key not actually down, everything else true -> pass through.
                                                           // Guards against a vkey-code mixup swallowing unrelated Ctrl+Shift+<key> combos.
    public void Matches_the_three_step_decision_in_the_design_spec(
        bool ctrlDown, bool isVKeyDown, bool inTerminal, bool forced, bool clipboardHasImage, bool expectedSwallow)
    {
        Assert.Equal(expectedSwallow, HotkeyDecision.ShouldSwallow(ctrlDown, isVKeyDown, inTerminal, forced, clipboardHasImage));
    }

    [Fact]
    public void Terminal_check_is_an_exact_process_name_match()
    {
        var terminals = new[] { "WindowsTerminal" };
        Assert.True(HotkeyDecision.IsForegroundTerminal("WindowsTerminal", terminals));
        Assert.False(HotkeyDecision.IsForegroundTerminal("notepad", terminals));
        Assert.False(HotkeyDecision.IsForegroundTerminal(null, terminals));
    }

    // Real-world config-file trap: Process.GetProcessById(...).ProcessName never
    // includes ".exe", but a human editing the config plausibly writes one anyway.
    // If that silently never matches, the user's paste hook goes dark with no error
    // at all - the most surprising finding from the Step 5 probe.
    [Fact]
    public void Exe_suffix_in_configured_terminal_name_never_matches()
    {
        var terminals = new[] { "WindowsTerminal.exe" };
        Assert.False(HotkeyDecision.IsForegroundTerminal("WindowsTerminal", terminals));
    }

    [Fact]
    public void Matching_is_case_sensitive()
    {
        var terminals = new[] { "WindowsTerminal" };
        Assert.False(HotkeyDecision.IsForegroundTerminal("windowsterminal", terminals));
    }

    [Fact]
    public void Matching_does_not_trim_whitespace()
    {
        var terminals = new[] { "WindowsTerminal" };
        Assert.False(HotkeyDecision.IsForegroundTerminal(" WindowsTerminal ", terminals));
    }

    [Fact]
    public void Empty_configured_list_never_matches()
    {
        Assert.False(HotkeyDecision.IsForegroundTerminal("WindowsTerminal", System.Array.Empty<string>()));
    }

    [Fact]
    public void Default_clipboard_snapshot_has_null_formats_dictionary()
    {
        // Contract check for the Win32 tasks (13+): a default-constructed
        // ClipboardSnapshot does NOT give an empty dictionary - it gives null.
        // If Restore() ever foreach's FormatsToData without a null check, a
        // default(ClipboardSnapshot) input will NRE.
        var snapshot = default(ClipboardSnapshot);
        Assert.Null(snapshot.FormatsToData);
    }
}
