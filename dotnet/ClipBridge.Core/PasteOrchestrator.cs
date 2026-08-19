namespace ClipBridge.Core;

public sealed class PasteOrchestrator
{
    private readonly IClipboard _clipboard;
    private readonly IPasteSink _pasteSink;
    private readonly ISshTransport _sshTransport;
    private readonly Func<ClipbridgeConfig> _configProvider;
    private readonly string _configDir;
    private readonly Action<string> _log;

    public PasteOrchestrator(
        IClipboard clipboard,
        IPasteSink pasteSink,
        ISshTransport sshTransport,
        Func<ClipbridgeConfig> configProvider,
        string configDir,
        Action<string>? log = null)
    {
        _clipboard = clipboard;
        _pasteSink = pasteSink;
        _sshTransport = sshTransport;
        _configProvider = configProvider;
        _configDir = configDir;
        _log = log ?? (msg => ClipbridgeLogger.Append(_configDir, msg));
    }

    // Called from the worker thread AFTER the keyboard hook has already
    // swallowed the keystroke (see ClipBridge.Win32.KeyboardHook, Task 17).
    // Every return path from here on must end in a synthesized paste except
    // NoImageNoOp, which mirrors Send-Clip.ps1 exit 2 - "not treated as an
    // error, nothing is logged" - and is reachable here only via a forced
    // Ctrl+Shift+V against a text clipboard (a plain Ctrl+V never swallows
    // without an image present in the first place, per HotkeyDecision).
    public PasteAttemptResult Handle(bool forced)
    {
        byte[]? png;
        try
        {
            png = _clipboard.TryGetPng();
        }
        catch (Exception ex)
        {
            _log($"cannot read clipboard image - {ex.Message}");
            _pasteSink.SendPaste();
            return new PasteAttemptResult(PasteOutcome.Failed, null, ex.Message);
        }

        if (png is null)
        {
            _pasteSink.SendPaste();
            return new PasteAttemptResult(PasteOutcome.NoImageNoOp, null, null);
        }

        var tmpPng = Path.Combine(Path.GetTempPath(), $"clipbridge-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(tmpPng, png);
        }
        catch (Exception ex)
        {
            _log($"cannot write local temp file {tmpPng} - {ex.Message}");
            _pasteSink.SendPaste();
            return new PasteAttemptResult(PasteOutcome.Failed, null, ex.Message);
        }

        try
        {
            ClipbridgeConfig cfg;
            try
            {
                cfg = _configProvider();
            }
            catch (Exception ex)
            {
                _log($"configuration problem: {ex.Message}");
                _pasteSink.SendPaste();
                return new PasteAttemptResult(PasteOutcome.Failed, null, ex.Message);
            }

            var inv = SshArgumentBuilder.Build(cfg.Transport, cfg.SshHost);
            var result = _sshTransport.Send(inv.Exe, inv.Arguments, tmpPng);

            if (result.ExitCode != 0)
            {
                // 3 and 5 are clipbridge-recv's own codes; anything else is
                // a transport/auth failure. The process-level outcome is
                // the same either way (Failed + a synthesized paste), but
                // naming the bucket in the log keeps debugging pointed at
                // the right machine - see docs/clipbridge-architecture.md's
                // exit-code table for why that distinction was added.
                var reason = result.ExitCode switch
                {
                    3 => $"remote rejected input (clipbridge-recv exit 3): {result.StdErr}",
                    5 => $"remote cannot write (clipbridge-recv exit 5): {result.StdErr}",
                    _ => $"ssh exit {result.ExitCode} (transport failure): {result.StdErr}",
                };
                _log(reason);
                _pasteSink.SendPaste();
                return new PasteAttemptResult(PasteOutcome.Failed, null, reason);
            }

            var resolved = RemotePathResolver.Resolve(result.StdOut);
            if (resolved.Path is null)
            {
                _log(resolved.Reason!);
                _pasteSink.SendPaste();
                return new PasteAttemptResult(PasteOutcome.Failed, null, resolved.Reason);
            }

            var snapshot = _clipboard.Capture();
            _clipboard.SetPathText(resolved.Path);
            _pasteSink.SendPaste();
            _clipboard.Restore(snapshot);
            return new PasteAttemptResult(PasteOutcome.Pasted, resolved.Path, null);
        }
        finally
        {
            try { File.Delete(tmpPng); } catch { /* best effort, matches v1's -ErrorAction SilentlyContinue */ }
        }
    }
}
