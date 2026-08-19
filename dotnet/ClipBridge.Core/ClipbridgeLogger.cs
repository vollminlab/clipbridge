namespace ClipBridge.Core;

public static class ClipbridgeLogger
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const string StampFormat = "yyyy-MM-ddTHH:mm:ss";

    // Capped at 7 days, same rule as the images (design spec). Each line
    // starts with a fixed-width, sortable stamp, so a plain ordinal string
    // compare against a cutoff stamp reproduces chronological order - a
    // filter, not a parse, which matters because this runs on every hotkey
    // press.
    public static void Append(string configDir, string message, DateTime? now = null)
    {
        var effectiveNow = now ?? DateTime.Now;
        Directory.CreateDirectory(configDir);
        var logPath = Path.Combine(configDir, "clipbridge.log");
        var line = $"{effectiveNow.ToString(StampFormat)}  {message}";
        var cutoff = effectiveNow.Subtract(Retention).ToString(StampFormat);

        var kept = new List<string>();
        if (File.Exists(logPath))
        {
            foreach (var existing in File.ReadAllLines(logPath))
            {
                if (existing.Length >= 19 && string.CompareOrdinal(existing[..19], cutoff) >= 0)
                {
                    kept.Add(existing);
                }
            }
        }
        kept.Add(line);
        File.WriteAllLines(logPath, kept);
    }
}
