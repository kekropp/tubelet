using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Tubelet.Providers;

/// <summary>Episode thumbnail from the Tubelet server's /cache image host.</summary>
public sealed class EpisodeImageProvider : IRemoteImageProvider
{
    private readonly TubeletClient _client;

    public EpisodeImageProvider(TubeletClient client) => _client = client;

    public string Name => "Tubelet";

    public bool Supports(BaseItem item) => item is Episode;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => [ImageType.Primary];

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var videoId = TubeletIds.VideoId(item);
        if (string.IsNullOrEmpty(videoId)) return [];

        var video = await _client.GetVideoAsync(videoId, cancellationToken).ConfigureAwait(false);
        var url = _client.AbsoluteUrl(video?.Thumb);
        if (url is null) return [];

        return [new RemoteImageInfo { ProviderName = Name, Type = ImageType.Primary, Url = url }];
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        _client.GetImageResponseAsync(url, cancellationToken);
}
