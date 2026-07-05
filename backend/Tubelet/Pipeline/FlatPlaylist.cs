using System.Text.Json;

namespace Tubelet.Pipeline;

public sealed record FlatEntry(
    string Id, string? Title, string? ChannelId, string? ChannelName,
    long? DurationS = null, string? UploadDate = null);

/// <summary>Parsed <c>yt-dlp --flat-playlist -J</c> output: the container + its flattened video entries.</summary>
public sealed record FlatListing(
    string? Id, string? Title, string? ChannelId, string? ChannelName, string? Description, FlatEntry[] Entries);

/// <summary>
/// Parses flat-playlist JSON. Channels come back as nested tab playlists, so entries are
/// gathered recursively; only 11-char YouTube video ids are kept. Pure, for tests.
/// </summary>
public static class FlatPlaylist
{
    public static FlatListing Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        List<FlatEntry> entries = [];
        Collect(r, entries);
        return new FlatListing(
            Id: Str(r, "id"),
            Title: Str(r, "title"),
            ChannelId: Str(r, "channel_id") ?? Str(r, "uploader_id"),
            ChannelName: Str(r, "channel") ?? Str(r, "uploader"),
            Description: Str(r, "description"),
            Entries: entries.ToArray());
    }

    private static void Collect(JsonElement node, List<FlatEntry> outp)
    {
        if (!node.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return;
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (e.TryGetProperty("entries", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                Collect(e, outp); // channel tab / nested playlist
                continue;
            }
            var id = Str(e, "id");
            if (id is not { Length: 11 }) continue;
            if (outp.Any(x => x.Id == id)) continue; // dedupe across tabs
            outp.Add(new FlatEntry(id, Str(e, "title"),
                Str(e, "channel_id") ?? Str(e, "uploader_id"),
                Str(e, "channel") ?? Str(e, "uploader"),
                // flat-playlist fields are extractor-dependent and often absent; used best-effort by filters.
                DurationS: Num(e, "duration"),
                UploadDate: Str(e, "upload_date")));
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? (long)Math.Round(d) : null;
}
