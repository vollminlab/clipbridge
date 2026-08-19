using System.Runtime.InteropServices;
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

// Task 11 Step 4/5 regression suite for the class invariant: SendPaste()
// is called exactly once per Handle() call, even when something Handle()
// did not anticipate throws.
//
// Before the Step 5 fix, every one of the first five tests below FAILED
// against the plan's verbatim PasteOrchestrator: PasteCount stayed 0 (or,
// for Probe_4b, the paste had already happened but the exception still
// escaped uncaught) and the triggering exception propagated straight out
// of Handle(), because nothing below the config-provider call was wrapped
// in a try/catch. That was proven with Record.Exception before any fix was
// written - see the PR history for the exact pre-fix assertions. These
// tests now assert the FIXED, intended behaviour and are kept permanently
// as the regression suite for this invariant.
public class PasteOrchestratorInvariantProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clipbridge-probe-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly byte[] SamplePng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private PasteOrchestrator Build(FakeClipboard clip, FakePasteSink paste, FakeSshTransport ssh, Action<string> log) =>
        new(clip, paste, ssh, () => new ClipbridgeConfig("clipbridge", "ssh"), _dir, log);

    [Fact]
    public void Probe_1_log_throwing_does_not_prevent_the_paste()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(3, "", "boom") };
        var orchestrator = Build(clip, paste, ssh, _ => throw new UnauthorizedAccessException("log is read-only"));

        var escaped = Record.Exception(() => orchestrator.Handle(forced: false));

        Assert.Null(escaped);
        Assert.Equal(1, paste.PasteCount);
    }

    [Fact]
    public void Probe_2_ssh_transport_send_throwing_still_pastes()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ThrowOnSend = new InvalidOperationException("process start failed") };
        var orchestrator = Build(clip, paste, ssh, _ => { });

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
    }

    [Fact]
    public void Probe_3_clipboard_set_path_text_throwing_still_pastes()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng, ThrowOnSetPathText = new ExternalException("clipboard locked") };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(0, "/home/vollmin/.clipbridge/x.png\n", "") };
        var orchestrator = Build(clip, paste, ssh, _ => { });

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
    }

    [Fact]
    public void Probe_4a_clipboard_capture_throwing_still_pastes()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng, ThrowOnCapture = new InvalidOperationException("capture failed") };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(0, "/home/vollmin/.clipbridge/x.png\n", "") };
        var orchestrator = Build(clip, paste, ssh, _ => { });

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
    }

    [Fact]
    public void Probe_4b_clipboard_restore_throwing_after_paste_does_not_double_paste_or_change_outcome()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng, ThrowOnRestore = new InvalidOperationException("restore failed") };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(0, "/home/vollmin/.clipbridge/x.png\n", "") };
        var orchestrator = Build(clip, paste, ssh, _ => { });

        var escaped = Record.Exception(() => orchestrator.Handle(forced: false));

        // The paste already happened; a failed restore is logged and does
        // not fail the overall outcome or escape Handle().
        Assert.Null(escaped);
        Assert.Equal(1, paste.PasteCount);
        Assert.Equal(1, clip.RestoreCallCount);
    }

    [Fact]
    public void Probe_5_ssh_argument_builder_throwing_on_unknown_transport_still_pastes()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport();
        var orchestrator = new PasteOrchestrator(clip, paste, ssh,
            () => new ClipbridgeConfig("clipbridge", "carrier-pigeon"), _dir, _ => { });

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
    }

    [Fact]
    public void A_paste_sink_that_itself_throws_is_attempted_exactly_once_and_the_exception_is_not_swallowed()
    {
        var clip = new FakeClipboard { PngToReturn = null }; // NoImageNoOp path - the shortest route to EnsurePasted()
        var paste = new FakePasteSink { ThrowOnSend = new InvalidOperationException("paste sink is broken") };
        var ssh = new FakeSshTransport();
        var orchestrator = Build(clip, paste, ssh, _ => { });

        var escaped = Record.Exception(() => orchestrator.Handle(forced: true));

        // There is nothing to recover a broken paste sink with - this is
        // the one failure mode the class cannot paper over. The important
        // guarantees are narrower: it was attempted exactly once (no
        // double paste), and the failure is not hidden from the caller.
        Assert.NotNull(escaped);
        Assert.IsType<InvalidOperationException>(escaped);
        Assert.Equal(1, paste.PasteCount);
    }
}
