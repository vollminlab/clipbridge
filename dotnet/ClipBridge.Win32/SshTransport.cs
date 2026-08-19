using System.Diagnostics;
using ClipBridge.Core;

namespace ClipBridge.Win32;

public sealed class SshTransport : ISshTransport
{
    // Long enough for a large screenshot over a slow link (measured happy
    // path is ~0.5s); short enough that a wedged transport does not strand
    // the user. Ctrl+V being dead until the process is restarted is worse
    // than any error message, so this bounds every call - see the
    // constructor comment and PasteOrchestrator's "SendPaste exactly once"
    // invariant, which an unbounded wait here would silently violate by
    // never returning at all.
    private const int DefaultTimeoutSeconds = 30;

    private readonly TimeSpan _timeout;

    public SshTransport(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    public SshExecResult Send(string exePath, IReadOnlyList<string> arguments, string stdinFilePath)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exePath}");

        // Start draining stdout AND stderr before writing a single byte of
        // stdin. v1 (PowerShell's `Start-Process -RedirectStandardInput
        // <file>`) was an OS-level redirect - the child read the file
        // directly and no pipe existed to deadlock. This C# port substitutes
        // a pipe plus a manual copy (below), which introduces a failure mode
        // v1 structurally could not have: if stdout/stderr are read
        // sequentially *after* stdin is written, a child that writes freely
        // to stderr (ssh.exe does - banners, "Warning: Permanently added...",
        // verbose auth output) can fill the OS pipe buffer (a few KB) and
        // block on its own stderr write while we are still blocked writing
        // stdin or blocked in a synchronous stdout ReadToEnd(). Neither side
        // then moves. Starting both reads first, concurrently with the
        // write, removes the cycle. See
        // Captures_a_large_stderr_write_without_deadlocking for the
        // regression test this fixes.
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        try
        {
            // FileStream -> StandardInput.BaseStream is a raw byte copy, not
            // a string round-trip anywhere in this path - the byte-fidelity
            // property (arbitrary PNG bytes survive intact) does not depend
            // on the deadlock fix above; the two are independent concerns
            // fixed together here.
            using var fs = File.OpenRead(stdinFilePath);
            fs.CopyTo(process.StandardInput.BaseStream);
        }
        catch (IOException)
        {
            // The child exited (e.g. ssh auth failure) before consuming all
            // of stdin, so the pipe write hit a broken pipe. That is not a
            // transport failure worth throwing for - the child's real exit
            // code and stderr already carry the actual diagnosis, and
            // PasteOrchestrator's contract is a returned non-zero
            // SshExecResult, not an exception, for exactly this class of
            // problem. Fall through to the normal exit/collect path below.
        }
        finally
        {
            process.StandardInput.Close();
        }

        if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
        {
            KillProcessTree(process);
            return new SshExecResult(
                ExitCode: -1,
                StdOut: "",
                StdErr: $"SshTransport: timed out after {_timeout.TotalSeconds:0}s waiting for '{exePath}' to exit");
        }

        // .NET's async Process stream events need the event-based WaitForExit
        // overload to guarantee delivery before this point in some edge
        // cases; here we already blocked above on WaitForExit(int), and the
        // read tasks were started well before the process could exit, so
        // awaiting them here just collects buffers that are either already
        // complete or about to be (the pipe closes when the child exits).
        var stdOut = stdOutTask.GetAwaiter().GetResult();
        var stdErr = stdErrTask.GetAwaiter().GetResult();

        return new SshExecResult(process.ExitCode, stdOut, stdErr);
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the WaitForExit timeout and this call.
        }
    }
}
