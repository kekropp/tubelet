using System.Text.Json.Serialization;

namespace Tubelet.Contracts;

// Wire contract for /api/jf/v1 (and reused by the frontend API).
// Both the server and the Jellyfin plugin compile against these types,
// so the contract cannot drift. Property names are pinned explicitly —
// they must never depend on ambient serializer naming policy.

public sealed record ChapterDoc(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("start_s")] double StartS);

public sealed record SegmentDoc(
    [property: JsonPropertyName("type")] string Type,       // Jellyfin MediaSegmentType name, pre-mapped server-side
    [property: JsonPropertyName("start_s")] double StartS,
    [property: JsonPropertyName("end_s")] double EndS);

public sealed record VideoDoc(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("channel_id")] string ChannelId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("published")] string Published,  // ISO 8601 full timestamp
    [property: JsonPropertyName("duration_s")] long DurationS,
    [property: JsonPropertyName("tags")] string[] Tags,
    [property: JsonPropertyName("thumb")] string? Thumb,
    [property: JsonPropertyName("chapters")] ChapterDoc[] Chapters,
    [property: JsonPropertyName("segments")] SegmentDoc[] Segments);

public sealed record ChannelDoc(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("tags")] string[] Tags,
    [property: JsonPropertyName("thumb")] string? Thumb,
    [property: JsonPropertyName("banner")] string? Banner,
    [property: JsonPropertyName("tvart")] string? Tvart);

public sealed record PlaylistDoc(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("type")] string Type,        // regular | custom
    [property: JsonPropertyName("entries")] string[] Entries);

public sealed record ChangesDoc(
    [property: JsonPropertyName("videos")] string[] Videos,          // ids whose docs changed — refetch via batch GET
    [property: JsonPropertyName("playlists")] PlaylistDoc[] Playlists,
    [property: JsonPropertyName("next_cursor")] string NextCursor);
