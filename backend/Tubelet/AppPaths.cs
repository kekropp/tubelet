namespace Tubelet;

/// <summary>Resolved data directories (TUBELET_MEDIA / TUBELET_CACHE).</summary>
public sealed class AppPaths
{
    public string MediaDir { get; }
    public string CacheDir { get; }
    public string IncompleteDir => Path.Combine(CacheDir, "incomplete");
    public string VideoThumbDir => Path.Combine(CacheDir, "videos");
    public string ChannelArtDir => Path.Combine(CacheDir, "channels");
    public string CookiesDir => Path.Combine(CacheDir, "cookies");
    public string BackupDir => Path.Combine(CacheDir, "backup");
    public string BinDir => Path.Combine(CacheDir, "bin");
    public string LogDir => Path.Combine(CacheDir, "logs");

    public AppPaths(string mediaDir, string cacheDir)
    {
        MediaDir = Path.GetFullPath(mediaDir);
        CacheDir = Path.GetFullPath(cacheDir);
        foreach (var d in new[] { MediaDir, CacheDir, IncompleteDir, VideoThumbDir, ChannelArtDir, CookiesDir, BackupDir, BinDir, LogDir })
            Directory.CreateDirectory(d);
    }

    /// <summary>Resolve a stored relative path under a root, refusing traversal outside it.</summary>
    public static string? SafeResolve(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }
}
