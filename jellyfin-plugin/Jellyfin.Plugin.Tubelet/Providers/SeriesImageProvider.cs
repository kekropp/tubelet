using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Tubelet.Providers;

/// <summary>Channel poster (Primary), banner and backdrop (tvart) from the Tubelet /cache host.</summary>
public sealed class SeriesImageProvider : IRemoteImageProvider
{
    private readonly TubeletClient _client;

    public SeriesImageProvider(TubeletClient client) => _client = client;

    public string Name => "Tubelet";

    public bool Supports(BaseItem item) => item is Series;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) =>
        [ImageType.Primary, ImageType.Banner, ImageType.Backdrop];

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var channelId = TubeletIds.ChannelId(item);
        if (string.IsNullOrEmpty(channelId)) return [];

        var channel = await _client.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (channel is null) return [];

        var images = new List<RemoteImageInfo>();
        Add(images, ImageType.Primary, channel.Thumb);
        Add(images, ImageType.Banner, channel.Banner);
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
