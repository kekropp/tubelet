using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Tubelet.Providers;

/// <summary>
/// Year-season metadata: stamps the channel avatar as the season poster so "Season 2026" shows the
/// channel image instead of a gray tile. Seasons are virtual (no path), so the channel resolves via
/// the parent series' provider id, which Jellyfin passes along in <see cref="SeasonInfo"/>.
/// Name/index are left to Jellyfin; only the image is contributed.
/// </summary>
public sealed class SeasonMetadataProvider : IRemoteMetadataProvider<Season, SeasonInfo>
{
    private readonly TubeletClient _client;

    public SeasonMetadataProvider(TubeletClient client) => _client = client;

    public string Name => "Tubelet";

    public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Season> { HasMetadata = false };

        if (info.SeriesProviderIds is null ||
            !info.SeriesProviderIds.TryGetValue(Plugin.ProviderKey, out var channelId) ||
            string.IsNullOrEmpty(channelId))
            return result;

        var channel = await _client.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        var thumbUrl = _client.AbsoluteUrl(channel?.Thumb);
        if (thumbUrl is null) return result;

        result.HasMetadata = true;
        result.Item = new Season
        {
            Name = info.Name,
            IndexNumber = info.IndexNumber,
            ImageInfos = [new ItemImageInfo { Path = thumbUrl, Type = ImageType.Primary }],
        };
        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken) =>
        Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        _client.GetImageResponseAsync(url, cancellationToken);
}
