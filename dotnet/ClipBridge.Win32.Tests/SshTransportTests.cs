using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class SshTransportTests
{
    // These three tests are about stdout/exit-code capture, byte fidelity and
    // deadlock-freedom - not about the timeout, which has its own test below.
    // They pass an explicit, generous timeout so they cannot fail for the wrong
    // reason: a GitHub-hosted runner measured ~14x slower than a healthy one on
    // 2026-08-19 turned a normally-2s case into a 30s timeout failure. Binding a
    // behaviour test to the production default silently asserts "CI is never
    // slow", which is not a property we want to test or rely on.
    private static SshTransport NewTransport() => new(timeout: TimeSpan.FromMinutes(3));

    [WindowsFact]
    public void Captures_exit_code_and_stdout()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        try
        {
            var transport = NewTransport();
            var result = transport.Send("powershell.exe",
                new[] { "-NoProfile", "-Command", "$null = $input; Write-Output 'ok'" },
                tmp);

            Assert.True(result.ExitCode == 0,
                $"expected exit 0, got {result.ExitCode}; stderr={result.StdErr}");
            Assert.Contains("ok", result.StdOut);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [WindowsFact]
    public void Sends_the_exact_bytes_from_the_stdin_file_no_string_conversion_anywhere()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, payload);
        try
        {
            var transport = NewTransport();
            // Echo stdin back as base64 on stdout so the comparison never
            // depends on a text encoding assumption anywhere in the pipe.
            var result = transport.Send("powershell.exe",
                new[]
                {
                    "-NoProfile", "-Command",
                    "$ms = New-Object System.IO.MemoryStream; " +
                    "[Console]::OpenStandardInput().CopyTo($ms); " +
                    "[Console]::Out.Write([Convert]::ToBase64String($ms.ToArray()))",
                },
                tmp);

            var roundTripped = Convert.FromBase64String(result.StdOut.Trim());
            Assert.Equal(payload, roundTripped);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [WindowsFact]
    public void Times_out_instead_of_hanging_forever_when_the_child_never_exits()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
        try
        {
            // Short timeout so the test itself completes quickly - the
            // production default (30s) is exercised only implicitly by the
            // other tests here, all of which finish in well under a second.
            var transport = new SshTransport(timeout: TimeSpan.FromSeconds(2));

            var started = DateTime.UtcNow;
            var result = transport.Send("powershell.exe",
                new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" },
                tmp);
            var elapsed = DateTime.UtcNow - started;

            Assert.True(elapsed < TimeSpan.FromSeconds(10), $"expected an early return, took {elapsed}");
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("time", result.StdErr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [WindowsFact]
    public void Captures_a_large_stderr_write_without_deadlocking()
    {
        // Deadlock regression test. Against the plan's original ordering -
        // write all of stdin, THEN ReadToEnd() stdout, THEN ReadToEnd()
        // stderr - a child that writes more than the OS pipe buffer (a few
        // KB) to stderr before we ever start draining it blocks on its own
        // stderr write while we're still blocked writing stdin/reading
        // stdout. Neither side moves and this test hangs. With both reads
        // started asynchronously before stdin is written, it completes
        // quickly.
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
        try
        {
            var transport = NewTransport();
            var result = transport.Send("powershell.exe",
                new[]
                {
                    "-NoProfile", "-Command",
                    "$null = $input; " +
                    "$e = [Console]::Error; " +
                    "for ($i = 0; $i -lt 4000; $i++) { $e.Write('0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ01234567890123456789'); } " +
                    "Write-Output 'done'",
                },
                tmp);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("done", result.StdOut);
            Assert.True(result.StdErr.Length > 100_000, $"expected a large stderr capture, got {result.StdErr.Length} bytes");
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
