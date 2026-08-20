using System.ComponentModel;
using System.Diagnostics;
using ClipBridge.Core;

namespace ClipBridge.App;

public static class InstallCommand
{
    public static int Run(TextWriter output)
    {
        // Two DIFFERENT names, and conflating them breaks the probe: ssh
        // matches Host patterns against the name typed on the command
        // line, not the resolved hostname. probeHost must be a name the
        // user's EXISTING ~/.ssh/config already has a Host block for.
        // targetHost becomes HostName in the generated block, which IS
        // resolved by DNS directly, so it needs the FQDN.
        const string probeHost = "devsbx01";
        const string targetHost = "devsbx01.vollminlab.com";
        const string targetUser = "vollmin";
        const string hostAlias = "clipbridge";

        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clipbridge");
        // Deliberately the PUBLIC key path - this is how the 1Password SSH
        // agent selects which key to offer. Do not change this to the
        // private key path.
        var identityFile = Path.Combine(sshDir, "devsbx01_id_ed25519.pub");

        output.WriteLine($"Probing ssh.exe against {probeHost}...");
        var sshProbe = ProbeTransport("ssh.exe", Array.Empty<string>(), probeHost);
        var sshOutcome = TransportProbeClassifier.Classify(sshProbe.ExeFound, sshProbe.ExitCode, sshProbe.StdErr);

        ProbeOutcome wslOutcome;
        if (sshOutcome == ProbeOutcome.Authenticated)
        {
            wslOutcome = ProbeOutcome.NotProbed;
        }
        else
        {
            output.WriteLine($"ssh.exe did not authenticate ({sshOutcome}); probing wsl.exe -e ssh...");
            var wslProbe = ProbeTransport("wsl.exe", new[] { "-e", "ssh" }, probeHost);
            wslOutcome = TransportProbeClassifier.Classify(wslProbe.ExeFound, wslProbe.ExitCode, wslProbe.StdErr);
        }

        string transport;
        try
        {
            transport = TransportProbeClassifier.SelectTransport(sshOutcome, wslOutcome, probeHost);
        }
        catch (ClipbridgeConfigException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        output.WriteLine($"transport: {transport}");

        var paths = ClipbridgePaths.From(sshDir, configDir);
        Directory.CreateDirectory(sshDir);

        var existingConfig = File.Exists(paths.SshConfigPath) ? File.ReadAllText(paths.SshConfigPath) : "";
        if (SshConfigInspector.HasHostBlock(existingConfig, hostAlias))
        {
            output.WriteLine($"ssh config already has a '{hostAlias}' Host block - leaving it alone");
        }
        else
        {
            var block = SshConfigBlockBuilder.Build(hostAlias, targetHost, targetUser, identityFile);
            File.AppendAllText(paths.SshConfigPath, block);
            output.WriteLine($"added Host {hostAlias} to {paths.SshConfigPath}");
        }

        Directory.CreateDirectory(configDir);
        var cfg = ClipbridgeConfigFactory.Create(hostAlias, transport);
        File.WriteAllText(paths.ConfigJsonPath, ClipbridgeConfigWriter.ToJson(cfg));
        output.WriteLine($"wrote {paths.ConfigJsonPath}");

        CreateStartMenuShortcut(output);

        return 0;
    }

    // Bounds how long a single probe can block the installer. ConnectTimeout=5
    // (below) only bounds the TCP connect - a wedged ssh.exe (auth prompt it
    // can't complete in BatchMode, a hung agent forwarding, etc.) can still
    // sit past that with the process never exiting. This is deliberately
    // generous relative to ConnectTimeout=5: it needs headroom for the
    // authentication exchange itself (agent round-trip, host key checks),
    // not just the TCP handshake, while still being short enough that the
    // installer never looks hung to a human watching it.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    // Shelled out to PowerShell rather than done in-process, because NativeAOT
    // has no built-in COM support and creating a .lnk means IShellLink. One
    // process spawn at install time is a better trade than hand-rolling the
    // shortcut binary format or wiring up ComWrappers for a single call.
    //
    // Per-user Start Menu, so this needs no elevation. Non-fatal: a missing
    // shortcut is cosmetic, and the install itself has already succeeded by the
    // time this runs.
    private static void CreateStartMenuShortcut(TextWriter output)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                output.WriteLine("skipped Start Menu shortcut - could not determine own path");
                return;
            }

            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            Directory.CreateDirectory(startMenu);
            var lnk = Path.Combine(startMenu, "clipbridge.lnk");

