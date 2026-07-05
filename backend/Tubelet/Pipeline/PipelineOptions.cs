using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Tubelet.Api;
using Tubelet.Data;

namespace Tubelet.Pipeline;

/// <summary>
/// Settings → Network section (settings key "section:network"). All fields optional;
/// <see cref="NetworkOptions.Resolved"/> applies defaults so a missing/partial blob is fine.
/// Changes apply live — the coordinator/RateGate re-read on each job.
/// </summary>
public sealed record NetworkOptions(
    [property: JsonPropertyName("ops_per_hour")] int? OpsPerHour,
    [property: JsonPropertyName("download_workers")] int? DownloadWorkers,
    [property: JsonPropertyName("concurrent_fragments")] int? ConcurrentFragments,
    [property: JsonPropertyName("sleep_requests")] int? SleepRequests,
    [property: JsonPropertyName("sleep_interval")] int? SleepInterval,
    [property: JsonPropertyName("max_sleep_interval")] int? MaxSleepInterval,
    [property: JsonPropertyName("limit_rate")] string? LimitRate)
{
    public static readonly NetworkOptions Defaults =
        new(OpsPerHour: 30, DownloadWorkers: 2, ConcurrentFragments: 4,
            SleepRequests: 1, SleepInterval: 3, MaxSleepInterval: 8, LimitRate: null);

    /// <summary>This instance with any null field filled from <see cref="Defaults"/>, then clamped to sane ranges.</summary>
    public NetworkOptions Resolved() => new(
        OpsPerHour: Math.Clamp(OpsPerHour ?? Defaults.OpsPerHour!.Value, 1, 100_000),
        DownloadWorkers: Math.Clamp(DownloadWorkers ?? Defaults.DownloadWorkers!.Value, 1, 16),
        ConcurrentFragments: Math.Clamp(ConcurrentFragments ?? Defaults.ConcurrentFragments!.Value, 1, 16),
        SleepRequests: Math.Max(0, SleepRequests ?? Defaults.SleepRequests!.Value),
        SleepInterval: Math.Max(0, SleepInterval ?? Defaults.SleepInterval!.Value),
        MaxSleepInterval: Math.Max(0, MaxSleepInterval ?? Defaults.MaxSleepInterval!.Value),
        LimitRate: string.IsNullOrWhiteSpace(LimitRate) ? Defaults.LimitRate : LimitRate);

    public static NetworkOptions Load(SqliteConnection conn) => LoadRaw(Database.GetSetting(conn, "section:network"));

    public static NetworkOptions LoadRaw(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return Defaults;
        try
        {
            return (JsonSerializer.Deserialize(raw, ApiJsonContext.Default.NetworkOptions) ?? Defaults).Resolved();
        }
        catch (JsonException)
        {
            return Defaults;
        }
    }
}

/// <summary>Settings → Quality section (settings key "section:quality").</summary>
public sealed record QualityOptions(
    [property: JsonPropertyName("profile")] string? Profile,
    // Hardware transcode: auto (detect a GPU, else libx264) | none (always libx264) | vaapi | qsv | nvenc.
    [property: JsonPropertyName("hwaccel")] string? Hwaccel = null,
    [property: JsonPropertyName("embed_subs")] bool? EmbedSubs = null,
    [property: JsonPropertyName("embed_thumbnail")] bool? EmbedThumbnail = null,
    [property: JsonPropertyName("sub_langs")] string? SubLangs = null)
{
    public static readonly QualityOptions Defaults = new(Profile: "compat", Hwaccel: "auto");

    /// <summary>compat = transcode incompatible streams; quality = remux only (rely on client direct-play).</summary>
    public string ResolvedProfile() =>
        string.Equals(Profile, "quality", StringComparison.OrdinalIgnoreCase) ? "quality" : "compat";

    /// <summary>Requested hwaccel mode, normalized (auto|none|vaapi|qsv|nvenc). Unknown → auto.</summary>
    public string ResolvedHwaccel() => (Hwaccel ?? "auto").ToLowerInvariant() switch
    {
        "none" or "off" or "libx264" or "cpu" => "none",
        "vaapi" => "vaapi",
        "qsv" => "qsv",
        "nvenc" or "nvidia" or "cuda" => "nvenc",
        _ => "auto",
    };

    public bool WantsSubs => EmbedSubs == true;
    public bool WantsThumbnail => EmbedThumbnail == true;
    public string ResolvedSubLangs() => string.IsNullOrWhiteSpace(SubLangs) ? "en.*" : SubLangs!.Trim();

    public static QualityOptions Load(SqliteConnection conn)
    {
        var raw = Database.GetSetting(conn, "section:quality");
        if (string.IsNullOrEmpty(raw)) return Defaults;
        try { return JsonSerializer.Deserialize(raw, ApiJsonContext.Default.QualityOptions) ?? Defaults; }
        catch (JsonException) { return Defaults; }
    }
}

