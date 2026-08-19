using System.Runtime.InteropServices;
using ClipBridge.Core;

namespace ClipBridge.Win32;

public sealed class Win32PasteSink : IPasteSink
{
    // v1 (windows/clipbridge.ahk lines 131-133) did `Send("^v")` followed by
    // `Sleep(200)` before restoring the clipboard, with the comment "let the
    // target consume the paste before restoring". SendInput only queues
    // input - it returns as soon as the events are accepted by the input
    // queue, not once the target application has actually processed them.
    // Without this wait, PasteOrchestrator.Handle's immediate
    // _clipboard.Restore(snapshot) races the target's read of the clipboard:
    // the original clipboard contents can land back before Ctrl+V has been
    // delivered, so the target pastes the user's previous clipboard instead
    // of the remote image path. That is exactly the v1 behaviour this class
    // exists to reproduce, so the wait belongs here rather than being
    // dropped as an unexplained port gap.
    private const int PostSendSettleDelayMs = 200;

    public void SendPaste()
    {
        var inputs = new[]
        {
            KeyDown(NativeMethods.VK_CONTROL),
            KeyDown(NativeMethods.VK_V),
            KeyUp(NativeMethods.VK_V),
            KeyUp(NativeMethods.VK_CONTROL),
        };
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"SendInput sent only {sent}/{inputs.Length} events (GetLastError={error})");
        }

        Thread.Sleep(PostSendSettleDelayMs);
    }

    private static NativeMethods.INPUT KeyDown(ushort vk) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk } },
    };

    private static NativeMethods.INPUT KeyUp(ushort vk) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = NativeMethods.KEYEVENTF_KEYUP } },
    };
}
