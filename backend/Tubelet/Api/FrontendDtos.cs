using Tubelet.Contracts;

namespace Tubelet.Api;

// DTOs used only by the frontend REST API (/api/v1). Serialized snake_case via
// ApiJsonContext's naming policy — the shared plugin contract types live in
// Tubelet.Contracts with explicitly pinned names.

// Quality is an optional profile for the enqueued jobs: a preset key (directplay|best|720p)
// or "custom:<-f string>"; null/"default" follows the global setting.
public sealed record IntakeRequest(string Url, ScopeRequest? Scope = null, string? Quality = null);

public sealed record IntakeResult(
    string Kind,          // video | playlist | channel | unknown
    string? Id,           // extracted id/handle when recognized
    string Status,        // enqueued | archived | ignored | duplicate | expanding | unrecognized
    string[] Enqueued,
    string[] Skipped);

// Backlog scope chosen after the metadata preview. mode: all | newest | after | none.
// n applies to "newest"; after ("YYYY-MM-DD" or "YYYYMMDD") applies to "after".
public sealed record ScopeRequest(string? Mode, int? N, string? After);

// Metadata-first preview of a channel/playlist before deciding how much to download.
// Either Url (an omnibox paste) or Kind+Id (an already-classified target) identifies the source.
public sealed record PreviewRequest(string? Url, string? Kind, string? Id);

public sealed record PreviewResult(
    string Kind,          // video | playlist | channel | unknown
    string? Id,
    string? Title,
    string? ChannelId,
    string? ChannelName,
    int VideoCount,
    bool HasDates,        // whether YouTube exposed any per-video upload dates (→ "after date" is reliable)
    PreviewEntry[] Sample);

public sealed record PreviewEntry(string Id, string? Title, string? UploadDate);

public sealed record JobDoc(
    long Id, string YoutubeId, string? ChannelId, string? Title, string State,
    int Priority, double Progress, int Attempts, int MaxAttempts,
    string? LastError, string? ErrorKind,
    long AddedAt, long? StartedAt, long? FinishedAt, long? NextRetry,
    string? Thumb);

public sealed record QueueDoc(JobDoc[] Active, JobDoc[] Recent, JobDoc[] Failed, QueueStatsDoc Stats, bool Paused);

// Paginated slice of the queue for the management view.
public sealed record PagedJobs(JobDoc[] Items, long Total, int Page, int PageSize);

// Multi-item / global queue action. Either Ids (explicit selection) or Scope
// ("queued" | "active" | "failed" | "all") targets the jobs; Priority is used by action "priority".
public sealed record QueueBulkRequest(string Action, long[]? Ids, string? Scope, int? Priority);

public sealed record QueueBulkResult(int Affected);

public sealed record PagedVideos(VideoDoc[] Items, long Total, int Page, int PageSize);

public sealed record PriorityRequest(int Priority);

public sealed record ChannelSummary(string Id, string Name, string? Thumb, long VideoCount);

public sealed record DiskDoc(long FreeBytes, long TotalBytes);

public sealed record QueueStatsDoc(int Queued, int Running, int Failed, int Done);

public sealed record SystemDoc(
    string Version, string? YtdlpVersion,
    DiskDoc? Media, DiskDoc? Cache,
    QueueStatsDoc Queue, long? CooldownUntil, long VideoCount, long ChannelCount, bool Paused);

// Cookie-jar status. The jar itself is write-only and never serialized back — only this metadata.
public sealed record CookieStatusDoc(
    bool Present, bool Valid, string? Identity, long? UploadedAt, long? ValidatedAt, string? Message);

// yt-dlp self-update / backup action results (Settings → Maintenance).
public sealed record YtdlpUpdateResult(bool Ok, string? Version, string? Error);
public sealed record BackupResult(bool Ok, string? File, long? Bytes, string? Error);

public sealed record SubscriptionDoc(
    long Id, string Kind, string TargetId, string Cron, string QualityProf,
    string? FilterJson, bool Enabled, long? LastCheck, long? NextCheck);

public sealed record SubscriptionRequest(
    string? Kind, string? TargetId, string? Cron, string? QualityProf, string? FilterJson, bool? Enabled);

public sealed record PlaylistRequest(string Name, string? Description, string[]? Entries);

// Playlist list item for the frontend (the plugin uses the shared PlaylistDoc with full entries).
public sealed record PlaylistSummary(
    string Id, string Name, string Description, string Type, bool Active, int Count, string? Thumb);

// Jellyfin plugin repository manifest (casing fixed by Jellyfin, not ours).
public sealed record RepoPackage(
    [property: System.Text.Json.Serialization.JsonPropertyName("guid")] string Guid,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("description")] string Description,
    [property: System.Text.Json.Serialization.JsonPropertyName("overview")] string Overview,
    [property: System.Text.Json.Serialization.JsonPropertyName("owner")] string Owner,
    [property: System.Text.Json.Serialization.JsonPropertyName("category")] string Category,
    [property: System.Text.Json.Serialization.JsonPropertyName("versions")] RepoVersion[] Versions);

public sealed record RepoVersion(
    [property: System.Text.Json.Serialization.JsonPropertyName("version")] string Version,
    [property: System.Text.Json.Serialization.JsonPropertyName("changelog")] string Changelog,
    [property: System.Text.Json.Serialization.JsonPropertyName("targetAbi")] string TargetAbi,
    [property: System.Text.Json.Serialization.JsonPropertyName("sourceUrl")] string SourceUrl,
    [property: System.Text.Json.Serialization.JsonPropertyName("checksum")] string Checksum,
    [property: System.Text.Json.Serialization.JsonPropertyName("timestamp")] string Timestamp);
