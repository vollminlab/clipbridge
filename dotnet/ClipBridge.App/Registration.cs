using ClipBridge.Core;
using Microsoft.Win32;

namespace ClipBridge.App;

// The two registry entries clipbridge owns. Both are rewritten on every
// startup rather than once at --install, so moving or replacing the exe heals
// them on the next launch - and an install that predates them (a portable
// copy already in use) picks them up without re-running --install.
public static class Registration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "clipbridge";

    public static void Register()
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("could not determine exe path");

        // Registry Run key, not a Startup-folder .lnk shortcut (design decision
        // #6) - one file, no shortcut to keep in sync with the exe's path.
        using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            run.SetValue(RunValueName, $"\"{exePath}\"");
        }

        // Settings > Apps / appwiz.cpl entry. Without it a portable exe is
        // invisible there, and --uninstall is undiscoverable.
        using var uninstall = Registry.CurrentUser.CreateSubKey(UninstallRegistration.KeyPath);
        foreach (var (name, value) in UninstallRegistration.Values(exePath, new FileInfo(exePath).Length))
        {
            uninstall.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
    }

    public static void Unregister(TextWriter output)
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
        {
            if (run?.GetValue(RunValueName) is not null)
            {
                run.DeleteValue(RunValueName);
                output.WriteLine($@"removed HKCU\{RunKeyPath}\{RunValueName}");
            }
        }

        if (Registry.CurrentUser.OpenSubKey(UninstallRegistration.KeyPath) is { } existing)
        {
            existing.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistration.KeyPath);
            output.WriteLine($@"removed HKCU\{UninstallRegistration.KeyPath}");
        }
    }
}
