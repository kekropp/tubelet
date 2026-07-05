using Tubelet.Data;
using Tubelet.Realtime;

namespace Tubelet.Pipeline;

/// <summary>
/// Expands a channel/playlist paste into individual download jobs via
/// <c>yt-dlp --flat-playlist -J</c> (cheap: 1–2 requests, no per-video pages). Runs in the
/// background; progress rides SignalR <c>scan.progress</c>. Already-archived/ignored/queued
/// ids are skipped and counted. An <see cref="IntakeScope"/> narrows how many entries are taken
/// (whole listing, newest N, or everything since a date).
/// </summary>
public sealed class IntakeExpander(
    Database db, YtDlpLocator locator, RateGate rateGate, AppPaths paths,
    PipelineSignal signal, Broadcaster bc, ILogger<IntakeExpander> log)
{
    /// <summary>Fire-and-forget expansion. Never throws to the caller (intake responds immediately).</summary>
    public void ExpandInBackground(UrlKind kind, string id, int priority, IntakeScope? scope = null)
        => _ = Task.Run(async () =>
        {
            try { await ExpandAsync(kind, id, priority, scope ?? IntakeScope.All, CancellationToken.None); }
            catch (Exception e) { log.LogError(e, "expansion failed for {Kind} {Id}", kind, id); }
        });

    /// <summary>
    /// Runs the flat-playlist fetch for a channel/playlist. Returns the parsed listing, or null with an
    /// error message when yt-dlp is unavailable or the listing can't be read. RateGate'd + cookie-aware.
    /// </summary>
    public async Task<(FlatListing? Listing, string? Error)> FetchListingAsync(UrlKind kind, string id, CancellationToken ct)
    {
        if (locator.Resolve() is null)
            return (null, "yt-dlp is not installed — cannot read the listing.");

        await rateGate.WaitAsync(ct);
        var url = YtSources.For(kind, id);
        var r = await Proc.RunAsync(locator.Path, Args(url, YtSources.CookiesFile(paths)), ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || r.Stdout.Trim().Length == 0)
        {
            log.LogWarning("flat-playlist failed for {Url}: {Err}", url, r.Stderr);
            return (null, "Could not read the listing (see logs).");
        }
        return (FlatPlaylist.Parse(r.Stdout), null);
    }

    public async Task ExpandAsync(UrlKind kind, string id, int priority, IntakeScope scope, CancellationToken ct)
    {
        await bc.ScanProgress(id, 0, 0, done: false, "Fetching listing…");

        var (listing, error) = await FetchListingAsync(kind, id, ct);
        if (listing is null)
        {
            await bc.ScanProgress(id, 0, 0, done: true, error);
            if (locator.Resolve() is null)
                await bc.SystemBanner("ytdlp", "yt-dlp is not available. Install it or download it in Settings → Maintenance.");
            return;
        }

        // "None" = subscribe but grab no backlog. Baseline the current ids onto the skip list so future
        // scans only enqueue genuinely new uploads — robust even when YouTube exposes no upload dates.
        if (scope.Mode == ScopeMode.None)
        {
            int ignored;
            using (var conn = db.Open())
            {
                if (kind == UrlKind.Playlist) Playlists.UpsertRegular(conn, id, listing);
                ignored = Queries.IgnoreExisting(conn, listing.Entries.Select(e => e.Id));
            }
            await bc.ScanProgress(id, 0, 0, done: true,
                $"Skipping {ignored} existing video(s) — only new uploads will download.");
            log.LogInformation("Baselined {Kind} {Id}: {N} existing id(s) ignored", kind, id, ignored);
            return;
        }

        var selected = scope.Slice(listing.Entries).ToArray();
        var total = selected.Length;
        var enqueued = 0;

        using (var conn = db.Open())
        {
            if (kind == UrlKind.Playlist) Playlists.UpsertRegular(conn, id, listing);

            foreach (var e in selected)
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
        log.LogInformation("Expanded {Kind} {Id} ({Mode}): {Enqueued}/{Total} enqueued",
            kind, id, scope.Mode, enqueued, total);
    }

    private static IEnumerable<string> Args(string url, string? cookies)
    {
        List<string> a = ["--flat-playlist", "-J", "--no-warnings"];
        if (!string.IsNullOrEmpty(cookies)) { a.Add("--cookies"); a.Add(cookies); }
        a.Add(url);
        return a;
    }
}
