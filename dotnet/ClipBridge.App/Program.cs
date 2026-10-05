using ClipBridge.Core;
using ClipBridge.Win32;

namespace ClipBridge.App;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--install"))
        {
            // No-op when there is no parent console (double-clicked); the install
            // still runs, its output just has nowhere to go.
            NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
            return InstallCommand.Run(Console.Out);
        }

        if (args.Contains("--uninstall"))
        {
            return Uninstall(quiet: args.Contains("--quiet"));
        }

        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clipbridge");
        Directory.CreateDirectory(configDir);

        Registration.Register();

        var clipboard = new Win32Clipboard();
        var pasteSink = new Win32PasteSink();
        var sshTransport = new SshTransport();
        var foregroundWindow = new Win32ForegroundWindow();

        var orchestrator = new PasteOrchestrator(
            clipboard, pasteSink, sshTransport,
            () => ClipbridgeConfigReader.Load(configDir),
            configDir);

        using var workerThread = new SingleThreadDispatcher();
        var hook = new KeyboardHook(foregroundWindow, clipboard, workerThread.Post);

        // onReinstall runs on the message-pump thread (WndProc), so it must
        // never do the installer's own work inline: InstallCommand.Run does
        // two ssh.exe probes at ConnectTimeout=5 each, which would block
        // this thread for seconds. While blocked, no messages are
        // dispatched - not the tray's own menu, and not the low-level
        // keyboard hook either, since HookCallback also runs via this same
        // pump. Typing would stall system-wide for the duration, and if the
        // block runs past Windows' LowLevelHooksTimeout (5s default) the
        // hook gets silently unhooked. Posting to the existing worker
        // thread (the same one PasteOrchestrator.Handle already runs on)
        // keeps the pump free the whole time.
        //
        // onExit does not have this problem: PostQuitMessage is a single
        // non-blocking call that just queues WM_QUIT for this same pump to
        // pick up on its next iteration - nothing to move off-thread.
        using var tray = new TrayIcon(
            Path.Combine(configDir, "clipbridge.log"),
            onExit: () => NativeMethods.PostQuitMessage(0),
            onReinstall: () => workerThread.Post(() => InstallCommand.Run(new LogWriter(configDir))),
            // Runs on WndProc (the message-pump thread) via WM_APP_REHOOK -
            // see the watchdog Timer below for why this indirection exists
            // and TrayIcon.RequestRehook for the mechanism.
            onRehook: () => hook.Rehook());

        // Created (and its window handle live) before the watchdog Timer
        // below is constructed, so RequestRehook always has a real _hwnd to
        // post to. The Timer's dueTime is 5 minutes regardless, so this
        // ordering isn't strictly required to avoid a race today - it's
        // kept explicit so nobody can shorten dueTime later without also
        // re-discovering this dependency the hard way.
        tray.Create();

        hook.PasteRequested += forced =>
        {
            var result = orchestrator.Handle(forced);
            NotifyResult(result);
        };
        hook.Start();

        // Watchdog: re-arms unconditionally every 5 minutes rather than
        // trying to detect a silent unhook (see Task 17, constraint 5 -
        // detecting the drop isn't directly queryable, and re-hooking an
        // already-active hook is cheap and idempotent).
        //
        // Callback posts to the tray window instead of calling
        // hook.Rehook() directly. System.Threading.Timer callbacks run on a
        // ThreadPool thread, and SetWindowsHookExW's docs are explicit that
        // a low-level hook "can be called on the thread that installed the
        // hook" and that "the hooking application must continue to pump
        // messages" on that thread. A thread-pool thread never pumps
        // messages, so a hook re-installed there would be permanently
        // undeliverable - five minutes after every startup, clipbridge
        // would silently stop responding to Ctrl+V forever, which is
        // exactly the failure this watchdog exists to prevent. Routing
        // through TrayIcon.RequestRehook (PostMessageW, non-blocking,
        // thread-safe) makes the actual Rehook() call happen on WndProc,
        // which runs on the real message-pump thread below.
        using var watchdog = new Timer(_ => tray.RequestRehook(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        // Elevation watch: polls the foreground window every 2s and warns
        // (log + balloon, once per terminal process) when Windows Terminal is
        // elevated and clipbridge is not. UIPI hides that window's keystrokes
        // from the hook, so Ctrl+V there pastes nothing and nothing is logged;
        // the hook cannot report a keystroke it is never shown, which is why
        // this polls instead. Found 2026-10-04 after a month of silent failure.
        //
        // Thread-pool timer, deliberately not the pump: OpenProcess and the
        // log write must never block the thread servicing the hook. Every
        // exception is caught because an unhandled one on a thread-pool
        // thread terminates the process - and with it the user's Ctrl+V.
        var selfElevated = ProcessElevationProbe.IsCurrentProcessElevated();
        var elevationPolicy = new ElevationWarningPolicy();
        var elevationCheckRunning = 0;
        string? lastElevationError = null;
        using var elevationWatch = new Timer(_ =>
        {
            // Skip a tick rather than overlap: the policy is not thread-safe.
            if (Interlocked.Exchange(ref elevationCheckRunning, 1) == 1) return;
            try
            {
                if (!ProcessElevationProbe.TryGetForeground(out var pid, out var name, out var elevation)) return;
                var warning = elevationPolicy.Evaluate(selfElevated, name, pid, elevation);
                if (warning is null) return;
                ClipbridgeLogger.Append(configDir, warning);
                if (!tray.ShowWarning("clipbridge can't see this terminal", warning))
                {
                    ClipbridgeLogger.Append(configDir, "elevation warning balloon was not shown (Shell_NotifyIcon returned FALSE)");
                }
            }
            catch (Exception ex)
            {
                // Losing one tick is harmless, but say why - and only when the
                // reason changes, or a persistent fault writes a line every 2s.
                if (ex.Message != lastElevationError)
                {
                    lastElevationError = ex.Message;
                    try { ClipbridgeLogger.Append(configDir, $"elevation check failed - {ex.Message}"); } catch (Exception) { }
                }
            }
            finally
            {
                Volatile.Write(ref elevationCheckRunning, 0);
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        // Raw Win32 message pump - required for the low-level hook AND the
        // tray window's WndProc to receive messages. No
        // System.Windows.Forms.Application.Run: everything here is raw
        // Win32, consistent with the rest of this project.
        //
        // `> 0` is load-bearing, not `while (GetMessageW(...))`: GetMessage
        // returns -1 on error, and a bare truthiness check on a nonzero
        // return value would treat -1 as "keep pumping" and spin forever.
        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        hook.Dispose();
        return 0;
    }

    // Tones mirror clipbridge.ahk's SoundBeep calls exactly, so the audible
    // feedback a user has already learned carries over unchanged: 900Hz on
    // success, the two-tone 600/400Hz on "nothing to do", 300Hz on failure.
    private static void NotifyResult(PasteAttemptResult result)
    {
        switch (result.Outcome)
        {
            case PasteOutcome.Pasted:
                NativeMethods.Beep(900, 60);
                break;
            case PasteOutcome.NoImageNoOp:
                NativeMethods.Beep(600, 80);
                NativeMethods.Beep(400, 80);
                break;
            case PasteOutcome.Failed:
                NativeMethods.Beep(300, 200);
                break;
        }
    }

    // Reinstall from the tray used to write to TextWriter.Null, so it ran
    // correctly and reported absolutely nothing - indistinguishable from a menu
    // item that does not work. There is no console to print to (WinExe) and a
    // dialog would need a UI this design deliberately does not have, so its
    // progress goes where every other diagnostic already goes: the log.
    private sealed class LogWriter(string configDir) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ClipbridgeLogger.Append(configDir, $"reinstall: {value}");
            }
        }
    }

    // Run from a terminal, the result prints there like --install. Run from
    // Settings > Apps there is no parent console, so without a dialog the
    // uninstall would finish (or fail) in total silence. --quiet (the
    // registered QuietUninstallString, and CI) suppresses the dialog, which
    // would otherwise block an unattended run forever.
    private static int Uninstall(bool quiet)
    {
        var hasConsole = NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        var report = new StringWriter();
        var code = UninstallCommand.Run(report);
        Console.Out.Write(report.ToString());
        Console.Out.Flush();

        if (!hasConsole && !quiet)
        {
            NativeMethods.MessageBoxW(IntPtr.Zero, report.ToString(), "clipbridge",
                code == 0 ? NativeMethods.MB_ICONINFORMATION : NativeMethods.MB_ICONWARNING);
        }
        return code;
    }
}
