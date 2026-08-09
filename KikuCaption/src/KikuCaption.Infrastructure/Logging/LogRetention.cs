using System.Globalization;
using System.Text.RegularExpressions;

namespace KikuCaption.Infrastructure.Logging;

/// <summary>
/// Startup cleanup of over-retention rolling log files (Milestone 7 §6). Deletes only
/// <c>app-yyyyMMdd.log</c> files older than the retention window — never meeting files, databases,
/// or anything else.
/// </summary>
public static class LogRetention
{
    private static readonly Regex DatedLog = new(@"^app-(\d{8})\.log$", RegexOptions.Compiled);

    /// <summary>Deletes dated app logs older than <paramref name="retentionDays"/>. Returns the count.</summary>
    public static int CleanupOldLogs(string logDirectory, int retentionDays, DateTime? nowUtc = null)
    {
        if (retentionDays < 1)
        {
            retentionDays = 1;
        }

        if (!Directory.Exists(logDirectory))
        {
            return 0;
        }

        var cutoff = (nowUtc ?? DateTime.UtcNow).Date.AddDays(-retentionDays);
        int deleted = 0;

        foreach (var path in Directory.EnumerateFiles(logDirectory, "app-*.log"))
        {
            var match = DatedLog.Match(Path.GetFileName(path));
            if (!match.Success)
            {
                continue;
            }

            if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fileDate))
            {
                continue;
            }

            if (fileDate.Date < cutoff)
            {
                try { File.Delete(path); deleted++; } catch { /* best effort; never fatal */ }
            }
        }

        return deleted;
    }
}
