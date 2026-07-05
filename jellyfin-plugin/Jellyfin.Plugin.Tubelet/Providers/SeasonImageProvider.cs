using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Tubelet.Providers;

/// <summary>
/// Year-season artwork. Seasons are virtual (no path of their own), so identity resolves through
/// the parent Series (the channel folder); each season reuses the channel's art — poster from the
/// avatar, backdrop from the banner-derived tvart — so "Season 2026" isn't a blank tile.
/// </summary>
public sealed class SeasonImageProvider : IRemoteImageProvider
{
    private readonly TubeletClient _client;

    public SeasonImageProvider(TubeletClient client) => _client = client;

    public string Name => "Tubelet";

    public bool Supports(BaseItem item) => item is Season;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) =>
        [ImageType.Primary, ImageType.Backdrop];

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var series = (item as Season)?.Series;
        var channelId = series is null ? null : TubeletIds.ChannelId(series);
        if (string.IsNullOrEmpty(channelId)) return [];

        var channel = await _client.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (channel is null) return [];

        var images = new List<RemoteImageInfo>();
        Add(images, ImageType.Primary, channel.Thumb);
        Add(images, ImageType.Backdrop, channel.Tvart);
        return images;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        _client.GetImageResponseAsync(url, cancellationToken);

    private void Add(List<RemoteImageInfo> images, ImageType type, string? relative)
    {
        var url = _client.AbsoluteUrl(relative);
        if (url is not null)
            images.Add(new RemoteImageInfo { ProviderName = Name, Type = type, Url = url });
    }
}
