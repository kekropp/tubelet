using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Tubelet.Contracts;

namespace Tubelet.Pipeline;

/// <summary>Parsed yt-dlp infojson — the fields Tubelet maps into channels/videos rows.</summary>
public sealed record VideoMeta(
    string Id,
    string ChannelId,
    string ChannelName,
    string Title,
    string Description,
    string PublishedIso,     // ISO 8601 UTC
    long DurationS,
    string[] Tags,
    ChapterDoc[] Chapters,
    string? ThumbnailUrl,    // best video thumbnail
    string? ChannelAvatarUrl,
    string? ChannelBannerUrl,
    string RawJson);         // full infojson (gzip'd into videos.info_json)

/// <summary>
/// Channel-page metadata (description + avatar/banner URLs). These fields exist only in the
/// channel-tab infojson — a video's infojson never carries them.
/// </summary>
public sealed record ChannelPage(string? Description, string? AvatarUrl, string? BannerUrl);

/// <summary>Per-download tuning pulled from Settings → Network/Quality (+ cookies if configured).</summary>
/// <param name="FormatOverride">Replaces the default <c>-f</c> selector (see <see cref="YtDlp.FallbackFormat"/>);
/// null uses <see cref="YtDlp.DefaultFormat"/>.</param>
public sealed record DownloadArgs(
    int ConcurrentFragments, string? LimitRate, int SleepRequests, int SleepInterval,
    int MaxSleepInterval, string? CookiesFile,
    bool WriteSubs = false, string? SubLangs = null, bool EmbedThumbnail = false,
    IReadOnlyList<string>? ExtraArgs = null, string? FormatOverride = null);

/// <summary>
/// yt-dlp subprocess wrapper: single metadata call (-J → infojson) and a streaming download
/// with the exact flags from DESIGN §4.2. Cancellation kills the process tree (Proc), so the
/// .part file survives for --continue on the next attempt.
/// </summary>
public sealed class YtDlp(YtDlpLocator locator, AppPaths paths, ILogger<YtDlp>? log = null)
{
    /// <summary>
    /// Default format selector: prefer AVC video + m4a audio (direct-play, no transcode), else the best
    /// separate video+audio of any codec, else the best pre-muxed stream.
    /// </summary>
    public const string DefaultFormat = "bestvideo[vcodec^=avc1]+bestaudio[ext=m4a]/bestvideo*+bestaudio/best";

    /// <summary>
    /// Permissive fallback for "Requested format is not available": grab whatever the extractor offers —
    /// best separate video+audio, else best pre-muxed, else the single best stream even if it is
    /// video-only or audio-only. The postprocess ffmpeg pass transcodes it into a valid Jellyfin mp4.
    /// </summary>
    public const string FallbackFormat = "bestvideo*+bestaudio/best/best*";

    /// <summary>The output template the download uses; the produced file is incomplete/&lt;id&gt;.&lt;ext&gt;.</summary>
    public string IncompleteTemplate => Path.Combine(paths.IncompleteDir, "%(id)s.%(ext)s");

    /// <summary>
    /// Canonical watch URL for a video id. yt-dlp must be handed a URL, not a bare id: YouTube ids
    /// may begin with '-' (e.g. "--n1s8IoOuc"), which yt-dlp otherwise parses as a CLI option and
    /// then aborts with "You must provide at least one URL".
    /// </summary>
    public static string WatchUrl(string id) => "https://www.youtube.com/watch?v=" + id;

    /// <summary>Locate the merged file yt-dlp produced for <paramref name="id"/> (prefers .mp4).</summary>
    public string? FindDownloaded(string id)
    {
        var mp4 = Path.Combine(paths.IncompleteDir, id + ".mp4");
        if (File.Exists(mp4)) return mp4;
        return Directory.EnumerateFiles(paths.IncompleteDir, id + ".*")
            .FirstOrDefault(f => !f.EndsWith(".part", StringComparison.Ordinal)
                              && !f.EndsWith(".ytdl", StringComparison.Ordinal));
    }

    public async Task<VideoMeta> FetchMetadataAsync(string id, string? cookiesFile,
        IReadOnlyList<string>? extraArgs = null, CancellationToken ct = default)
    {
        List<string> args = ["-J", "--no-warnings", "--no-playlist"];
        if (!string.IsNullOrEmpty(cookiesFile)) { args.Add("--cookies"); args.Add(cookiesFile); }
        if (extraArgs is { Count: > 0 }) args.AddRange(extraArgs);
        args.Add(WatchUrl(id));

        log?.LogDebug("yt-dlp meta {Id}: {Cmd}", id, Proc.Render(locator.Path, args));
        var sw = Stopwatch.StartNew();
        var r = await Proc.RunAsync(locator.Path, args, ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || r.Stdout.Trim().Length == 0)
        {
            log?.LogWarning("yt-dlp meta {Id} failed (exit {Code}, {Ms} ms, stdout {Bytes} bytes) — stderr tail:\n{Stderr}",
                id, r.ExitCode, sw.ElapsedMilliseconds, r.Stdout.Length, Proc.Tail(r.Stderr, 10));
            throw new YtDlpException(r.ExitCode, r.Stderr);
        }
        log?.LogDebug("yt-dlp meta {Id} ok ({Ms} ms, {Bytes} bytes infojson)", id, sw.ElapsedMilliseconds, r.Stdout.Length);
        return ParseInfoJson(r.Stdout);
    }

