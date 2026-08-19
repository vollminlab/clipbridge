using ClipBridge.Core;

namespace ClipBridge.Core.Tests;

internal sealed class FakeClipboard : IClipboard
{
    public byte[]? PngToReturn;
    public Exception? ThrowOnGet;
    public Exception? ThrowOnCapture;
    public Exception? ThrowOnSetPathText;
    public Exception? ThrowOnRestore;
    public List<string> PathTextsSet = new();
    public int RestoreCallCount;
    public int CaptureCallCount;

    public bool HasImageAvailable() => PngToReturn is not null;

    public byte[]? TryGetPng()
    {
        if (ThrowOnGet is not null) throw ThrowOnGet;
        return PngToReturn;
    }

    public ClipboardSnapshot Capture()
    {
        CaptureCallCount++;
        if (ThrowOnCapture is not null) throw ThrowOnCapture;
        return new ClipboardSnapshot(true, new Dictionary<uint, byte[]>());
    }

    public void Restore(ClipboardSnapshot snapshot)
    {
        RestoreCallCount++;
        if (ThrowOnRestore is not null) throw ThrowOnRestore;
    }

    public void SetPathText(string path)
    {
        if (ThrowOnSetPathText is not null) throw ThrowOnSetPathText;
        PathTextsSet.Add(path);
    }
}

internal sealed class FakePasteSink : IPasteSink
{
    public int PasteCount;
    public void SendPaste() => PasteCount++;
}

internal sealed class FakeSshTransport : ISshTransport
{
    public SshExecResult ResultToReturn;
    public Exception? ThrowOnSend;
    public (string Exe, IReadOnlyList<string> Arguments, string StdinFile)? LastCall;

    public SshExecResult Send(string exePath, IReadOnlyList<string> arguments, string stdinFilePath)
    {
        LastCall = (exePath, arguments, stdinFilePath);
        if (ThrowOnSend is not null) throw ThrowOnSend;
        return ResultToReturn;
    }
}
