using Tubelet.Data;
using Tubelet.Realtime;

namespace Tubelet.Pipeline;

/// <summary>
/// Expands a channel/playlist paste into individual download jobs via
/// <c>yt-dlp --flat-playlist -J</c> (cheap: 1–2 requests, no per-video pages). Runs in the
/// background; progress rides SignalR <c>scan.progress</c>. Already-archived/ignored/queued
/// ids are skipped and counted.
/// </summary>
public sealed class IntakeExpander(
    Database db, YtDlpLocator locator, RateGate rateGate, AppPaths paths,
    PipelineSignal signal, Broadcaster bc, ILogger<IntakeExpander> log)
{
    /// <summary>Fire-and-forget expansion. Never throws to the caller (intake responds immediately).</summary>
    public void ExpandInBackground(UrlKind kind, string id, int priority)
        => _ = Task.Run(async () =>
        {
            try { await ExpandAsync(kind, id, priority, CancellationToken.None); }
            catch (Exception e) { log.LogError(e, "expansion failed for {Kind} {Id}", kind, id); }
        });

    public async Task ExpandAsync(UrlKind kind, string id, int priority, CancellationToken ct)
    {
        if (locator.Resolve() is null)
        {
            await bc.ScanProgress(id, 0, 0, done: true, "yt-dlp is not installed — cannot expand.");
            await bc.SystemBanner("ytdlp", "yt-dlp is not available. Install it or download it in Settings → Maintenance.");
            return;
        }

        await rateGate.WaitAsync(ct);
        var cookies = YtSources.CookiesFile(paths);
        await bc.ScanProgress(id, 0, 0, done: false, "Fetching listing…");

        var url = YtSources.For(kind, id);
        var r = await Proc.RunAsync(locator.Path,
            Args(url, cookies), ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || r.Stdout.Trim().Length == 0)
        {
            log.LogWarning("flat-playlist failed for {Url}: {Err}", url, r.Stderr);
            await bc.ScanProgress(id, 0, 0, done: true, "Could not read the listing (see logs).");
            return;
        }

        var listing = FlatPlaylist.Parse(r.Stdout);
        var total = listing.Entries.Length;
        var enqueued = 0;

        using (var conn = db.Open())
        {
            if (kind == UrlKind.Playlist) Playlists.UpsertRegular(conn, id, listing);

            foreach (var e in listing.Entries)
            {
                if (Queries.EnqueueJob(conn, e.Id, priority, e.ChannelId ?? listing.ChannelId, e.Title))
                    enqueued++;
                if (enqueued % 10 == 0)
                    await bc.ScanProgress(id, total, enqueued, done: false);
            }
        }

        signal.Signal();
        await bc.ScanProgress(id, total, enqueued, done: true,
            $"Queued {enqueued} of {total} video(s).");
        log.LogInformation("Expanded {Kind} {Id}: {Enqueued}/{Total} enqueued", kind, id, enqueued, total);
    }

    private static IEnumerable<string> Args(string url, string? cookies)
    {
        List<string> a = ["--flat-playlist", "-J", "--no-warnings"];
        if (!string.IsNullOrEmpty(cookies)) { a.Add("--cookies"); a.Add(cookies); }
        a.Add(url);
        return a;
    }

}
