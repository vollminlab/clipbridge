namespace ClipBridge.Core;

public interface IClipboard
{
    bool HasImageAvailable();
    byte[]? TryGetPng();
    ClipboardSnapshot Capture();
    void Restore(ClipboardSnapshot snapshot);
    void SetPathText(string path);
}

public readonly record struct ClipboardSnapshot(bool HadData, IReadOnlyDictionary<uint, byte[]> FormatsToData);

public interface IPasteSink
{
    void SendPaste();
}

public interface IForegroundWindow
{
    string? GetForegroundProcessName();
}

public interface ISshTransport
{
    SshExecResult Send(string exePath, IReadOnlyList<string> arguments, string stdinFilePath);
}

public readonly record struct SshExecResult(int ExitCode, string StdOut, string StdErr);

public interface IKeyboardHook : IDisposable
{
    event Action<bool>? PasteRequested; // payload: forced (Ctrl+Shift+V)
    void Start();
    void Rehook();
}

public enum PasteOutcome { Pasted, NoImageNoOp, Failed }

public sealed record PasteAttemptResult(PasteOutcome Outcome, string? RemotePath, string? LogMessage);
