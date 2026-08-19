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
    //
    // INVARIANT: _pasteSink.SendPaste() is called exactly once per Handle()
    // call - never zero times, never twice - on every path, including
    // paths where something throws that this method did not anticipate.
    // An early version of this method (see git history / project notes for
    // "Task 11 Step 4") wrapped only the failure modes it expected
    // (clipboard read failure, bad config, non-zero ssh exit, an unusable
    // returned path) and left everything else - SshArgumentBuilder.Build,
    // ISshTransport.Send, IClipboard.Capture/SetPathText/Restore - entirely
    // unguarded. Any exception from those left Ctrl+V dead: the exception
    // propagated out of Handle() before SendPaste() was ever called. Six
    // regression tests in PasteOrchestratorInvariantProbeTests.cs proved
    // this concretely before this version existed; they are now the
    // permanent regression suite for this invariant.
    public PasteAttemptResult Handle(bool forced)
    {
        // `pasted` is a per-call local, not a field: this instance is
        // constructed once and Handle() is invoked once per hotkey press,
        // so a field would only allow the very first call to ever paste.
        var pasted = false;
        string? tmpPng = null;

        // Deliberately idempotent: guarded by `pasted`, so calling this a
        // second time on the same Handle() invocation (e.g. from the
        // catch-all backstop below, after some earlier branch already
        // pasted) is a safe no-op rather than a second real paste.
        //
        // `pasted` is set to true BEFORE invoking the sink, not after. If
        // _pasteSink.SendPaste() itself throws (a broken paste sink -
        // there is genuinely nothing left to do about that), the flag
        // still records that a paste was attempted exactly once, so nobody
        // downstream retries it and turns one failure into a double paste.
        // That exception is deliberately NOT caught here or by the
        // catch-all below (see the `when (!pasted)` filter) - a paste sink
        // that throws is exactly the condition Step 5 says must not be
        // silently swallowed, so it is left to propagate out of Handle().
        void EnsurePasted()
        {
            if (pasted) return;
            pasted = true;
            _pasteSink.SendPaste();
        }

        try
        {
            byte[]? png;
            try
            {
                png = _clipboard.TryGetPng();
            }
            catch (Exception ex)
            {
                SafeLog($"cannot read clipboard image - {ex.Message}");
                EnsurePasted();
                return new PasteAttemptResult(PasteOutcome.Failed, null, ex.Message);
            }

            if (png is null)
            {
                EnsurePasted();
                return new PasteAttemptResult(PasteOutcome.NoImageNoOp, null, null);
            }

            tmpPng = Path.Combine(Path.GetTempPath(), $"clipbridge-{Guid.NewGuid():N}.png");
            try
            {
                File.WriteAllBytes(tmpPng, png);
            }
            catch (Exception ex)
            {
                SafeLog($"cannot write local temp file {tmpPng} - {ex.Message}");
                EnsurePasted();
                return new PasteAttemptResult(PasteOutcome.Failed, null, ex.Message);
            }

            ClipbridgeConfig cfg;
            try
            {
                cfg = _configProvider();
            }
            catch (Exception ex)
            {
                SafeLog($"configuration problem: {ex.Message}");
                EnsurePasted();
                return new PasteAttemptResult(PasteOutcome.Failed, null, ex.Message);
            }

            // Everything from here down - argument building, the ssh call
            // itself, and the clipboard writeback - has no local try/catch
            // of its own (Restore is the one exception, see below). Any
            // throw here falls through to the catch-all at the bottom of
            // this method, which is the backstop the class invariant
            // depends on.
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
                SafeLog(reason);
                EnsurePasted();
                return new PasteAttemptResult(PasteOutcome.Failed, null, reason);
            }

            var resolved = RemotePathResolver.Resolve(result.StdOut);
            if (resolved.Path is null)
            {
                SafeLog(resolved.Reason!);
                EnsurePasted();
                return new PasteAttemptResult(PasteOutcome.Failed, null, resolved.Reason);
            }

            var snapshot = _clipboard.Capture();
            _clipboard.SetPathText(resolved.Path);
            EnsurePasted();
            try
            {
                _clipboard.Restore(snapshot);
            }
            catch (Exception ex)
            {
                // The paste has already happened by this point. A failed
                // restore is a real but strictly lesser problem than a
                // dead Ctrl+V - the user's clipboard is left holding the
                // synthetic remote path text instead of whatever they had
                // before - so it is logged and does not change the
                // outcome or retry the paste.
                SafeLog($"failed to restore original clipboard contents - {ex.Message}");
            }
            return new PasteAttemptResult(PasteOutcome.Pasted, resolved.Path, null);
        }
        catch (Exception ex) when (!pasted)
        {
            // Deliberately broad, and deliberately gated on `!pasted`.
            // This is the backstop for every failure mode this method
            // does not name explicitly above - SshArgumentBuilder.Build
            // throwing on a config value it wasn't expecting,
            // ISshTransport.Send throwing instead of returning a non-zero
            // exit code, IClipboard.Capture/SetPathText throwing (a locked
            // clipboard is a real, common Windows condition, not an
            // exotic one). The whole point of this class is that ANYTHING
            // going wrong still ends in a paste - see the class invariant
            // above. The `when (!pasted)` filter is what keeps this from
            // also catching (and thereby hiding) an exception thrown by
            // _pasteSink.SendPaste() itself from inside EnsurePasted():
            // once `pasted` is true a paste was already attempted, and a
            // broken paste sink is a failure this method cannot paper
            // over, so that exception is left to propagate instead.
            SafeLog($"unexpected error in paste orchestration - {ex.Message}");
            EnsurePasted();
            return new PasteAttemptResult(PasteOutcome.Failed, null, ex.Message);
        }
        finally
        {
            if (tmpPng is not null)
            {
                try { File.Delete(tmpPng); } catch { /* best effort, matches v1's -ErrorAction SilentlyContinue */ }
            }
        }
    }

    // Logging must never be able to prevent a paste. ClipbridgeLogger.Append
    // does real file I/O and has independently been shown to throw
    // UnauthorizedAccessException against a read-only log file; a caller
    // may also supply an arbitrary `log` delegate that throws for any
    // reason. Either way, the paste that follows every SafeLog() call in
    // this class must still happen.
    private void SafeLog(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Deliberately swallowed - see the method comment above. There
            // is no secondary logging channel to fall back to here, and
            // failing to log a diagnostic is a strictly smaller problem
            // than failing to paste.
        }
    }
}
