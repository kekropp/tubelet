using System.Text.Json;
using System.Text.Json.Serialization;
using Tubelet.Contracts;
using Tubelet.Pipeline;
using Tubelet.Sponsorblock;

namespace Tubelet.Api;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
// contracts (shared with the Jellyfin plugin)
[JsonSerializable(typeof(VideoDoc))]
[JsonSerializable(typeof(VideoDoc[]))]
[JsonSerializable(typeof(ChannelDoc[]))]
[JsonSerializable(typeof(ChapterDoc[]))]
[JsonSerializable(typeof(SegmentDoc[]))]
[JsonSerializable(typeof(ChangesDoc))]
[JsonSerializable(typeof(PlaylistDoc[]))]
// frontend api
[JsonSerializable(typeof(IntakeRequest))]
[JsonSerializable(typeof(IntakeResult))]
[JsonSerializable(typeof(ScopeRequest))]
[JsonSerializable(typeof(PreviewRequest))]
[JsonSerializable(typeof(PreviewResult))]
[JsonSerializable(typeof(QueueDoc))]
[JsonSerializable(typeof(PagedJobs))]
[JsonSerializable(typeof(QueueBulkRequest))]
[JsonSerializable(typeof(QueueBulkResult))]
[JsonSerializable(typeof(JobDoc))]
[JsonSerializable(typeof(JobDoc[]))]
[JsonSerializable(typeof(PagedVideos))]
[JsonSerializable(typeof(PriorityRequest))]
[JsonSerializable(typeof(ChannelSummary[]))]
[JsonSerializable(typeof(SystemDoc))]
[JsonSerializable(typeof(CookieStatusDoc))]
[JsonSerializable(typeof(YtdlpUpdateResult))]
[JsonSerializable(typeof(BackupResult))]
[JsonSerializable(typeof(SubscriptionDoc))]
[JsonSerializable(typeof(SubscriptionDoc[]))]
[JsonSerializable(typeof(SubscriptionRequest))]
[JsonSerializable(typeof(PlaylistRequest))]
[JsonSerializable(typeof(PlaylistDoc))]
[JsonSerializable(typeof(PlaylistSummary))]
[JsonSerializable(typeof(PlaylistSummary[]))]
// plugin repo manifest
[JsonSerializable(typeof(RepoPackage[]))]
// stored-JSON shapes
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(SbSegment[]))]
[JsonSerializable(typeof(SbSettings))]
[JsonSerializable(typeof(Tubelet.Scheduling.SubscriptionFilter))]
[JsonSerializable(typeof(NetworkOptions))]
[JsonSerializable(typeof(QualityOptions))]
[JsonSerializable(typeof(MaintenanceOptions))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
public partial class ApiJsonContext : JsonSerializerContext;
