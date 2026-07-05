using Dapper;
using Tubelet.Data;
using Tubelet.Domain;
using Tubelet.Pipeline;
using Tubelet.Sponsorblock;

namespace Tubelet.Scheduling;

/// <summary>
/// One hosted service, one loop (DESIGN §6). Every tick it selects enabled subscriptions whose
/// next_check is due and scans them serially (RateGate keeps YouTube traffic polite), then runs the
/// cron-gated SB-refresh and yt-dlp self-update lanes. Cron math and cursor advancement live in
/// <see cref="SubscriptionScanner"/> / <see cref="CronGate"/>; filesystem/db housekeeping is the
/// <see cref="Janitor"/>'s job.
/// </summary>
public sealed class Scheduler(
    Database db, SubscriptionScanner scanner, SbRefresher sbRefresher, YtDlpLocator locator,
    IHttpClientFactory httpFactory, ILogger<Scheduler> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First tick after a short delay so startup (migration, fixtures, recovery) settles first.
        using var timer = new PeriodicTimer(Tick);
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; }

        do
        {
            try { await RunDueAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogError(e, "scheduler tick failed"); }

            try { await RunMaintenanceLanesAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogError(e, "scheduler maintenance lane failed"); }
        }
        while (await SafeWait(timer, stoppingToken));
    }

    // ---- maintenance lanes (SB refresh + yt-dlp self-update) ---------------

    private async Task RunMaintenanceLanesAsync(CancellationToken ct)
    {
        MaintenanceOptions opt;
        bool sbDue, updateDue, updateRequested;
        using (var conn = db.Open())
        {
            opt = MaintenanceOptions.Load(conn);
            var now = DateTimeOffset.UtcNow;
            sbDue = CronGate.DueAndReschedule(conn, "sb_refresh_next", opt.SbRefreshCron!, now);
            updateDue = opt.YtdlpAutoUpdate!.Value && CronGate.DueAndReschedule(conn, "ytdlp_update_next", opt.YtdlpUpdateCron!, now);
            // Set by the coordinator after repeated extractor breakage (DESIGN §9).
            updateRequested = opt.YtdlpAutoUpdate!.Value && Database.GetSetting(conn, "ytdlp_update_requested") == "1";
        }

        if (updateDue || updateRequested)
        {
            using (var conn = db.Open()) Database.SetSetting(conn, "ytdlp_update_requested", "0");
            await SelfUpdateYtDlp(ct).ConfigureAwait(false);
        }
        if (sbDue) await sbRefresher.RefreshRecentAsync(ct).ConfigureAwait(false);
    }

    private async Task SelfUpdateYtDlp(CancellationToken ct)
    {
        try
        {
            var version = await locator.DownloadLatestAsync(httpFactory.CreateClient(), ct).ConfigureAwait(false);
            log.LogInformation("yt-dlp self-update complete: {Version}", version);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "yt-dlp self-update failed");
        }
    }

    private async Task RunDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        List<SubscriptionRow> due;
        using (var conn = db.Open())
            due = conn.Query<SubscriptionRow>("""
                SELECT * FROM subscriptions
                WHERE enabled = 1 AND (next_check IS NULL OR next_check <= @now)
                ORDER BY next_check
                """, new { now }).ToList();

        foreach (var sub in due)
        {
            ct.ThrowIfCancellationRequested();
            await scanner.ScanAsync(sub, ct); // scans serially; never throws (advances cursor in a finally)
        }
    }

    private static async Task<bool> SafeWait(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
