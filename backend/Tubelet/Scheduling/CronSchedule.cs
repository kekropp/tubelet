using Cronos;

namespace Tubelet.Scheduling;

/// <summary>
/// Thin wrapper over Cronos. Subscriptions store a standard 5-field cron string
/// (minute hour dom month dow); we also accept 6-field (with seconds) transparently.
/// All occurrences are computed in UTC — the schedule is machine-relative, not
/// wall-clock-locale-relative, which keeps container restarts deterministic.
/// </summary>
public static class CronSchedule
{
    public const string Default = "0 */6 * * *"; // every 6 hours

    public static bool IsValid(string? cron) => TryParse(cron, out _);

    /// <summary>Next occurrence strictly after <paramref name="from"/> as unixtime, or null if the cron is invalid.</summary>
    public static long? Next(string? cron, DateTimeOffset from)
    {
        if (!TryParse(cron, out var expr)) return null;
        // UTC-only (from.UtcDateTime has Kind=Utc): no DST ambiguity, deterministic across restarts.
        var next = expr.GetNextOccurrence(from.UtcDateTime);
        return next is null ? null : new DateTimeOffset(next.Value, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    private static bool TryParse(string? cron, out CronExpression expr)
    {
        expr = null!;
        if (string.IsNullOrWhiteSpace(cron)) return false;
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fields >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        try { expr = CronExpression.Parse(cron, format); return true; }
        catch (CronFormatException) { return false; }
    }
}
