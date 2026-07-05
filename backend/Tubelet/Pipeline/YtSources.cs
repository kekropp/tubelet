namespace Tubelet.Pipeline;

/// <summary>Builds the YouTube listing URLs and locates the cookie jar — shared by intake expansion and the scheduler.</summary>
public static class YtSources
{
    public static string Playlist(string id) => $"https://www.youtube.com/playlist?list={id}";

    /// <summary>Uploads tab for a concrete UC id, @handle, or c/… / user/… reference.</summary>
    public static string Channel(string id)
    {
        if (id.StartsWith("UC", StringComparison.Ordinal)) return $"https://www.youtube.com/channel/{id}/videos";
        if (id.StartsWith('@')) return $"https://www.youtube.com/{id}/videos";
        return $"https://www.youtube.com/{id.TrimStart('/')}/videos";
    }

    public static string For(UrlKind kind, string id) =>
        kind == UrlKind.Playlist ? Playlist(id) : Channel(id);

    /// <summary>Path to cookies.txt if present, else null (yt-dlp runs without cookies).</summary>
    public static string? CookiesFile(AppPaths paths)
    {
        var f = Path.Combine(paths.CookiesDir, "cookies.txt");
        return File.Exists(f) ? f : null;
    }
}
