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

        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clipbridge");
        Directory.CreateDirectory(configDir);

        RegisterStartup();

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
            onReinstall: () => workerThread.Post(() => InstallCommand.Run(TextWriter.Null)),
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

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Registry Run key, not a Startup-folder .lnk shortcut (design decision
    // #6) - one file, no shortcut to keep in sync with the exe's path.
    private static void RegisterStartup()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("could not determine exe path");
        key.SetValue("clipbridge", $"\"{exePath}\"");
    }
}
