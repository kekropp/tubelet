namespace Tubelet.Domain;

// Row models mapped 1:1 from SQLite by Dapper (snake_case columns → PascalCase
// via DefaultTypeMap.MatchNamesWithUnderscores).

public sealed class VideoRow
{
    public long Rid { get; set; }              // sqlite rowid, selected as "rowid AS rid" (FTS key)
    public string YoutubeId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Published { get; set; } = "";
    public long DurationS { get; set; }
    public string Tags { get; set; } = "[]";
    public string? Chapters { get; set; }
    public string MediaPath { get; set; } = "";
    public long? MediaSize { get; set; }
    public long? Width { get; set; }
    public long? Height { get; set; }
    public string? Vcodec { get; set; }
    public string? Acodec { get; set; }
    public string? ThumbPath { get; set; }
    public string? Segments { get; set; }
    public long? SbRefreshed { get; set; }
    public long DownloadedAt { get; set; }
    public long ChangedAt { get; set; }
    public byte[]? InfoJson { get; set; }      // gzip'd full yt-dlp infojson (re-index later)
}

public sealed class ChannelRow
{
    public string ChannelId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tags { get; set; } = "[]";
    public string? ThumbPath { get; set; }
    public string? BannerPath { get; set; }
    public string? TvartPath { get; set; }
    public long? LastRefresh { get; set; }
}

public sealed class PlaylistRow
{
    public string PlaylistId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ChannelId { get; set; }
    public string? ChannelName { get; set; }
    public string Description { get; set; } = "";
    public string Type { get; set; } = "regular";
    public bool Active { get; set; }
    public string? ThumbPath { get; set; }
    public long ChangedAt { get; set; }
}

public sealed class JobRow
{
    public long Id { get; set; }
    public string YoutubeId { get; set; } = "";
    public string? ChannelId { get; set; }
    public string? Title { get; set; }
    public string State { get; set; } = "queued";
    public int Priority { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public long? NextRetry { get; set; }
    public string? LastError { get; set; }
    public string? ErrorKind { get; set; }
    public double Progress { get; set; }
    public long AddedAt { get; set; }
    public long? StartedAt { get; set; }
    public long? FinishedAt { get; set; }
}

public sealed class SubscriptionRow
{
    public long Id { get; set; }
    public string Kind { get; set; } = "channel";
    public string TargetId { get; set; } = "";
    public string Cron { get; set; } = "0 */6 * * *";
    public string QualityProf { get; set; } = "default";
    public string? FilterJson { get; set; }
    public bool Enabled { get; set; }
    public long? LastCheck { get; set; }
    public long? NextCheck { get; set; }
}

public static class JobStates
{
    public const string Queued = "queued";
    public const string FetchingMeta = "fetching_meta";
    public const string Downloading = "downloading";
    public const string Converting = "converting";
    public const string Indexing = "indexing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Paused = "paused";

    /// <summary>States that occupy (or wait for) a worker slot — shown as the active queue.</summary>
    public static readonly string[] Active = [Queued, FetchingMeta, Downloading, Converting, Indexing, Paused];

    /// <summary>States a crashed worker can leave behind; reset to queued on startup.</summary>
    public static readonly string[] Stuck = [FetchingMeta, Downloading, Converting, Indexing];
}