    /// <summary>
    /// Fetch a channel's page metadata: description and avatar/banner art URLs. Uses a flat-playlist
    /// listing capped at one entry, so it costs a single page hit. Only the channel-tab root JSON
    /// carries these fields; video infojsons never do.
    /// </summary>
    public async Task<ChannelPage> FetchChannelPageAsync(string channelId, string? cookiesFile,
        IReadOnlyList<string>? extraArgs = null, CancellationToken ct = default)
    {
        List<string> args = ["-J", "--flat-playlist", "--playlist-end", "1", "--no-warnings"];
        if (!string.IsNullOrEmpty(cookiesFile)) { args.Add("--cookies"); args.Add(cookiesFile); }
        if (extraArgs is { Count: > 0 }) args.AddRange(extraArgs);
        args.Add(YtSources.Channel(channelId));

        var r = await Proc.RunAsync(locator.Path, args, ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || r.Stdout.Trim().Length == 0)
            throw new YtDlpException(r.ExitCode, r.Stderr);
        return ParseChannelPage(r.Stdout);
    }

    /// <summary>Parse the channel-tab root JSON into <see cref="ChannelPage"/>. Pure, for tests.</summary>
    public static ChannelPage ParseChannelPage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new ChannelPage(
            Description: Str(r, "description"),
            AvatarUrl: ThumbById(r, "avatar_uncropped") ?? ThumbById(r, "avatar"),
            BannerUrl: ThumbById(r, "banner_uncropped") ?? ThumbById(r, "banner"));
    }

    /// <summary>
    /// Download one video. Progress JSON lines are parsed off stdout and handed to
    /// <paramref name="onProgress"/> (unthrottled — the caller throttles before broadcasting).
    /// Returns the raw yt-dlp result; the caller classifies failures via <see cref="RetryPolicy"/>.
    /// </summary>
    public async Task<ProcResult> DownloadAsync(string id, DownloadArgs opt, Action<DownloadProgress> onProgress,
        CancellationToken ct = default)
    {
        List<string> args =
        [
            "-f", opt.FormatOverride ?? DefaultFormat,
            "--merge-output-format", "mp4",
            // Embed YouTube chapters as native mp4 chapter markers. Our postprocess remux uses -c copy,
            // which preserves container chapters, so they reach Jellyfin without a metadata provider.
            "--embed-chapters",
            "--continue", "--part",
            "--concurrent-fragments", opt.ConcurrentFragments.ToString(CultureInfo.InvariantCulture),
            "--retries", "10", "--fragment-retries", "10", "--retry-sleep", "exp=1:120",
            "--sleep-requests", opt.SleepRequests.ToString(CultureInfo.InvariantCulture),
            "--sleep-interval", opt.SleepInterval.ToString(CultureInfo.InvariantCulture),
            "--max-sleep-interval", opt.MaxSleepInterval.ToString(CultureInfo.InvariantCulture),
            "--no-warnings", "--no-playlist",
            "--progress-template", "%(progress)j", "--newline", "--no-colors",
            "-o", IncompleteTemplate,
        ];
        if (!string.IsNullOrEmpty(opt.LimitRate)) { args.Add("--limit-rate"); args.Add(opt.LimitRate); }
        if (!string.IsNullOrEmpty(opt.CookiesFile)) { args.Add("--cookies"); args.Add(opt.CookiesFile); }
        if (opt.WriteSubs)
        {
            // Sidecar .srt next to the mp4 (Jellyfin picks these up) — robust across our remux, unlike
            // in-container embedding which the postprocess would drop.
            args.AddRange(["--write-subs", "--write-auto-subs", "--convert-subs", "srt",
                           "--sub-langs", string.IsNullOrWhiteSpace(opt.SubLangs) ? "en.*" : opt.SubLangs]);
        }
        if (opt.EmbedThumbnail) args.Add("--embed-thumbnail");
        if (opt.ExtraArgs is { Count: > 0 }) args.AddRange(opt.ExtraArgs);
        args.Add(WatchUrl(id));

        log?.LogInformation("yt-dlp download {Id}: {Cmd}", id, Proc.Render(locator.Path, args));
        var sw = Stopwatch.StartNew();
        var r = await Proc.StreamAsync(locator.Path, args, line =>
        {
            if (YtDlpProgress.TryParse(line, out var p)) onProgress(p);
        }, stderrRingLines: 50, ct: ct).ConfigureAwait(false);

        if (r.ExitCode == 0)
            log?.LogInformation("yt-dlp download {Id} finished (exit 0) in {Elapsed}", id, sw.Elapsed);
        else
            log?.LogWarning("yt-dlp download {Id} failed (exit {Code}) after {Elapsed} — stderr tail:\n{Stderr}",
                id, r.ExitCode, sw.Elapsed, Proc.Tail(r.Stderr, 15));
        return r;
    }

    /// <summary>
    /// Validate a cookie jar with a metadata-only call against an auth-only feed
    /// (<c>/feed/subscriptions</c>): success means the session is logged in. No download.
    /// </summary>
    public async Task<CookieCheck> ValidateCookiesAsync(string cookiesFile, CancellationToken ct = default)
    {
        List<string> args =
        [
            "--cookies", cookiesFile, "--flat-playlist", "--playlist-end", "1",
            "--no-warnings", "-J", "https://www.youtube.com/feed/subscriptions",
        ];
        ProcResult r;
        try { r = await Proc.RunAsync(locator.Path, args, ct).ConfigureAwait(false); }
        catch (Exception e) { return new CookieCheck(false, null, e.Message); }

        if (r.ExitCode == 0 && r.Stdout.Trim().Length > 0)
        {
            var identity = TryChannelName(r.Stdout);
            return new CookieCheck(true, identity, null);
        }
        return new CookieCheck(false, null, LastError(r.Stderr) ?? "yt-dlp could not use the cookie jar");
    }

    private static string? TryChannelName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Str(doc.RootElement, "channel") ?? Str(doc.RootElement, "uploader") ?? Str(doc.RootElement, "title");
        }
        catch (JsonException) { return null; }
    }

    private static string? LastError(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;
        string? last = null;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) last = line;
        }
        return last;
    }

    // ---- infojson mapping --------------------------------------------------

    public static VideoMeta ParseInfoJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var id = Str(r, "id") ?? throw new FormatException("infojson missing id");
        var channelId = Str(r, "channel_id") ?? Str(r, "uploader_id") ?? "unknown";
        var channelName = Str(r, "channel") ?? Str(r, "uploader") ?? channelId;
        var title = Str(r, "title") ?? id;
        var description = Str(r, "description") ?? "";
        var duration = (long)(Num(r, "duration") ?? 0);

        return new VideoMeta(
            Id: id,
            ChannelId: channelId,
            ChannelName: channelName,
            Title: title,
            Description: description,
            PublishedIso: Published(r),
            DurationS: duration,
            Tags: StrArray(r, "tags"),
            Chapters: Chapters(r),
            ThumbnailUrl: BestThumb(r),
            ChannelAvatarUrl: ThumbById(r, "avatar_uncropped") ?? ThumbById(r, "avatar"),
            ChannelBannerUrl: ThumbById(r, "banner_uncropped") ?? ThumbById(r, "banner"),
            RawJson: json);
    }

    private static string Published(JsonElement r)
    {
        var ts = Num(r, "timestamp") ?? Num(r, "release_timestamp");
        if (ts is not null)
            return DateTimeOffset.FromUnixTimeSeconds((long)ts).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var d = Str(r, "upload_date");
        if (d is { Length: 8 } && DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return DateTimeOffset.UnixEpoch.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    private static ChapterDoc[] Chapters(JsonElement r)
    {
        if (!r.TryGetProperty("chapters", out var c) || c.ValueKind != JsonValueKind.Array) return [];
        List<ChapterDoc> outp = [];
        foreach (var ch in c.EnumerateArray())
        {
            var title = ch.TryGetProperty("title", out var t) ? t.GetString() : null;
            var start = ch.TryGetProperty("start_time", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : (double?)null;
            if (title is not null && start is not null) outp.Add(new ChapterDoc(title, start.Value));
        }
        return outp.ToArray();
    }

    private static string? BestThumb(JsonElement r)
    {
        // Highest-resolution entry in "thumbnails", else the single "thumbnail" field.
        if (r.TryGetProperty("thumbnails", out var ts) && ts.ValueKind == JsonValueKind.Array)
        {
            string? best = null;
            double bestScore = -1;
            foreach (var t in ts.EnumerateArray())
            {
                var url = t.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (url is null) continue;
                var w = t.TryGetProperty("width", out var wv) && wv.ValueKind == JsonValueKind.Number ? wv.GetDouble() : 0;
                var h = t.TryGetProperty("height", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetDouble() : 0;
                var pref = t.TryGetProperty("preference", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetDouble() : 0;
                var score = w * h + pref; // area, tie-broken by preference
                if (score > bestScore) { bestScore = score; best = url; }
            }
            if (best is not null) return best;
        }
        return Str(r, "thumbnail");
    }

    private static string? ThumbById(JsonElement r, string id)
    {
        if (!r.TryGetProperty("thumbnails", out var ts) || ts.ValueKind != JsonValueKind.Array) return null;
        foreach (var t in ts.EnumerateArray())
            if (t.TryGetProperty("id", out var i) && i.GetString() == id
                && t.TryGetProperty("url", out var u)) return u.GetString();
        return null;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string[] StrArray(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();
    }
}

/// <summary>Result of a cookie-jar validation (metadata-only, no download).</summary>
public sealed record CookieCheck(bool Ok, string? Identity, string? Error);

public sealed class YtDlpException(int exitCode, string? stderr) : Exception($"yt-dlp exited {exitCode}")
{
    public int ExitCode { get; } = exitCode;
    public string Stderr { get; } = stderr ?? "";
}
