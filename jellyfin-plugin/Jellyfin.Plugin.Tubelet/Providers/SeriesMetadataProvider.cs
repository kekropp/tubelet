using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tubelet.Providers;

/// <summary>Channel → Jellyfin Series metadata. Identity comes from the folder name (channel id).</summary>
public sealed class SeriesMetadataProvider : IRemoteMetadataProvider<Series, SeriesInfo>
{
    private readonly TubeletClient _client;
    private readonly ILogger<SeriesMetadataProvider> _logger;

    public SeriesMetadataProvider(TubeletClient client, ILogger<SeriesMetadataProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    public string Name => "Tubelet";

    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Series> { HasMetadata = false };

        var channelId = TubeletIds.ChannelId(info);
        if (string.IsNullOrEmpty(channelId)) return result;

        var channel = await _client.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            _logger.LogDebug("No Tubelet channel for {ChannelId}", channelId);
            return result;
        }

        result.HasMetadata = true;
        result.Item = new Series
        {
            Name = channel.Name,
            Overview = channel.Description,
            Tags = channel.Tags,
        };
        result.Item.SetProviderId(Plugin.ProviderKey, channelId);

        // Stamp the poster directly on the item (like the TubeArchivist plugin does): it then shows
        // up on any metadata refresh, without depending on a separate remote-image download pass.
        var thumbUrl = _client.AbsoluteUrl(channel.Thumb);
        if (thumbUrl is not null)
            result.Item.ImageInfos = [new ItemImageInfo { Path = thumbUrl, Type = ImageType.Primary }];
        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        var channelId = TubeletIds.ChannelId(searchInfo);
        if (string.IsNullOrEmpty(channelId))
            return Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        var result = new RemoteSearchResult { Name = searchInfo.Name, SearchProviderName = Name };
        result.SetProviderId(Plugin.ProviderKey, channelId);
        return Task.FromResult<IEnumerable<RemoteSearchResult>>([result]);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        _client.GetImageResponseAsync(url, cancellationToken);
}
