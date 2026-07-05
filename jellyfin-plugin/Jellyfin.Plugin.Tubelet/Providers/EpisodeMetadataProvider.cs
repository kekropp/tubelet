using System.Globalization;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Tubelet.Contracts;

namespace Jellyfin.Plugin.Tubelet.Providers;

/// <summary>
/// Video → Jellyfin Episode metadata. Identity is the filename stem (video id). Videos are
/// grouped into year "seasons" (ParentIndexNumber = upload year); within a year they order
/// chronologically by PremiereDate. No IndexNumber is assigned — Jellyfin would render it as
/// an "N." prefix on every episode title (the TubeArchivist plugin's default is the same).
/// </summary>
public sealed class EpisodeMetadataProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>
{
    private readonly TubeletClient _client;
    private readonly ILogger<EpisodeMetadataProvider> _logger;

    public EpisodeMetadataProvider(TubeletClient client, ILogger<EpisodeMetadataProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    public string Name => "Tubelet";

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Episode> { HasMetadata = false };

        var videoId = TubeletIds.VideoId(info);
        if (string.IsNullOrEmpty(videoId)) return result;

        var video = await _client.GetVideoAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (video is null)
        {
            _logger.LogDebug("No Tubelet video for {VideoId}", videoId);
            return result;
        }

        var episode = new Episode
        {
            Name = video.Title,
            Overview = video.Description,
            Tags = video.Tags,
            RunTimeTicks = video.DurationS > 0 ? video.DurationS * TimeSpan.TicksPerSecond : null,
        };
        episode.SetProviderId(Plugin.ProviderKey, videoId);

        if (TryParsePublished(video.Published, out var published))
        {
            episode.PremiereDate = published;
            episode.ProductionYear = published.Year;
            episode.ParentIndexNumber = published.Year;          // year season
        }

        result.HasMetadata = true;
        result.Item = episode;
        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
    {
        var videoId = TubeletIds.VideoId(searchInfo);
        if (string.IsNullOrEmpty(videoId))
            return Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        var result = new RemoteSearchResult { Name = searchInfo.Name, SearchProviderName = Name };
        result.SetProviderId(Plugin.ProviderKey, videoId);
        return Task.FromResult<IEnumerable<RemoteSearchResult>>([result]);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        _client.GetImageResponseAsync(url, cancellationToken);

    private static bool TryParsePublished(string? published, out DateTime value)
    {
        value = default;
        if (string.IsNullOrEmpty(published)) return false;
        if (!DateTime.TryParse(published, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
            return false;
        value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return true;
    }
}
