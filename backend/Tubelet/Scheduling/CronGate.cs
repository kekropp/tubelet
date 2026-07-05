using Microsoft.Data.Sqlite;
using Tubelet.Data;

namespace Tubelet.Scheduling;

/// <summary>
/// A cron-gated lane whose next-run instant is persisted in a settings key, so schedules survive
/// restarts and a lane missed while the container was down runs once on the next boot (catch-up).
/// First encounter schedules but does not fire, so startup never triggers a burst of maintenance.
/// </summary>
public static class CronGate
{
    /// <summary>True if the lane is due now; reschedules the next occurrence as a side effect.</summary>
    public static bool DueAndReschedule(SqliteConnection conn, string key, string cron, DateTimeOffset now)
    {
        var raw = Database.GetSetting(conn, key);
        var next = CronSchedule.Next(cron, now);

        if (!long.TryParse(raw, out var scheduled) || scheduled == 0)
        {
            if (next is not null) Database.SetSetting(conn, key, next.Value.ToString());
            return false; // first sighting: schedule, don't run
        }
        if (now.ToUnixTimeSeconds() < scheduled) return false;

        if (next is not null) Database.SetSetting(conn, key, next.Value.ToString());
        return true;
    }
}