            var script =
                "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('" + lnk.Replace("'", "''") + "'); " +
                "$s.TargetPath = '" + exePath.Replace("'", "''") + "'; " +
                "$s.WorkingDirectory = '" + (Path.GetDirectoryName(exePath) ?? "").Replace("'", "''") + "'; " +
                "$s.IconLocation = '" + exePath.Replace("'", "''") + ",0'; " +
                "$s.Description = 'clipbridge - paste a screenshot into a remote Claude Code prompt'; " +
                "$s.Save()";

            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var process = Process.Start(psi);
            if (process is null)
            {
                output.WriteLine("skipped Start Menu shortcut - could not start powershell.exe");
                return;
            }
            var stdErrTask = process.StandardError.ReadToEndAsync();
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                output.WriteLine("skipped Start Menu shortcut - powershell.exe timed out");
                return;
            }
            stdOutTask.GetAwaiter().GetResult();
            var stdErr = stdErrTask.GetAwaiter().GetResult();

            if (process.ExitCode == 0 && File.Exists(lnk))
            {
                output.WriteLine($"created Start Menu shortcut {lnk}");
            }
            else
            {
                output.WriteLine($"could not create Start Menu shortcut (exit {process.ExitCode}) {stdErr.Trim()}");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"could not create Start Menu shortcut - {ex.Message}");
        }
    }

    private static (bool ExeFound, int ExitCode, string StdErr) ProbeTransport(string exe, string[] prefix, string targetHost)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in prefix) startInfo.ArgumentList.Add(a);
        startInfo.ArgumentList.Add("-o"); startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add("-o"); startInfo.ArgumentList.Add("ConnectTimeout=5");
        startInfo.ArgumentList.Add(targetHost);
        startInfo.ArgumentList.Add("echo clipbridge-ok");

        // Same deadlock SshTransport.Send already fixed (Task 16), and it
        // matters MORE here: the child is ssh.exe itself doing an
        // interactive-ish auth attempt, which is exactly the process most
        // likely to write a large stderr (banners, "Warning: Permanently
        // added...", verbose auth output on failure). If stderr fills its
        // pipe buffer while we're blocked in a synchronous stdout
        // ReadToEnd(), both sides stop and the installer hangs forever with
        // no output. Starting both reads asynchronously before waiting
        // removes the cycle - see SshTransport.Send for the full writeup.
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"failed to start {exe}");

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                KillProcessTree(process);
                // -1 is never mistaken for the "0 exit but wrong stdout"
                // downgrade below, and TransportProbeClassifier.Classify
                // routes any non-zero exit to its stderr-pattern checks -
                // stderr is empty here, so it falls through to
                // OtherFailure, not the Timeout branch. That's acceptable:
                // the caller-visible outcome is still a probe failure, and
                // ConnectTimeout=5 already covers the "genuinely
                // unreachable host" case that Timeout classification is
                // for. This branch is specifically the "process wedged
                // past both the TCP connect timeout and a generous
                // installer-side ceiling" case.
                return (true, -1, "");
            }

            var stdout = stdOutTask.GetAwaiter().GetResult();
            var stderr = stdErrTask.GetAwaiter().GetResult();
            // Belt-and-suspenders: a 0 exit with unexpected stdout is not a
            // trustworthy "authenticated" - downgraded to OtherFailure territory.
            var exitCode = process.ExitCode == 0 && !stdout.Contains("clipbridge-ok") ? -1 : process.ExitCode;
            return (true, exitCode, stderr);
        }
        catch (Win32Exception)
        {
            return (false, -1, "");
        }
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
