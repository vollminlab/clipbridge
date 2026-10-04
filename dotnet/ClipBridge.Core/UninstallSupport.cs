using System.Text.RegularExpressions;

namespace ClipBridge.Core;

public static class SshConfigHostBlockRemover
{
    private static readonly Regex AnyHeader = new(@"^\s*(Host|Match)\s", RegexOptions.IgnoreCase);

    // Inverse of what --install appends (SshConfigBlockBuilder.Build): the
    // 'Host <alias>' line through to the next Host/Match header or EOF.
    // Matched with the same anchored rule as SshConfigInspector.HasHostBlock,
    // so 'Host clipbridge-laptop' and 'Host clipbridge other' are never
    // touched - uninstall removes exactly what install would have
    // recognised as its own, and nothing else.
    //
    // Returns the input unchanged (same instance) when there is no block, so
    // the caller can skip rewriting the file - and skip the backup.
    public static string Remove(string config, string hostAlias)
    {
        var own = new Regex(@"^\s*Host\s+" + Regex.Escape(hostAlias) + @"\s*$", RegexOptions.IgnoreCase);
        var newline = config.Contains("\r\n") ? "\r\n" : "\n";
        var lines = config.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        var changed = false;

        int start;
        while ((start = lines.FindIndex(l => own.IsMatch(l))) >= 0)
        {
            var end = start + 1;
            while (end < lines.Count && !AnyHeader.IsMatch(lines[end])) end++;

            if (end == lines.Count)
            {
                // Last block in the file, which is where install put it. Also
                // take the blank separator line Build() emits ahead of it, so
                // the file returns to the shape it had before install.
                if (start > 0 && string.IsNullOrWhiteSpace(lines[start - 1])) start--;
                lines.RemoveRange(start, end - start);
                lines.Add("");
            }
            else
            {
                // Something follows: keep its preceding separator, drop ours.
                lines.RemoveRange(start, end - start);
            }
            changed = true;
        }

        if (!changed) return config;

        // Collapse the trailing blank lines this can leave down to the single
        // final newline the file had (or none, if it had none).
        while (lines.Count > 1 && string.IsNullOrWhiteSpace(lines[^1]) && string.IsNullOrWhiteSpace(lines[^2]))
            lines.RemoveAt(lines.Count - 1);
        var hadFinalNewline = config.EndsWith('\n');
        var joined = string.Join(newline, lines);
        return hadFinalNewline ? joined : joined.TrimEnd('\r', '\n');
    }
}

// What Add/Remove Programs reads. Kept here, as data, so the parts that are
// easy to get subtly wrong - quoting the exe path, the size unit, the quiet
// variant - are asserted on Linux rather than discovered in appwiz.cpl.
public static class UninstallRegistration
{
    // HKCU, not HKLM: clipbridge is per-user and never elevates, and
    // Settings > Apps lists per-user entries alongside machine-wide ones.
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\clipbridge";

    public static IReadOnlyList<(string Name, object Value)> Values(string exePath, long exeSizeBytes)
    {
        var quoted = $"\"{exePath}\"";
        return
        [
            ("DisplayName", "clipbridge"),
            ("Publisher", "vollminlab"),
            ("DisplayIcon", $"{exePath},0"),
            ("InstallLocation", Path.GetDirectoryName(exePath) ?? ""),
            ("UninstallString", $"{quoted} --uninstall"),
            ("QuietUninstallString", $"{quoted} --uninstall --quiet"),
            ("URLInfoAbout", "https://github.com/vollminlab/clipbridge"),
            // REG_DWORD, in KiB - appwiz multiplies by 1024 for display.
            ("EstimatedSize", (int)Math.Ceiling(exeSizeBytes / 1024.0)),
            // No --modify/--repair entry points exist; without these the
            // dialog offers buttons that would just run the uninstaller.
            ("NoModify", 1),
            ("NoRepair", 1),
        ];
    }
}
