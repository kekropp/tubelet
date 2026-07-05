using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tubelet.Sponsorblock;

/// <summary>
/// SponsorBlock fetch via the k-anonymity endpoint: we send only the first 4 hex chars of
/// sha256(video_id), never the raw id. Response is filtered to configured categories and
/// validated (0 ≤ start &lt; end ≤ duration) before storage.
/// </summary>
public sealed class SbClient(HttpClient http, IConfiguration config)
{
    public const string DefaultEndpoint = "https://sponsor.ajay.app/api/skipSegments/";
    private readonly string _endpoint = config["TUBELET_SB_ENDPOINT"] ?? DefaultEndpoint;

    /// <summary>First 4 hex chars of sha256(id) — the k-anonymity prefix. Pure, for tests.</summary>
    public static string HashPrefix(string youtubeId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(youtubeId));
        return Convert.ToHexStringLower(hash)[..4];
    }

    public static string BuildUrl(string youtubeId) => DefaultEndpoint + HashPrefix(youtubeId);

    /// <summary>
    /// The <c>?categories=[…]</c> query. REQUIRED: without it the SponsorBlock API defaults to
    /// returning only <c>sponsor</c> segments, silently dropping intro/outro/preview/etc.
    /// </summary>
    public static string CategoriesQuery(IReadOnlyCollection<string> categories)
    {
        var arr = "[" + string.Join(",", categories.Select(c => "\"" + c + "\"")) + "]";
        return "?categories=" + Uri.EscapeDataString(arr);
    }

    /// <summary>
    /// Filter a raw prefix-endpoint response to segments for <paramref name="youtubeId"/> whose
    /// category is configured, validate bounds against <paramref name="durationS"/>, and dedupe.
    /// Pure so it can be tested against canned responses.
    /// </summary>
    public static SbSegment[] Parse(string json, string youtubeId, IReadOnlySet<string> categories, double durationS)
    {
        List<SbSegment> outp = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("videoID", out var vid) || vid.GetString() != youtubeId) continue;
                if (!entry.TryGetProperty("segments", out var segs) || segs.ValueKind != JsonValueKind.Array) continue;
                foreach (var s in segs.EnumerateArray())
                {
                    var category = s.TryGetProperty("category", out var c) ? c.GetString() : null;
                    if (category is null || !categories.Contains(category)) continue;
                    if (!s.TryGetProperty("segment", out var pair) || pair.ValueKind != JsonValueKind.Array
                        || pair.GetArrayLength() != 2) continue;
                    var start = pair[0].GetDouble();
                    var end = pair[1].GetDouble();
                    // Reject inverted/degenerate/out-of-range spans. Duration 0 = unknown → skip the ceiling.
                    if (!(start >= 0) || !(end > start)) continue;
                    if (durationS > 0 && end > durationS + 1) continue;
                    outp.Add(new SbSegment(category, Math.Round(start, 3), Math.Round(Math.Min(end, durationS > 0 ? durationS : end), 3)));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return outp.OrderBy(s => s.StartS).ToArray();
    }

    /// <summary>Fetch + parse. Returns an empty array on 404 (no segments) or any network error.</summary>
    public async Task<SbSegment[]> FetchAsync(string youtubeId, IReadOnlySet<string> categories, double durationS,
        CancellationToken ct = default)
    {
        try
        {
            var url = _endpoint + HashPrefix(youtubeId) + CategoriesQuery(categories);
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json, youtubeId, categories, durationS);
        }
        catch (Exception)
        {
            return [];
        }
    }
}
