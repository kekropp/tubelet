using System.Globalization;
using Dapper;
using Tubelet.Data;
using Tubelet.Domain;
using Tubelet.Pipeline;
using Tubelet.Realtime;

namespace Tubelet.Scheduling;

/// <summary>
/// Checks one subscription for new uploads: <c>yt-dlp --flat-playlist --playlist-end N -J</c> on the
/// channel/playlist (cheap, RateGate'd, serial), applies its filter, and enqueues the misses at
/// priority 5. Dedup is delegated to <see cref="Queries.EnqueueJob"/> (skips archived/ignored/queued).
/// Shared by the <see cref="Scheduler"/> loop and the manual "scan now" endpoint.
/// </summary>
public sealed class SubscriptionScanner(
    Database db, YtDlpLocator locator, RateGate rateGate, AppPaths paths,
    PipelineSignal signal, Broadcaster bc, ILogger<SubscriptionScanner> log)
{
    /// <summary>Default window of recent uploads to inspect when the filter sets no max_items.</summary>
    public const int DefaultWindow = 30;

    public const int SubscriptionPriority = 5;

    /// <summary>Fire-and-forget scan (manual "scan now"). Never throws to the caller.</summary>
    public void ScanInBackground(long subscriptionId)
        => _ = Task.Run(async () =>
        {
            try
            {
                SubscriptionRow? sub;
                using (var conn = db.Open())
                    sub = conn.QuerySingleOrDefault<SubscriptionRow>(
                        "SELECT * FROM subscriptions WHERE id = @subscriptionId", new { subscriptionId });
                if (sub is not null) await ScanAsync(sub, CancellationToken.None);
            }
            catch (Exception e) { log.LogError(e, "manual scan failed for subscription {Id}", subscriptionId); }
        });

    /// <summary>Scan a subscription, enqueue new videos, and recompute its next_check. Returns count enqueued.</summary>
    public async Task<int> ScanAsync(SubscriptionRow sub, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var enqueued = 0;
        try
        {
            enqueued = await RunAsync(sub, ct);
        }
        finally
        {
            // Always advance the cursor, even on failure, so one bad subscription can't hot-loop the scheduler.
            using var conn = db.Open();
            conn.Execute(
                "UPDATE subscriptions SET last_check = @last, next_check = @next WHERE id = @id",
                new { last = now.ToUnixTimeSeconds(), next = CronSchedule.Next(sub.Cron, now), id = sub.Id });
        }

        if (enqueued > 0)
        {
            signal.Signal();
            using var conn = db.Open();
            var s = Queries.QueueStats(conn);
            await bc.QueueStats(new(s.Queued, s.Running, s.Failed, s.Done));
        }
        return enqueued;
    }

    private async Task<int> RunAsync(SubscriptionRow sub, CancellationToken ct)
    {
        if (locator.Resolve() is null)
        {
            log.LogWarning("subscription {Id}: yt-dlp not available, skipping scan", sub.Id);
            return 0;
        }

        var kind = sub.Kind == "playlist" ? UrlKind.Playlist : UrlKind.Channel;
        var filter = SubscriptionFilter.Parse(sub.FilterJson);
        var window = filter.MaxItems is > 0 ? filter.MaxItems.Value : DefaultWindow;

        await rateGate.WaitAsync(ct);
        var url = YtSources.For(kind, sub.TargetId);
        var r = await Proc.RunAsync(locator.Path, Args(sub, url, window), ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || r.Stdout.Trim().Length == 0)
        {
            log.LogWarning("subscription {Id}: flat-playlist failed: {Err}", sub.Id, r.Stderr.Trim());
            return 0;
        }

        var listing = FlatPlaylist.Parse(r.Stdout);
        var enqueued = 0;
        using var conn = db.Open();
        if (kind == UrlKind.Playlist) Playlists.UpsertRegular(conn, sub.TargetId, listing);

        foreach (var e in listing.Entries)
        {
            if (enqueued >= filter.Cap) break;
            if (!filter.Accepts(e)) continue;
            if (Queries.EnqueueJob(conn, e.Id, SubscriptionPriority, e.ChannelId ?? listing.ChannelId, e.Title))
                enqueued++;
        }

        log.LogInformation("subscription {Id} ({Target}): enqueued {N} new video(s)", sub.Id, sub.TargetId, enqueued);
        return enqueued;
    }

    private IEnumerable<string> Args(SubscriptionRow sub, string url, int window)
    {
        var net = NetworkOptions.Defaults;
        using (var conn = db.Open()) net = NetworkOptions.Load(conn);

        List<string> a =
        [
            "--flat-playlist", "-J", "--no-warnings",
            "--playlist-end", window.ToString(CultureInfo.InvariantCulture),
            "--sleep-requests", net.SleepRequests!.Value.ToString(CultureInfo.InvariantCulture),
        ];
        if (YtSources.CookiesFile(paths) is { } cookies) { a.Add("--cookies"); a.Add(cookies); }
        a.Add(url);
        return a;
    }
}
