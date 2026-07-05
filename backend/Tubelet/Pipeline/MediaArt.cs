namespace Tubelet.Pipeline;

/// <summary>
/// Downloads video thumbnails and channel art, normalizing everything to jpg via ffmpeg.
/// Returns cache-root-relative paths (stored in thumb_path/banner_path/tvart_path and served
/// under /cache). All methods are best-effort: art failures never fail a download.
/// </summary>
public sealed class MediaArt(AppPaths paths, Ffmpeg ffmpeg, IHttpClientFactory httpFactory, ILogger<MediaArt> log)
{
    /// <summary>Video thumb → cache/videos/&lt;first-char&gt;/&lt;id&gt;.jpg. Returns the cache-relative path or null.</summary>
    public async Task<string?> SaveVideoThumbAsync(string youtubeId, string? url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var sub = youtubeId[..1];
        var dir = Path.Combine(paths.VideoThumbDir, sub);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, youtubeId + ".jpg");
        return await FetchToJpegAsync(url, dest, maxWidth: 1280, ct) ? $"videos/{sub}/{youtubeId}.jpg" : null;
    }

    /// <summary>Channel avatar/banner → square _thumb, _banner, 16:9 _tvart. Returns cache-relative paths.</summary>
    public async Task<(string? Thumb, string? Banner, string? Tvart)> SaveChannelArtAsync(
        string channelId, string? avatarUrl, string? bannerUrl, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.ChannelArtDir);
        var safe = Sanitize(channelId);
        string? thumb = null, banner = null, tvart = null;

        var tmp = await FetchTempAsync(avatarUrl, ct);
        if (tmp is not null)
        {
            try
            {
                var t = Path.Combine(paths.ChannelArtDir, safe + "_thumb.jpg");
                await ffmpeg.CropJpegAsync(tmp, t, 400, 400, ct);
                thumb = $"channels/{safe}_thumb.jpg";
            }
            catch (Exception e) { log.LogWarning(e, "channel thumb failed for {Channel}", channelId); }
            finally { TryDelete(tmp); }
        }

        var btmp = await FetchTempAsync(bannerUrl, ct);
        if (btmp is not null)
        {
            try
            {
                var b = Path.Combine(paths.ChannelArtDir, safe + "_banner.jpg");
                await ffmpeg.ToJpegAsync(btmp, b, maxWidth: 2120, ct);
                banner = $"channels/{safe}_banner.jpg";

                var tv = Path.Combine(paths.ChannelArtDir, safe + "_tvart.jpg");
                await ffmpeg.CropJpegAsync(btmp, tv, 1920, 1080, ct);
                tvart = $"channels/{safe}_tvart.jpg";
            }
            catch (Exception e) { log.LogWarning(e, "channel banner failed for {Channel}", channelId); }
            finally { TryDelete(btmp); }
        }

        return (thumb, banner, tvart);
    }

    private async Task<bool> FetchToJpegAsync(string url, string dest, int? maxWidth, CancellationToken ct)
    {
        var tmp = await FetchTempAsync(url, ct);
        if (tmp is null) return false;
        try { await ffmpeg.ToJpegAsync(tmp, dest, maxWidth, ct); return true; }
        catch (Exception e) { log.LogWarning(e, "thumb normalize failed for {Url}", url); return false; }
        finally { TryDelete(tmp); }
    }

    private async Task<string?> FetchTempAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var tmp = Path.Combine(paths.IncompleteDir, "art-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            using var http = httpFactory.CreateClient("art");
            await using var src = await http.GetStreamAsync(url, ct);
            await using var f = File.Create(tmp);
            await src.CopyToAsync(f, ct);
            return tmp;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "art download failed for {Url}", url);
            TryDelete(tmp);
            return null;
        }
    }

    private static string Sanitize(string id) =>
        string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
