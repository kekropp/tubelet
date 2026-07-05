using Dapper;
using Tubelet.Data;
using Tubelet.Pipeline;

namespace Tubelet.Scheduling;

/// <summary>
/// Housekeeping hosted service (DESIGN §2/§4.3/§6): on a nightly cron it deletes orphaned .part files,
/// runs <c>PRAGMA optimize</c>, backs the db up to <c>{cache}/backup</c>, and rotates logs. Every action
/// is idempotent and best-effort — a failure in one never blocks the others or the loop.
/// </summary>
public sealed class Janitor(Database db, AppPaths paths, ILogger<Janitor> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { RunIfDue(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogError(e, "janitor tick failed"); }
        }
        while (await SafeWait(timer, stoppingToken));
    }

    private void RunIfDue(CancellationToken ct)
    {
        MaintenanceOptions opt;
        bool due;
        using (var conn = db.Open())
        {
            opt = MaintenanceOptions.Load(conn);
            due = CronGate.DueAndReschedule(conn, "janitor_next", opt.JanitorCron!, DateTimeOffset.UtcNow);
        }
        if (!due) return;

        log.LogInformation("janitor: nightly maintenance running");
        var orphans = DeleteOrphanParts(opt.PartTtlDays!.Value);
        if (orphans > 0) log.LogInformation("janitor: removed {N} orphaned incomplete file(s)", orphans);
        Optimize();
        if (opt.BackupEnabled!.Value) { BackupDatabase(); PruneBackups(opt.BackupKeep!.Value); }
        RotateLogs();
    }

    // ---- orphan .part cleanup (testable) -----------------------------------

    /// <summary>
    /// Delete files in the incomplete dir older than <paramref name="ttlDays"/> whose video id has no
    /// live job (anything but <c>done</c>) that could still resume them. Returns the count removed.
    /// </summary>
    public int DeleteOrphanParts(int ttlDays)
    {
        if (!Directory.Exists(paths.IncompleteDir)) return 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-ttlDays);

        using var conn = db.Open();
        var live = conn.Query<string>("SELECT youtube_id FROM jobs WHERE state <> 'done'").ToHashSet(StringComparer.Ordinal);

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(paths.IncompleteDir))
        {
            var id = Path.GetFileName(file).Split('.', 2)[0]; // <id>.<ext>[.part] — id has no dots
            if (live.Contains(id)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(file) > cutoff.UtcDateTime) continue;
                File.Delete(file);
                removed++;
            }
            catch (IOException e) { log.LogWarning(e, "janitor: could not delete {File}", file); }
        }
        return removed;
    }

    // ---- db maintenance ----------------------------------------------------

    private void Optimize()
    {
        try { using var conn = db.Open(); conn.Execute("PRAGMA optimize;"); }
        catch (Exception e) { log.LogWarning(e, "janitor: PRAGMA optimize failed"); }
    }

    /// <summary>Snapshot the db to a dated file with <c>VACUUM INTO</c> (a clean, consistent copy).</summary>
    public string? BackupDatabase()
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var dest = Path.Combine(paths.BackupDir, $"tubelet-{stamp}.db");
            using var conn = db.Open();
            conn.Execute("VACUUM INTO @dest", new { dest });
            log.LogInformation("janitor: database backed up to {Dest}", dest);
            return dest;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "janitor: db backup failed");
            return null;
        }
    }

    private void PruneBackups(int keep)
    {
        try
        {
            var old = Directory.EnumerateFiles(paths.BackupDir, "tubelet-*.db")
                .OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep);
            foreach (var f in old) File.Delete(f);
        }
        catch (IOException e) { log.LogWarning(e, "janitor: backup prune failed"); }
    }

    private void RotateLogs()
    {
        try
        {
            if (!Directory.Exists(paths.LogDir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (var f in Directory.EnumerateFiles(paths.LogDir))
                if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
        }
        catch (IOException e) { log.LogWarning(e, "janitor: log rotation failed"); }
    }

    private static async Task<bool> SafeWait(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
