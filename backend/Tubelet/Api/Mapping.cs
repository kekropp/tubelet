using System.Text.Json;
using Tubelet.Contracts;
using Tubelet.Domain;
using Tubelet.Sponsorblock;

namespace Tubelet.Api;

/// <summary>Row → wire-contract DTO mapping. One code path for the plugin API and the frontend.</summary>
public static class Mapping
{
    public static VideoDoc ToDoc(VideoRow v, Dictionary<string, string> sbMapping) => new(
        Id: v.YoutubeId,
        ChannelId: v.ChannelId,
        Title: v.Title,
        Description: v.Description,
        Published: v.Published,
        DurationS: v.DurationS,
        Tags: ParseJson(v.Tags, ApiJsonContext.Default.StringArray) ?? [],
        Thumb: CacheUrl(v.ThumbPath),
        Chapters: ParseJson(v.Chapters, ApiJsonContext.Default.ChapterDocArray) ?? [],
        Segments: MapSegments(v.Segments, sbMapping));

    public static ChannelDoc ToDoc(ChannelRow c) => new(
        Id: c.ChannelId,
        Name: c.Name,
        Description: c.Description,
        Tags: ParseJson(c.Tags, ApiJsonContext.Default.StringArray) ?? [],
        Thumb: CacheUrl(c.ThumbPath),
        Banner: CacheUrl(c.BannerPath),
        Tvart: CacheUrl(c.TvartPath));

    public static PlaylistDoc ToDoc(PlaylistRow p, string[] entries) => new(
        Id: p.PlaylistId,
        Name: p.Name,
        Description: p.Description,
        Type: p.Type,
        Entries: entries);

    public static SubscriptionDoc ToDoc(SubscriptionRow s) => new(
        Id: s.Id,
        Kind: s.Kind,
        TargetId: s.TargetId,
        Cron: s.Cron,
        QualityProf: s.QualityProf,
        FilterJson: s.FilterJson,
        Enabled: s.Enabled,
        LastCheck: s.LastCheck,
        NextCheck: s.NextCheck);

    public static JobDoc ToDoc(JobRow j) => new(
        j.Id, j.YoutubeId, j.ChannelId, j.Title, j.State,
        j.Priority, j.Progress, j.Attempts, j.MaxAttempts,
        j.LastError, j.ErrorKind,
        j.AddedAt, j.StartedAt, j.FinishedAt, j.NextRetry,
        // Conventional thumb URL (§5). The browser hides it (onerror) until the file exists.
        Thumb: j.YoutubeId.Length > 0 ? $"/cache/videos/{j.YoutubeId[0]}/{j.YoutubeId}.jpg" : null);

    private static SegmentDoc[] MapSegments(string? segmentsJson, Dictionary<string, string> sbMapping)
    {
        var stored = ParseJson(segmentsJson, ApiJsonContext.Default.SbSegmentArray);
        if (stored is null || stored.Length == 0) return [];
        return stored
            .Where(s => sbMapping.ContainsKey(s.Category))
            .Select(s => new SegmentDoc(sbMapping[s.Category], s.StartS, s.EndS))
            .ToArray();
    }

    private static T? ParseJson<T>(string? json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize(json, typeInfo); }
        catch (JsonException) { return null; }
    }

    private static string? CacheUrl(string? relPath) =>
        string.IsNullOrEmpty(relPath) ? null : "/cache/" + relPath.Replace('\\', '/');
}
