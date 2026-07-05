using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tubelet.Contracts;

namespace Jellyfin.Plugin.Tubelet;

/// <summary>
/// Typed client for the Tubelet server's <c>/api/jf/v1</c> batch + delta API and its
/// <c>/cache</c> image host. The base URL is read fresh from plugin configuration on
/// every call so a server-URL change in the dashboard takes effect without a restart.
/// </summary>
public sealed class TubeletClient
{
    /// <summary>
    /// The wire deserializer for every Tubelet response. Contracts DTOs pin their own
    /// [JsonPropertyName], so Web (case-insensitive) defaults round-trip them exactly.
    /// Exposed so the server-side round-trip test can assert against the identical config.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TubeletClient> _logger;

    public TubeletClient(IHttpClientFactory httpClientFactory, ILogger<TubeletClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Configured server base URL, trailing slashes trimmed. Empty when unconfigured.</summary>
    public string BaseUrl => (Plugin.Instance?.Configuration.ServerUrl ?? string.Empty).TrimEnd('/');

    /// <summary>Turn a server-relative path (e.g. <c>/cache/videos/d/x.jpg</c>) into an absolute URL.</summary>
    public string? AbsoluteUrl(string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        if (relative.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return relative;
        return BaseUrl + (relative.StartsWith('/') ? relative : "/" + relative);
    }

    private HttpClient Http()
    {
        var client = _httpClientFactory.CreateClient(nameof(TubeletClient));
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    public async Task<VideoDoc?> GetVideoAsync(string youtubeId, CancellationToken ct)
    {
        var docs = await GetVideosAsync([youtubeId], ct).ConfigureAwait(false);
        return docs.FirstOrDefault(d => d.Id == youtubeId);
    }

    public async Task<IReadOnlyList<VideoDoc>> GetVideosAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0 || BaseUrl.Length == 0) return [];
        var url = $"{BaseUrl}/api/jf/v1/videos?ids={Join(ids)}";
        return await GetJsonAsync<VideoDoc[]>(url, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<ChannelDoc?> GetChannelAsync(string channelId, CancellationToken ct)
    {
        if (BaseUrl.Length == 0) return null;
        var url = $"{BaseUrl}/api/jf/v1/channels?ids={Uri.EscapeDataString(channelId)}";
        var docs = await GetJsonAsync<ChannelDoc[]>(url, ct).ConfigureAwait(false);
        return docs?.FirstOrDefault(d => d.Id == channelId);
    }

    /// <summary>Delta since <paramref name="cursor"/>. Returns null when the server answers 204 (nothing new).</summary>
    public async Task<ChangesDoc?> GetChangesAsync(string cursor, CancellationToken ct)
    {
        if (BaseUrl.Length == 0) return null;
        var url = $"{BaseUrl}/api/jf/v1/changes";
        if (!string.IsNullOrEmpty(cursor)) url += $"?since={Uri.EscapeDataString(cursor)}";

        using var resp = await Http().GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ChangesDoc>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>Fetch an image (or any URL) as an HTTP response — used by the image providers.</summary>
    public Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken ct) =>
        Http().GetAsync(url, ct);

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http().GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NoContent) return default;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Tubelet request failed: {Url}", url);
            return default;
        }
    }

    private static string Join(IEnumerable<string> ids) =>
        string.Join(',', ids.Select(Uri.EscapeDataString));
}
