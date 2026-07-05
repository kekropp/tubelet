using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.Tubelet.Providers;

/// <summary>
/// Tubelet identity derives from the filesystem layout <c>{media}/&lt;channel_id&gt;/&lt;video_id&gt;.mp4</c>:
/// a video's id is its filename stem, a channel's id is the parent directory name. Once matched we stamp
/// <c>ProviderIds["Tubelet"]</c> so later renames/moves can't break the mapping.
/// </summary>
public static class TubeletIds
{
    /// <summary>Resolve a video id from a persisted provider id, else the file stem.</summary>
    public static string? VideoId(ItemLookupInfo info) =>
        FromProvider(info.ProviderIds) ?? StemFromPath(info.Path);

    /// <summary>Resolve a channel id from a persisted provider id, else the folder name.</summary>
    public static string? ChannelId(ItemLookupInfo info) =>
        FromProvider(info.ProviderIds) ?? FolderNameFromPath(info.Path);

    public static string? VideoId(BaseItem item) =>
        FromProvider(item.ProviderIds) ?? StemFromPath(item.Path);

    public static string? ChannelId(BaseItem item) =>
        FromProvider(item.ProviderIds) ?? FolderNameFromPath(item.Path);

    private static string? FromProvider(IReadOnlyDictionary<string, string>? providerIds) =>
        providerIds is not null && providerIds.TryGetValue(Plugin.ProviderKey, out var id) && !string.IsNullOrEmpty(id)
            ? id
            : null;

    private static string? StemFromPath(string? path) =>
        string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);

    private static string? FolderNameFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        // A Series folder path ends in the channel dir; a stray file path yields its parent dir.
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
