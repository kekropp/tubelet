using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Tubelet.Api;
using Tubelet.Data;
using Tubelet.Domain;
using Tubelet.Realtime;
using Tubelet.Sponsorblock;

namespace Tubelet.Pipeline;

/// <summary>
/// The download pipeline (DESIGN §4.2). N download workers claim jobs from the SQLite queue and
/// run meta→download; a single postprocess lane runs convert→index so it never blocks a download
/// slot. All durable state is in the jobs/videos tables — nothing here survives a restart except
/// the .part files on disk, which yt-dlp resumes.
/// </summary>
public sealed class DownloadCoordinator(
    Database db, AppPaths paths, YtDlp ytdlp, Ffmpeg ffmpeg, MediaArt art, SbClient sb,
    Broadcaster bc, PipelineSignal signal, JobControl control, RateGate rateGate,
    IConfiguration config, ILogger<DownloadCoordinator> log) : BackgroundService
{
    private const int CooldownSeconds = 1800;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly Channel<PostItem> _post = Channel.CreateUnbounded<PostItem>();
    private int _activeDownloads;

    private sealed record PostItem(JobRow Job, VideoMeta Meta, string InputPath, string? ThumbRel);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var postLoop = Task.Run(() => PostProcessLoop(stoppingToken), stoppingToken);
        try { await ClaimLoop(stoppingToken); }
        finally
        {
            _post.Writer.TryComplete();
            await postLoop.ConfigureAwait(false);
        }
    }

    // ---- claim loop --------------------------------------------------------

    private async Task ClaimLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var net = LoadNetwork();
                rateGate.Reconfigure(net.OpsPerHour!.Value);
                var maxWorkers = net.DownloadWorkers!.Value;

                var cooldown = CooldownRemaining();
                if (cooldown > TimeSpan.Zero)
                {
                    await signal.WaitAsync(Min(cooldown, PollInterval), ct).ConfigureAwait(false);
                    continue;
                }

                // Operator-toggled global pause: in-flight jobs run to completion, but claim nothing new.
                if (QueuePaused())
                {
                    await signal.WaitAsync(PollInterval, ct).ConfigureAwait(false);
                    continue;
                }

                while (Volatile.Read(ref _activeDownloads) < maxWorkers)
                {
                    JobRow? job;
                    using (var conn = db.Open())
                        job = Queries.ClaimNextJob(conn, Now());
                    if (job is null) break;

                    Interlocked.Increment(ref _activeDownloads);
                    _ = Task.Run(() => RunDownload(job, ct), ct);
                }

                await signal.WaitAsync(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogError(e, "claim loop error");
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
    }

    // ---- download stage (per worker) ---------------------------------------

    private async Task RunDownload(JobRow job, CancellationToken stoppingToken)
    {
        using var reg = control.Register(job.Id, stoppingToken);
        var ct = reg.Token;
        try
        {
            var cookies = CookiesFile();
            var extra = PoTokenArgs();
            if (job.Priority > 1) await rateGate.WaitAsync(ct).ConfigureAwait(false); // pastes bypass the bucket

            BroadcastState(job.Id);

            var meta = await ytdlp.FetchMetadataAsync(job.YoutubeId, cookies, extra, ct).ConfigureAwait(false);
            var thumbRel = await HandleMeta(job, meta, ct).ConfigureAwait(false);

            SetState(job.Id, JobStates.Downloading);
            BroadcastState(job.Id);
            log.LogInformation("Downloading {Yt} — {Title} ({Duration}s, channel {Channel})",
                job.YoutubeId, meta.Title, meta.DurationS, meta.ChannelId);

            var net = LoadNetwork();
            var quality = LoadQualityOptions();
            var args = new DownloadArgs(net.ConcurrentFragments!.Value, net.LimitRate,
                net.SleepRequests!.Value, net.SleepInterval!.Value, net.MaxSleepInterval!.Value, cookies,
                WriteSubs: quality.WantsSubs, SubLangs: quality.ResolvedSubLangs(),
                EmbedThumbnail: quality.WantsThumbnail, ExtraArgs: extra,
                FormatOverride: FormatPresets.ResolveJobFormat(job.Format, quality));

            long lastBroadcast = 0;
            double lastPersisted = 0;
            void OnProgress(DownloadProgress p)
            {
                if (p.Status != "downloading") return;
                var now = Environment.TickCount64;
                if (now - lastBroadcast >= 250) // ≤4 Hz per job
                {
                    lastBroadcast = now;
                    _ = bc.JobProgress(job.Id, job.YoutubeId, p.Pct, p.SpeedText, p.EtaText);
                }
                if (p.Pct - lastPersisted >= 0.05) // persist coarsely
                {
                    lastPersisted = p.Pct;
                    PersistProgress(job.Id, p.Pct);
                    log.LogDebug("{Yt} downloading: {Pct:P0} ({Speed}, ETA {Eta})",
                        job.YoutubeId, p.Pct, p.SpeedText ?? "?", p.EtaText ?? "?");
                }
            }

            var result = await ytdlp.DownloadAsync(job.YoutubeId, args, OnProgress, ct).ConfigureAwait(false);

            // Format-selection miss (e.g. no AVC/pre-muxed rendition): retry once grabbing whatever the
            // extractor offers — the postprocess ffmpeg pass transcodes it into a valid mp4 (DESIGN §4.2).
            if (result.ExitCode != 0 && RetryPolicy.IsFormatUnavailable(result.Stderr))
            {
                log.LogInformation("Requested format unavailable for {Yt} — retrying with grab-anything selector", job.YoutubeId);
                result = await ytdlp.DownloadAsync(job.YoutubeId, args with { FormatOverride = YtDlp.FallbackFormat }, OnProgress, ct)
                    .ConfigureAwait(false);
            }

            if (result.ExitCode != 0) { HandleFailure(job, result.ExitCode, result.Stderr); return; }

            var input = ytdlp.FindDownloaded(job.YoutubeId);
            if (input is null)
            {
                var leftovers = Directory.EnumerateFiles(paths.IncompleteDir, job.YoutubeId + ".*")
                    .Select(Path.GetFileName).ToArray();
                log.LogError("yt-dlp exited 0 for {Yt} but no merged file was found — incomplete/ contains: [{Files}]",
                    job.YoutubeId, leftovers.Length > 0 ? string.Join(", ", leftovers) : "nothing for this id");
                HandleFailure(job, 1, "yt-dlp reported success but produced no file");
                return;
            }

            log.LogInformation("Downloaded {Yt} → {File} ({Size:N0} bytes); queueing for convert (postprocess backlog: {Backlog})",
                job.YoutubeId, Path.GetFileName(input), new FileInfo(input).Length, _post.Reader.Count);
            SetState(job.Id, JobStates.Converting);
            BroadcastState(job.Id);
            await _post.Writer.WriteAsync(new PostItem(job, meta, input, thumbRel), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Job cancel (row already deleted by the endpoint) or shutdown (restart recovery requeues).
            if (!stoppingToken.IsCancellationRequested)
                log.LogInformation("Job {Id} ({Yt}) cancelled", job.Id, job.YoutubeId);
        }
        catch (YtDlpException ye) { HandleFailure(job, ye.ExitCode, ye.Stderr); }
        catch (Exception e)
        {
            log.LogWarning(e, "download error for {Yt}", job.YoutubeId);
            HandleFailure(job, -1, e.Message);
        }
        finally
        {
            control.Unregister(job.Id);
            Interlocked.Decrement(ref _activeDownloads);
            await BroadcastQueueStats().ConfigureAwait(false);
            signal.Signal();
        }
    }

    /// <summary>Upsert the channel (+page meta when new/incomplete), update the job's title, save the video thumb.</summary>
    private async Task<string?> HandleMeta(JobRow job, VideoMeta meta, CancellationToken ct)
    {
        bool needsChannelMeta;
        using (var conn = db.Open())
        {
            conn.Execute("UPDATE jobs SET title = @title, channel_id = @cid WHERE id = @id",
                new { title = meta.Title, cid = meta.ChannelId, id = job.Id });
            using var tx = conn.BeginTransaction();
            Queries.UpsertChannel(conn, tx, new ChannelRow
            {
                ChannelId = meta.ChannelId,
                Name = meta.ChannelName,
                Description = "", // video infojsons never carry the channel description (see RefreshChannelMeta)
                Tags = "[]",
                LastRefresh = Now(),
            });
            tx.Commit();
            // New channel, or an existing row still missing art/description (backfills old libraries).
            needsChannelMeta = conn.ExecuteScalar<long>(
                "SELECT count(*) FROM channels WHERE channel_id = @cid AND (thumb_path IS NULL OR description = '')",
                new { cid = meta.ChannelId }) > 0;
        }

        var thumbRel = await art.SaveVideoThumbAsync(job.YoutubeId, meta.ThumbnailUrl, ct).ConfigureAwait(false);

        if (needsChannelMeta) await RefreshChannelMeta(meta, ct).ConfigureAwait(false);
        BroadcastState(job.Id);
        return thumbRel;
    }

    /// <summary>
    /// Fetch the channel page for description + avatar/banner (none of which exist in a video's
    /// infojson) and store what came back. Best-effort: a failed page fetch falls back to whatever
    /// the video infojson offered and never fails the download.
    /// </summary>
    private async Task RefreshChannelMeta(VideoMeta meta, CancellationToken ct)
    {
        var page = new ChannelPage(null, null, null);
        try
        {
            page = await ytdlp.FetchChannelPageAsync(meta.ChannelId, CookiesFile(), PoTokenArgs(), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("channel page fetch failed for {Channel}: {Err}", meta.ChannelId, e.Message);
        }

        var (t, b, tv) = await art.SaveChannelArtAsync(meta.ChannelId,
            page.AvatarUrl ?? meta.ChannelAvatarUrl, page.BannerUrl ?? meta.ChannelBannerUrl, ct).ConfigureAwait(false);

        using (var conn = db.Open())
            conn.Execute("""
                UPDATE channels SET
                    description = COALESCE(NULLIF(@d, ''), description),
                    thumb_path  = COALESCE(@t, thumb_path),
                    banner_path = COALESCE(@b, banner_path),
                    tvart_path  = COALESCE(@tv, tvart_path)
                WHERE channel_id = @cid
                """, new { d = page.Description ?? "", t, b, tv, cid = meta.ChannelId });
        await BroadcastChannel(meta.ChannelId).ConfigureAwait(false);
    }

    // ---- postprocess lane --------------------------------------------------

    private async Task PostProcessLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _post.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await PostProcess(item, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    log.LogWarning(e, "postprocess error for {Yt}", item.Job.YoutubeId);
                    HandleFailure(item.Job, -1, "post-processing failed: " + e.Message);
                    await BroadcastQueueStats().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task PostProcess(PostItem item, CancellationToken ct)
    {
        var (job, meta, input, thumbRel) = item;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var quality = LoadQualityOptions();
        var probe = await ffmpeg.ProbeAsync(input, ct).ConfigureAwait(false);
        var plan = Ffmpeg.Decide(probe.Vcodec, probe.Acodec, probe.Container, quality.ResolvedProfile());
        var hwaccel = ffmpeg.ResolveHwaccel(quality.ResolvedHwaccel());
        var output = Path.Combine(paths.IncompleteDir, job.YoutubeId + ".final.mp4");
        // Keep/Remux with an embedded thumbnail: copy all streams so yt-dlp's cover art survives.
        var copyAll = quality.WantsThumbnail && plan != ConversionPlan.Transcode;
        log.LogInformation(
            "Converting {Yt}: input={File} container={Container} v={V} a={A} {W}x{H} dur={Dur:F1}s (expected {Expected}s) → plan={Plan} profile={Profile} hwaccel={Hw} copyAll={CopyAll}",
            job.YoutubeId, Path.GetFileName(input), probe.Container, probe.Vcodec ?? "none", probe.Acodec ?? "none",
            probe.Width, probe.Height, probe.DurationS, meta.DurationS, plan, quality.ResolvedProfile(), hwaccel, copyAll);
        await ffmpeg.ConvertAsync(input, output, plan, hwaccel, copyAll, ct).ConfigureAwait(false);

        if (!await ffmpeg.VerifyAsync(output, meta.DurationS, ct).ConfigureAwait(false))
        {
            TryDelete(output);
            throw new InvalidOperationException(
                $"output failed ffprobe sanity check (plan {plan}, expected ~{meta.DurationS}s — see 'verify … REJECTED' log line above for the reason)");
        }
        var final = await ffmpeg.ProbeAsync(output, ct).ConfigureAwait(false);

        SetState(job.Id, JobStates.Indexing);
        BroadcastState(job.Id);

        var rel = Path.Combine(meta.ChannelId, job.YoutubeId + ".mp4");
        var dest = Path.Combine(paths.MediaDir, rel);
        MoveInto(output, dest);
        var size = new FileInfo(dest).Length;

        // Subtitle sidecars (Settings → Quality → embed subs): move any incomplete/<id>*.srt next to
        // the mp4 as <id>.<lang>.srt so Jellyfin picks them up. Done before CleanupIncomplete wipes them.
        MoveSubtitleSidecars(job.YoutubeId, Path.GetDirectoryName(dest)!);

        var cats = SbCategories();
        var segs = await sb.FetchAsync(job.YoutubeId, cats, meta.DurationS, ct).ConfigureAwait(false);

        var row = new VideoRow
        {
            YoutubeId = job.YoutubeId,
            ChannelId = meta.ChannelId,
            Title = meta.Title,
            Description = meta.Description,
            Published = meta.PublishedIso,
            DurationS = meta.DurationS,
            Tags = JsonSerializer.Serialize(meta.Tags, ApiJsonContext.Default.StringArray),
            Chapters = meta.Chapters.Length > 0 ? JsonSerializer.Serialize(meta.Chapters, ApiJsonContext.Default.ChapterDocArray) : null,
            MediaPath = rel.Replace('\\', '/'),
            MediaSize = size,
            Width = final.Width,
            Height = final.Height,
            Vcodec = final.Vcodec,
            Acodec = final.Acodec,
            ThumbPath = thumbRel,
            Segments = segs.Length > 0 ? JsonSerializer.Serialize(segs, ApiJsonContext.Default.SbSegmentArray) : null,
            SbRefreshed = segs.Length > 0 ? Now() : null,
            DownloadedAt = Now(),
            InfoJson = Gzip(meta.RawJson),
        };

        using (var conn = db.Open())
        {
            using var tx = conn.BeginTransaction();
            Queries.UpsertVideo(conn, tx, row, meta.ChannelName);
            conn.Execute("UPDATE jobs SET state = 'done', progress = 1, finished_at = @now WHERE id = @id",
                new { now = Now(), id = job.Id }, tx);
            tx.Commit();
            ClearExtractorBreakage(conn); // a clean index means the extractor is working again
        }

        CleanupIncomplete(job.YoutubeId, input);

        Dictionary<string, string> sbMapping;
        using (var conn = db.Open()) sbMapping = SbMapping.Load(conn);
        await bc.VideoAdded(Mapping.ToDoc(row, sbMapping)).ConfigureAwait(false);
        BroadcastState(job.Id);
        await BroadcastQueueStats().ConfigureAwait(false);
        signal.Signal();
        log.LogInformation("Indexed {Yt} → {Rel} ({Plan}, {Size:N0} bytes, v={V} a={A} {W}x{H}, postprocess took {Elapsed})",
            job.YoutubeId, rel, plan, size, final.Vcodec, final.Acodec, final.Width, final.Height, sw.Elapsed);
    }

    // ---- failure handling --------------------------------------------------

    private void HandleFailure(JobRow job, int exitCode, string? stderr)
    {
        var f = RetryPolicy.Classify(exitCode, stderr);
        var now = Now();

        var willRetry = f.Kind == ErrorKind.Transient && job.Attempts < job.MaxAttempts;
        log.LogWarning(
            "Job {Id} ({Yt}) failed: kind={Kind} exit={Exit} attempt={Attempt}/{Max} → {Outcome} — {Reason}",
            job.Id, job.YoutubeId, f.Kind, exitCode, job.Attempts, job.MaxAttempts,
            f.Kind == ErrorKind.Throttled ? "queue cooldown" : willRetry ? "will retry" : "permanent failure",
            f.Reason);
        if (!string.IsNullOrWhiteSpace(stderr))
            log.LogWarning("stderr tail for {Yt}:\n{Stderr}", job.YoutubeId, Proc.Tail(stderr, 15));

        using var conn = db.Open();

        NoteExtractorBreakage(conn, stderr);

        switch (f.Kind)
        {
            case ErrorKind.Throttled:
                // Pause the whole queue; don't burn this job's attempt.
                Database.SetSetting(conn, "cooldown_until", (now + CooldownSeconds).ToString());
                conn.Execute("""
                    UPDATE jobs SET state = 'queued', attempts = MAX(0, attempts - 1),
                        next_retry = @retry, last_error = @err, error_kind = 'throttled'
                    WHERE id = @id
                    """, new { retry = now + CooldownSeconds, err = f.Reason, id = job.Id });
                _ = bc.SystemBanner("cooldown", "YouTube throttling detected — queue paused. Consider adding cookies (Settings → Cookies).");
                break;

            case ErrorKind.Permanent:
                conn.Execute("""
                    UPDATE jobs SET state = 'failed', last_error = @err, error_kind = 'permanent',
                        finished_at = @now WHERE id = @id
                    """, new { err = f.Reason, now, id = job.Id });
                break;

            default: // Transient — back off and retry until max_attempts.
                if (job.Attempts < job.MaxAttempts)
                {
                    var backoff = (long)RetryPolicy.Backoff(job.Attempts, Jitter(job.Id)).TotalSeconds;
                    conn.Execute("""
                        UPDATE jobs SET state = 'queued', next_retry = @retry,
                            last_error = @err, error_kind = 'transient' WHERE id = @id
                        """, new { retry = now + backoff, err = f.Reason, id = job.Id });
                }
                else
                {
                    conn.Execute("""
                        UPDATE jobs SET state = 'failed', last_error = @err, error_kind = 'transient',
                            finished_at = @now WHERE id = @id
                        """, new { err = f.Reason, now, id = job.Id });
                }
                break;
        }
        BroadcastState(conn, job.Id);
    }

    // ---- settings / helpers ------------------------------------------------

    private NetworkOptions LoadNetwork()
    {
        using var conn = db.Open();
        return NetworkOptions.Load(conn);
    }

    private QualityOptions LoadQualityOptions()
    {
        using var conn = db.Open();
        return QualityOptions.Load(conn);
    }

    /// <summary>
    /// Optional PO-token provider passthrough (Settings → Maintenance, off by default). When enabled,
    /// forwards the operator-supplied <c>TUBELET_POT_EXTRACTOR_ARGS</c> to yt-dlp so a bundled
    /// bgutil-pot provider plugin can supply tokens. Empty when disabled or unconfigured.
    /// </summary>
    private IReadOnlyList<string>? PoTokenArgs()
    {
        using var conn = db.Open();
        if (!MaintenanceOptions.Load(conn).PoTokenEnabled!.Value) return null;
        var extractorArgs = config["TUBELET_POT_EXTRACTOR_ARGS"];
        return string.IsNullOrWhiteSpace(extractorArgs) ? null : ["--extractor-args", extractorArgs];
    }

    private void MoveSubtitleSidecars(string id, string destDir)
    {
        foreach (var srt in Directory.EnumerateFiles(paths.IncompleteDir, id + "*.srt"))
        {
            try
            {
                var name = Path.GetFileName(srt); // <id>.<lang>.srt (or <id>.srt)
                File.Move(srt, Path.Combine(destDir, name), overwrite: true);
            }
            catch (IOException e) { log.LogWarning(e, "could not move subtitle sidecar {File}", srt); }
        }
    }

    private IReadOnlySet<string> SbCategories()
    {
        using var conn = db.Open();
        return SbMapping.LoadCategories(conn);
    }

    // yt-dlp breakage signalling: several consecutive "unable to extract" failures usually mean
    // YouTube changed something and yt-dlp needs updating. We set a flag the scheduler's self-update
    // lane consumes (DESIGN §9). A clean index resets the counter.
    private const int ExtractFailThreshold = 3;

    private void NoteExtractorBreakage(SqliteConnection conn, string? stderr)
    {
        if (stderr is null) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(stderr,
                @"unable to extract|unable to download webpage|player response|nsig extraction",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return;
        var count = 1 + (int.TryParse(Database.GetSetting(conn, "extract_fail_count"), out var c) ? c : 0);
        Database.SetSetting(conn, "extract_fail_count", count.ToString());
        if (count >= ExtractFailThreshold && MaintenanceOptions.Load(conn).YtdlpAutoUpdate!.Value)
        {
            Database.SetSetting(conn, "ytdlp_update_requested", "1");
            log.LogWarning("{Count} consecutive extractor failures — requested yt-dlp self-update", count);
        }
    }

    private void ClearExtractorBreakage(SqliteConnection conn) =>
        Database.SetSetting(conn, "extract_fail_count", "0");

    private bool QueuePaused()
    {
        using var conn = db.Open();
        return Database.GetSetting(conn, "queue_paused") == "1";
    }

    private TimeSpan CooldownRemaining()
    {
        using var conn = db.Open();
        var raw = Database.GetSetting(conn, "cooldown_until");
        if (long.TryParse(raw, out var until))
        {
            var remaining = until - Now();
            if (remaining > 0) return TimeSpan.FromSeconds(remaining);
        }
        return TimeSpan.Zero;
    }

    private string? CookiesFile()
    {
        var f = Path.Combine(paths.CookiesDir, "cookies.txt");
        return File.Exists(f) ? f : null;
    }

    private void SetState(long id, string state)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE jobs SET state = @state WHERE id = @id", new { state, id });
    }

    private void PersistProgress(long id, double pct)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE jobs SET progress = @pct WHERE id = @id", new { pct, id });
    }

    private void BroadcastState(long id)
    {
        using var conn = db.Open();
        BroadcastState(conn, id);
    }

    private void BroadcastState(SqliteConnection conn, long id)
    {
        var row = conn.QuerySingleOrDefault<JobRow>("SELECT * FROM jobs WHERE id = @id", new { id });
        if (row is not null) _ = bc.JobState(Mapping.ToDoc(row));
    }

    private async Task BroadcastChannel(string channelId)
    {
        using var conn = db.Open();
        var row = conn.QuerySingleOrDefault<ChannelRow>(
            "SELECT * FROM channels WHERE channel_id = @channelId", new { channelId });
        if (row is not null) await bc.ChannelAdded(Mapping.ToDoc(row)).ConfigureAwait(false);
    }

    private async Task BroadcastQueueStats()
    {
        using var conn = db.Open();
        var s = Queries.QueueStats(conn);
        await bc.QueueStats(new QueueStatsDoc(s.Queued, s.Running, s.Failed, s.Done)).ConfigureAwait(false);
    }

    private void CleanupIncomplete(string id, string input)
    {
        TryDelete(input);
        foreach (var f in Directory.EnumerateFiles(paths.IncompleteDir, id + ".*"))
            TryDelete(f);
    }

    private static void MoveInto(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try
        {
            File.Move(src, dest, overwrite: true); // atomic rename on the same filesystem
        }
        catch (IOException)
        {
            // Cross-device (volumes on different mounts): copy + fsync + rename.
            var tmp = dest + ".tmp";
            using (var from = File.OpenRead(src))
            using (var to = File.Create(tmp))
            {
                from.CopyTo(to);
                to.Flush(flushToDisk: true);
            }
            File.Move(tmp, dest, overwrite: true);
            TryDelete(src);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static byte[] Gzip(string s)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
            gz.Write(Encoding.UTF8.GetBytes(s));
        return ms.ToArray();
    }

    private static double Jitter(long id) => (id * 2654435761L % 1000) / 1000.0; // deterministic per job
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
