using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Tubelet.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.Tubelet.Segments;

/// <summary>
/// SponsorBlock media segments. The server already filters by the configured categories and
/// pre-maps each to a Jellyfin segment-type name, so the plugin only parses the string to the
/// <see cref="MediaSegmentType"/> enum and converts seconds → ticks.
/// </summary>
public sealed class TubeletSegmentProvider : IMediaSegmentProvider
{
    private readonly TubeletClient _client;
    private readonly ILibraryManager _libraryManager;

    public TubeletSegmentProvider(TubeletClient client, ILibraryManager libraryManager)
    {
        _client = client;
        _libraryManager = libraryManager;
    }

    public string Name => "Tubelet";

    public ValueTask<bool> Supports(BaseItem item) =>
        ValueTask.FromResult(item is Episode && TubeletIds.VideoId(item) is { Length: > 0 });

    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(request.ItemId);
        var videoId = item is null ? null : TubeletIds.VideoId(item);
        if (string.IsNullOrEmpty(videoId)) return [];

        var video = await _client.GetVideoAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (video is null || video.Segments.Length == 0) return [];

        var segments = new List<MediaSegmentDto>(video.Segments.Length);
        foreach (var s in video.Segments)
        {
            if (!Enum.TryParse<MediaSegmentType>(s.Type, ignoreCase: true, out var type) ||
                type == MediaSegmentType.Unknown)
                continue;
            if (s.EndS <= s.StartS) continue;

            segments.Add(new MediaSegmentDto
            {
                ItemId = request.ItemId,
                Type = type,
                StartTicks = (long)(s.StartS * TimeSpan.TicksPerSecond),
                EndTicks = (long)(s.EndS * TimeSpan.TicksPerSecond),
            });
        }

        return segments;
    }
}
