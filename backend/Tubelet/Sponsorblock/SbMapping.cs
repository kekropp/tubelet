using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Tubelet.Api;
using Tubelet.Data;

namespace Tubelet.Sponsorblock;

/// <summary>Stored shape of a SponsorBlock segment in videos.segments.</summary>
public sealed record SbSegment(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("start_s")] double StartS,
    [property: JsonPropertyName("end_s")] double EndS);

/// <summary>Settings → SponsorBlock section (settings key "section:sponsorblock").</summary>
public sealed record SbSettings(
    [property: JsonPropertyName("categories")] string[]? Categories,
    [property: JsonPropertyName("mapping")] Dictionary<string, string>? Mapping);

/// <summary>
/// Category → Jellyfin MediaSegmentType mapping, applied server-side when serving
/// video docs so the plugin stays dumb and the mapping is editable in one place.
/// </summary>
public static class SbMapping
{
    public static readonly Dictionary<string, string> Default = new()
    {
        ["sponsor"] = "Commercial",
        ["intro"] = "Intro",
        ["outro"] = "Outro",
        ["preview"] = "Preview",
        ["filler"] = "Preview",
        ["interaction"] = "Recap",
        ["selfpromo"] = "Recap",
    };

    public static readonly string[] DefaultCategories =
        ["sponsor", "selfpromo", "interaction", "intro", "outro", "preview", "music_offtopic"];

    /// <summary>Configured SponsorBlock categories to fetch, falling back to defaults.</summary>
    public static IReadOnlySet<string> LoadCategories(SqliteConnection conn)
    {
        var raw = Database.GetSetting(conn, "section:sponsorblock");
        if (raw is not null)
            try
            {
                var s = JsonSerializer.Deserialize(raw, ApiJsonContext.Default.SbSettings);
                if (s?.Categories is { Length: > 0 } c) return c.ToHashSet();
            }
            catch (JsonException) { }
        return DefaultCategories.ToHashSet();
    }

    /// <summary>Load the configured mapping, falling back to defaults on absence or bad JSON.</summary>
    public static Dictionary<string, string> Load(SqliteConnection conn)
    {
        var raw = Database.GetSetting(conn, "section:sponsorblock");
        if (raw is null) return Default;
        try
        {
            var settings = JsonSerializer.Deserialize(raw, ApiJsonContext.Default.SbSettings);
            return settings?.Mapping is { Count: > 0 } m ? m : Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }
}
