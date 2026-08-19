using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class PasteOrchestratorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clipbridge-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly byte[] SamplePng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private PasteOrchestrator Build(FakeClipboard clip, FakePasteSink paste, FakeSshTransport ssh) =>
        new(clip, paste, ssh, () => new ClipbridgeConfig("clipbridge", "ssh"), _dir);

    [Fact]
    public void No_image_on_clipboard_pastes_without_calling_ssh()
    {
        var clip = new FakeClipboard { PngToReturn = null };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport();
        var orchestrator = Build(clip, paste, ssh);

        var result = orchestrator.Handle(forced: true);

        Assert.Equal(PasteOutcome.NoImageNoOp, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
        Assert.Null(ssh.LastCall);
    }

    [Fact]
    public void Successful_transfer_sets_clipboard_pastes_and_restores()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(0, "/home/vollmin/.clipbridge/20260819-1.png\n", "") };
        var orchestrator = Build(clip, paste, ssh);

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Pasted, result.Outcome);
        Assert.Equal("/home/vollmin/.clipbridge/20260819-1.png", result.RemotePath);
        Assert.Equal(new[] { "/home/vollmin/.clipbridge/20260819-1.png" }, clip.PathTextsSet);
        Assert.Equal(1, paste.PasteCount);
        Assert.Equal(1, clip.RestoreCallCount);
    }

    [Theory]
    [InlineData(3, "remote rejected")]
    [InlineData(5, "remote cannot write")]
    [InlineData(255, "transport failure")]
    public void Every_ssh_failure_still_synthesizes_a_paste(int exitCode, string expectedFragment)
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(exitCode, "", "boom") };
        var orchestrator = Build(clip, paste, ssh);

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
        Assert.Contains(expectedFragment, result.LogMessage);
        Assert.Empty(clip.PathTextsSet); // never overwrote the clipboard on failure
    }

    [Fact]
    public void An_unusable_returned_path_still_synthesizes_a_paste()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(0, "not/absolute\n", "") };
        var orchestrator = Build(clip, paste, ssh);

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
    }

    [Fact]
    public void A_clipboard_read_exception_still_synthesizes_a_paste()
    {
        var clip = new FakeClipboard { ThrowOnGet = new IOException("disk full") };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport();
        var orchestrator = Build(clip, paste, ssh);

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
    }

    [Fact]
    public void A_broken_config_still_synthesizes_a_paste()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport();
        var orchestrator = new PasteOrchestrator(clip, paste, ssh,
            () => throw new ClipbridgeConfigException("config not found"), _dir);

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
        Assert.Null(ssh.LastCall);
    }

    [Fact]
    public void Cleans_up_the_temp_png_regardless_of_outcome()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(4, "", "connection refused") };
        var orchestrator = Build(clip, paste, ssh);

        orchestrator.Handle(forced: false);

        Assert.NotNull(ssh.LastCall);
        Assert.False(File.Exists(ssh.LastCall!.Value.StdinFile), "temp PNG must be deleted after use, success or failure");
    }
}
