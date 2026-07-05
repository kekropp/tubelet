using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Tubelet.Contracts;

namespace Jellyfin.Plugin.Tubelet.Sync;

/// <summary>
/// Tubelet → Jellyfin delta sync. Polls <c>/api/jf/v1/changes</c> from a persisted cursor and
/// applies two things: a metadata refresh for videos whose server-side doc changed (edited
/// titles, freshly fetched SponsorBlock segments), and Tubelet playlists onto Jellyfin
/// collections. No playback/watched state — Jellyfin owns that. The cursor lives in plugin
/// config so restarts resume instead of re-walking the whole library.
/// </summary>
public sealed class TubeletSyncTask : IScheduledTask
{
    private readonly TubeletClient _client;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<TubeletSyncTask> _logger;

    public TubeletSyncTask(
        TubeletClient client,
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<TubeletSyncTask> logger)
    {
        _client = client;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public string Name => "Sync from Tubelet";
    public string Key => "TubeletSync";
    public string Description => "Refresh changed video metadata and playlists from the Tubelet server.";
    public string Category => "Tubelet";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(15).Ticks,
        },
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrEmpty(_client.BaseUrl))
        {
            _logger.LogInformation("Tubelet server URL not configured; skipping sync");
            progress.Report(100);
            return;
        }

        var cursor = config.SyncCursor;
        // The server returns everything since the cursor in one response; this loop guards
        // against a paginating server and stops as soon as the cursor stops advancing.
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changes = await _client.GetChangesAsync(cursor, cancellationToken).ConfigureAwait(false);
            if (changes is null) break;                 // 204 — nothing new

            RefreshChangedVideos(changes.Videos);
            await ApplyPlaylistsAsync(changes.Playlists, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(changes.NextCursor) || changes.NextCursor == cursor) break;
            cursor = changes.NextCursor;

            config.SyncCursor = cursor;
            Plugin.Instance!.SaveConfiguration();
        }

        progress.Report(100);
    }

    private void RefreshChangedVideos(IReadOnlyList<string> videoIds)
    {
        if (videoIds.Count == 0) return;

        foreach (var videoId in videoIds)
        {
            foreach (var item in FindByProviderId(videoId, BaseItemKind.Episode))
            {
                var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                };
                _providerManager.QueueRefresh(item.Id, options, RefreshPriority.Normal);
            }
        }
    }

    private async Task ApplyPlaylistsAsync(IReadOnlyList<PlaylistDoc> playlists, CancellationToken ct)
    {
        foreach (var playlist in playlists)
        {
            var itemIds = new List<Guid>();
            foreach (var entry in playlist.Entries)
            {
                var match = FindByProviderId(entry, BaseItemKind.Episode).FirstOrDefault();
                if (match is not null) itemIds.Add(match.Id);
            }

            var existing = FindByProviderId(playlist.Id, BaseItemKind.BoxSet).FirstOrDefault() as BoxSet;
            if (existing is null)
            {
                if (itemIds.Count == 0) continue;   // nothing to seed a collection with yet
                var created = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = playlist.Name,
                    ItemIdList = itemIds.Select(id => id.ToString("N")).ToList(),
                }).ConfigureAwait(false);
                created.SetProviderId(Plugin.ProviderKey, playlist.Id);
                await _libraryManager.UpdateItemAsync(
                    created, created.GetParent(), ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                continue;
            }

            await ReconcileCollectionAsync(existing, itemIds, ct).ConfigureAwait(false);
        }
    }

    private async Task ReconcileCollectionAsync(BoxSet collection, IReadOnlyList<Guid> desired, CancellationToken ct)
    {
        var current = collection.GetRecursiveChildren()
            .Where(i => i is not BoxSet)
            .Select(i => i.Id)
            .ToHashSet();
        var target = desired.ToHashSet();

        var toAdd = target.Where(id => !current.Contains(id)).ToList();
        var toRemove = current.Where(id => !target.Contains(id)).ToList();

        if (toAdd.Count > 0)
            await _collectionManager.AddToCollectionAsync(collection.Id, toAdd).ConfigureAwait(false);
        if (toRemove.Count > 0)
            await _collectionManager.RemoveFromCollectionAsync(collection.Id, toRemove).ConfigureAwait(false);
    }

    private IReadOnlyList<BaseItem> FindByProviderId(string tubeletId, BaseItemKind kind) =>
        _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string> { [Plugin.ProviderKey] = tubeletId },
        });
}
