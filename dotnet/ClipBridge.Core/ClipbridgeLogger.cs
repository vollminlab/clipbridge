using System.Globalization;

namespace ClipBridge.Core;

public static class ClipbridgeLogger
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const string StampFormat = "yyyy-MM-ddTHH:mm:ss";

    // The stamp is fixed-width, so the retention filter can compare the first
    // StampLength characters of a line against a cutoff stamp ordinally. The
    // trailing 'Z' sits at index StampLength and is deliberately outside the
    // compared prefix, so adding it did not disturb the comparison.
    private const int StampLength = 19;

    // Formatted with InvariantCulture, not the ambient one. A custom format
    // string takes ':' from the current culture's TimeSeparator, so under a
    // locale such as fi-FI this stamp renders as 2026-08-19T04.30.00 - measured.
    // Width and ordinal sort happen to survive that, but the retention filter
    // compares these stamps as text, and a format this load-bearing should not
    // vary with a machine setting. ClipBridge.App sets InvariantGlobalization,
    // which would also mask it; pinning it here means Core does not depend on a
    // property set in a different assembly.

    // Capped at 7 days, same rule as the images (design spec). Each line
    // starts with a fixed-width, sortable stamp, so a plain ordinal string
    // compare against a cutoff stamp reproduces chronological order - a
    // filter, not a parse, which matters because this runs on every hotkey
    // press.
    //
    // UTC, not local time. A local-time stamp carries no offset, so during a
    // DST fall-back the repeated wall-clock hour renders two genuinely
    // different instants as byte-identical stamps - measured: two instants an
    // hour apart both produced 2026-11-01T01:30:00 and compared equal. UTC has
    // no repeated hour, so the ambiguity cannot arise.
    //
    // An offset-bearing stamp (yyyy-MM-ddTHH:mm:sszzz) would ALSO remove the
    // ambiguity, and was rejected: it is not reliably ordinal-sortable. Once
    // two lines differ in both local time and offset the comparison is
    // dominated by the local-time prefix, which is the wrong key - measured:
    // 2026-11-01T01:00:00-04:00 is genuinely EARLIER than
    // 2026-11-01T00:30:00-05:00, but sorts later. That would have put a subtle
    // ordering bug inside the retention filter to fix a rare display one.
    //
    // The 'Z' suffix is not decoration. Without it these timestamps read as
    // local time to anyone opening the log on the Windows laptop, which is
    // where it is actually read - a log that quietly misreports the time is a
    // worse defect than the aliasing this change removes.
    //
    // Note on upgrade: lines written by the previous local-time version have
    // no 'Z' and are still compared as though they were UTC, so on the first
    // run after upgrading they age out up to one UTC-offset early or late.
    // One-off, bounded by the offset, against a 7-day window.
    public static void Append(string configDir, string message, DateTime? now = null)
    {
        var effectiveNow = now ?? DateTime.UtcNow;
        Directory.CreateDirectory(configDir);
        var logPath = Path.Combine(configDir, "clipbridge.log");

        // One event must be exactly one physical line. A message containing a
        // newline would otherwise write a continuation line with no stamp,
        // which the NEXT call silently deletes because it fails the stamp
        // check below - so the tail of the message vanishes with no trace that
        // it ever existed. That is not hypothetical: .NET exception messages
        // routinely contain newlines, PasteOrchestrator logs exception
        // messages, and this log is the only record of a failure the user
        // never otherwise sees.
        var oneLine = message.ReplaceLineEndings(" | ");
        var line = $"{effectiveNow.ToString(StampFormat, CultureInfo.InvariantCulture)}Z  {oneLine}";
        var cutoff = effectiveNow.Subtract(Retention).ToString(StampFormat, CultureInfo.InvariantCulture);

        var kept = new List<string>();
        if (File.Exists(logPath))
        {
            foreach (var existing in File.ReadAllLines(logPath))
            {
                if (existing.Length >= StampLength && string.CompareOrdinal(existing[..StampLength], cutoff) >= 0)
                {
                    kept.Add(existing);
                }
            }
        }
        kept.Add(line);
        File.WriteAllLines(logPath, kept);
    }
}
