using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tubelet.Api;
using Tubelet.Pipeline;

namespace Tubelet.Scheduling;

/// <summary>
/// Optional per-subscription filter (stored as <c>subscriptions.filter_json</c>). Every field is
/// optional; a missing field never filters. Applied against flat-playlist entries whose duration /
/// upload_date are extractor-dependent — when the entry lacks the field a filter that needs it lets
/// the entry through (better to download a maybe-unwanted video than silently drop a wanted one).
/// <see cref="MaxItems"/> caps how many *new* videos a single scan enqueues.
/// </summary>
public sealed record SubscriptionFilter(
    [property: JsonPropertyName("min_duration_s")] long? MinDurationS,
    [property: JsonPropertyName("max_duration_s")] long? MaxDurationS,
    [property: JsonPropertyName("title_regex")] string? TitleRegex,
    [property: JsonPropertyName("date_floor")] string? DateFloor,   // "YYYYMMDD" (yt-dlp upload_date form)
    [property: JsonPropertyName("max_items")] int? MaxItems)
{
    public static readonly SubscriptionFilter None = new(null, null, null, null, null);

    public static SubscriptionFilter Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try { return JsonSerializer.Deserialize(json, ApiJsonContext.Default.SubscriptionFilter) ?? None; }
        catch (JsonException) { return None; }
    }

    /// <summary>Effective cap on new enqueues per scan (>=1), or int.MaxValue when unset.</summary>
    public int Cap => MaxItems is > 0 ? MaxItems.Value : int.MaxValue;

    public bool Accepts(FlatEntry e)
    {
        if (e.DurationS is { } dur)
        {
            if (MinDurationS is { } min && dur < min) return false;
            if (MaxDurationS is { } max && dur > max) return false;
        }
        if (!string.IsNullOrEmpty(DateFloor) && e.UploadDate is { Length: 8 } up
            && string.CompareOrdinal(up, DateFloor) < 0) return false;
        if (!string.IsNullOrWhiteSpace(TitleRegex) && !string.IsNullOrEmpty(e.Title))
        {
            var rx = Compiled(TitleRegex);
            if (rx is not null && !rx.IsMatch(e.Title)) return false;
        }
        return true;
    }

    private static readonly Dictionary<string, Regex?> _cache = new();

    /// <summary>Compile a user regex once (case-insensitive, time-limited). Invalid patterns match nothing-filtered.</summary>
    private static Regex? Compiled(string pattern)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(pattern, out var cached)) return cached;
            Regex? rx;
            try { rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { rx = null; }
            _cache[pattern] = rx;
            return rx;
        }
    }
}