/// <summary>
/// Settings → Maintenance section (settings key "section:maintenance"). Cron strings drive the
/// scheduler's SB-refresh / yt-dlp self-update / janitor lanes; all optional with sane defaults.
/// </summary>
public sealed record MaintenanceOptions(
    [property: JsonPropertyName("sb_refresh_cron")] string? SbRefreshCron,
    [property: JsonPropertyName("ytdlp_update_cron")] string? YtdlpUpdateCron,
    [property: JsonPropertyName("janitor_cron")] string? JanitorCron,
    [property: JsonPropertyName("part_ttl_days")] int? PartTtlDays,
    [property: JsonPropertyName("backup_enabled")] bool? BackupEnabled,
    [property: JsonPropertyName("backup_keep")] int? BackupKeep,
    [property: JsonPropertyName("ytdlp_autoupdate")] bool? YtdlpAutoUpdate,
    [property: JsonPropertyName("po_token_enabled")] bool? PoTokenEnabled)
{
    public static readonly MaintenanceOptions Defaults = new(
        SbRefreshCron: "0 4 * * 0",     // weekly, Sunday 04:00 UTC
        YtdlpUpdateCron: "0 5 * * 1",   // weekly, Monday 05:00 UTC
        JanitorCron: "0 3 * * *",       // nightly 03:00 UTC
        PartTtlDays: 7,
        BackupEnabled: true,
        BackupKeep: 7,
        YtdlpAutoUpdate: true,
        PoTokenEnabled: false);

    public MaintenanceOptions Resolved() => new(
        SbRefreshCron: Valid(SbRefreshCron) ?? Defaults.SbRefreshCron,
        YtdlpUpdateCron: Valid(YtdlpUpdateCron) ?? Defaults.YtdlpUpdateCron,
        JanitorCron: Valid(JanitorCron) ?? Defaults.JanitorCron,
        PartTtlDays: Math.Clamp(PartTtlDays ?? Defaults.PartTtlDays!.Value, 1, 365),
        BackupEnabled: BackupEnabled ?? Defaults.BackupEnabled,
        BackupKeep: Math.Clamp(BackupKeep ?? Defaults.BackupKeep!.Value, 1, 100),
        YtdlpAutoUpdate: YtdlpAutoUpdate ?? Defaults.YtdlpAutoUpdate,
        PoTokenEnabled: PoTokenEnabled ?? Defaults.PoTokenEnabled);

    private static string? Valid(string? cron) => Tubelet.Scheduling.CronSchedule.IsValid(cron) ? cron : null;

    public static MaintenanceOptions Load(SqliteConnection conn)
    {
        var raw = Database.GetSetting(conn, "section:maintenance");
        if (string.IsNullOrEmpty(raw)) return Defaults;
        try { return (JsonSerializer.Deserialize(raw, ApiJsonContext.Default.MaintenanceOptions) ?? Defaults).Resolved(); }
        catch (JsonException) { return Defaults; }
    }
}
