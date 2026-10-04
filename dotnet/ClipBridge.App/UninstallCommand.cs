using System.Diagnostics;
using ClipBridge.Core;
using ClipBridge.Win32;

namespace ClipBridge.App;

// Reverses --install plus what every startup registers. Each step is
// independent and non-fatal: a step that fails is reported and the rest still
// run, so one locked file never strands the registry entries. Exit code 1 if
// anything was left behind.
//
// Deliberately NOT touched: clipbridge-recv and its image store on devsbx01.
// That is a separate machine, and linux/install.sh owns it.
public static class UninstallCommand
{
    private const string HostAlias = "clipbridge";

    public static int Run(TextWriter output)
    {
        var ok = true;
        void Step(string what, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                ok = false;
                output.WriteLine($"could not {what} - {ex.Message}");
            }
        }

        // First, because a running instance rewrites both registry entries on
        // its next startup and holds its log file open.
        Step("stop the running clipbridge", () => StopRunningInstances(output));
        Step("remove registry entries", () => Registration.Unregister(output));
        Step("remove the Start Menu shortcut", () => DeleteFile(output, StartMenuShortcutPath()));
        Step("remove the ssh config block", () => RemoveSshConfigBlock(output));
        Step("remove config and log", () =>
        {
            var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clipbridge");
            if (Directory.Exists(configDir))
            {
                Directory.Delete(configDir, recursive: true);
                output.WriteLine($"removed {configDir}");
            }
        });
        Step("schedule deletion of the exe", () => ScheduleSelfDelete(output));

        output.WriteLine(ok ? "clipbridge uninstalled" : "clipbridge uninstalled with errors - see above");
        return ok ? 0 : 1;
    }

    internal static string StartMenuShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "clipbridge.lnk");

    private static void StopRunningInstances(TextWriter output)
    {
        var self = Environment.ProcessId;
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "ClipBridge.App";
        var others = Process.GetProcessesByName(name).Where(p => p.Id != self).ToList();
        if (others.Count == 0) return;

        // Polite first: the tray's own Exit path unhooks Ctrl+V and removes
        // the icon. A killed process leaves a ghost icon until hovered.
        TrayIcon.RequestExitOfRunningInstances();
        foreach (var p in others)
        {
            using (p)
            {
                if (!p.WaitForExit(5000))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                    output.WriteLine($"killed clipbridge (pid {p.Id}) - it did not exit when asked");
                }
                else
                {
                    output.WriteLine($"stopped clipbridge (pid {p.Id})");
                }
            }
        }
    }

    private static void RemoveSshConfigBlock(TextWriter output)
    {
        var sshConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        if (!File.Exists(sshConfig)) return;

        var before = File.ReadAllText(sshConfig);
        var after = SshConfigHostBlockRemover.Remove(before, HostAlias);
        if (ReferenceEquals(before, after)) return;

        // It is the user's file, not ours - keep what was there.
        var backup = sshConfig + ".clipbridge-uninstall.bak";
        File.WriteAllText(backup, before);
        File.WriteAllText(sshConfig, after);
        output.WriteLine($"removed Host {HostAlias} from {sshConfig} (previous copy: {backup})");
    }

    private static void DeleteFile(TextWriter output, string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        output.WriteLine($"removed {path}");
    }

    // A running exe cannot delete itself, so a detached cmd.exe does it once
    // this process has exited. ping is the delay because `timeout` refuses to
    // run without a console stdin. Only the exe and its .pdb (the whole
    // publish output) are deleted, and the folder only via plain `rmdir`,
    // which fails harmlessly unless it is empty - the exe may sit in
    // Downloads or anywhere else the artifact was extracted.
    private static void ScheduleSelfDelete(TextWriter output)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("could not determine exe path");
        var dir = Path.GetDirectoryName(exe) ?? throw new InvalidOperationException("exe has no directory");
        var pdb = Path.ChangeExtension(exe, ".pdb");

        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(), // or cmd holds `dir` open and rmdir fails
            Arguments = $"/d /c ping -n 3 127.0.0.1 >nul & del /f /q \"{exe}\" \"{pdb}\" 2>nul & rmdir \"{dir}\" 2>nul",
        };
        using var _ = Process.Start(psi) ?? throw new InvalidOperationException("could not start cmd.exe");
        output.WriteLine($"{exe} will be deleted when this process exits");
    }
}
