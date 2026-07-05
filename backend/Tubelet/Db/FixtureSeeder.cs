using Dapper;
using Tubelet.Domain;

namespace Tubelet.Data;

/// <summary>
/// Seeds a small deterministic catalog when TUBELET_FIXTURES=1 and the db is empty.
/// Used by the contract tests and for developing the frontend / Jellyfin plugin
/// before the download pipeline exists.
/// </summary>
public static class FixtureSeeder
{
    public const string Channel1 = "UCuAXFkgsw1L7xaCfnd5JJOw";
    public const string Channel2 = "UC2C_jShtL725hvbm1arSV9w";
    public const string VideoWithSegments = "dQw4w9WgXcQ";
    public const string CustomPlaylist = "TL-0FIXTURE0000000000000000";

    /// <summary>
    /// Optional large synthetic catalog for a library perf smoke test (TUBELET_FIXTURES_BULK=N).
    /// Inserts N indexed videos under the fixture channel so search/paging can be exercised at scale.
    /// </summary>
    public static void SeedBulk(Database db, int count)
    {
        if (count <= 0) return;
        using var conn = db.Open();
        var have = conn.ExecuteScalar<long>("SELECT count(*) FROM videos WHERE youtube_id LIKE 'bulk%'");
        if (have >= count) return;

        var topics = new[] { "SQLite", "ffmpeg", "yt-dlp", "Vue", "Jellyfin", "Docker", "Linux", "networking" };
        using var tx = conn.BeginTransaction();
        for (var i = (int)have; i < count; i++)
        {
            var id = $"bulk{i:D7}"; // 11 chars
            Queries.UpsertVideo(conn, tx, new VideoRow
            {
                YoutubeId = id, ChannelId = Channel2,
                Title = $"Bulk #{i}: {topics[i % topics.Length]} deep dive part {i % 20 + 1}",
                Description = $"Synthetic perf-fixture video {i} about {topics[i % topics.Length]}.",
                Published = $"2024-{(i % 12) + 1:D2}-{(i % 27) + 1:D2}T12:00:00Z",
                DurationS = 120 + i % 3600,
                Tags = $"[\"{topics[i % topics.Length].ToLowerInvariant()}\"]",
                MediaPath = $"{Channel2}/{id}.mp4",
                DownloadedAt = 1719700000 + i,
            }, "Tubelet Test Channel");
        }
        tx.Commit();
    }

    public static void Seed(Database db)
    {
        using var conn = db.Open();
        if (conn.ExecuteScalar<long>("SELECT count(*) FROM videos") > 0) return;

        using var tx = conn.BeginTransaction();

        conn.Execute("""
            INSERT INTO channels (channel_id, name, description, tags, thumb_path, banner_path, tvart_path, last_refresh)
            VALUES
              (@c1, 'Rick Astley', 'Official Rick Astley channel.', '["music"]',
               'channels/UCuAXFkgsw1L7xaCfnd5JJOw_thumb.jpg', 'channels/UCuAXFkgsw1L7xaCfnd5JJOw_banner.jpg',
               'channels/UCuAXFkgsw1L7xaCfnd5JJOw_tvart.jpg', 1719800000),
              (@c2, 'Tubelet Test Channel', 'Fixture channel for contract tests.', '[]',
               NULL, NULL, NULL, 1719800000)
            """, new { c1 = Channel1, c2 = Channel2 }, tx);

        var videos = new[]
        {
            new VideoRow
            {
                YoutubeId = VideoWithSegments, ChannelId = Channel1,
                Title = "Never Gonna Give You Up",
                Description = "The official video for “Never Gonna Give You Up”.\n\nLine two of the description.",
                Published = "2009-10-25T06:57:33Z", DurationS = 213,
                Tags = """["music","80s"]""",
                Chapters = """[{"title":"Intro","start_s":0.0},{"title":"Chorus","start_s":43.0}]""",
                MediaPath = $"{Channel1}/dQw4w9WgXcQ.mp4", MediaSize = 123_456_789,
                Width = 1920, Height = 1080, Vcodec = "avc1.640028", Acodec = "mp4a.40.2",
                ThumbPath = "videos/d/dQw4w9WgXcQ.jpg",
                Segments = """[{"category":"sponsor","start_s":12.34,"end_s":56.78},{"category":"outro","start_s":200.0,"end_s":213.0}]""",
                SbRefreshed = 1719800000, DownloadedAt = 1719700000,
            },
            new VideoRow
            {
                YoutubeId = "yPYZpwSpKmA", ChannelId = Channel1,
                Title = "Together Forever",
                Description = "Official video.",
                Published = "2010-02-05T08:00:12Z", DurationS = 205,
                Tags = """["music"]""",
                MediaPath = $"{Channel1}/yPYZpwSpKmA.mp4",
                Width = 1280, Height = 720, Vcodec = "avc1.4d401f", Acodec = "mp4a.40.2",
                ThumbPath = "videos/y/yPYZpwSpKmA.jpg",
                DownloadedAt = 1719700100,
            },
            new VideoRow
            {
                YoutubeId = "fixture0001", ChannelId = Channel2,
                Title = "Fixture: SQLite performance deep dive",
                Description = "A long talk about WAL mode, FTS5 and why one file is enough.",
                Published = "2024-03-01T12:00:00Z", DurationS = 3600,
                Tags = """["tech","databases"]""",
                Chapters = """[{"title":"WAL","start_s":0.0},{"title":"FTS5","start_s":1800.0}]""",
                MediaPath = $"{Channel2}/fixture0001.mp4",
                Width = 2560, Height = 1440, Vcodec = "vp9", Acodec = "opus",
                ThumbPath = "videos/f/fixture0001.jpg",
                DownloadedAt = 1719700200,
            },
            new VideoRow
            {
                YoutubeId = "fixture0002", ChannelId = Channel2,
                Title = "Fixture: ffmpeg remux in 90 seconds",
                Description = "Short one.",
                Published = "2024-05-20T18:30:00Z", DurationS = 90,
                Tags = "[]",
                MediaPath = $"{Channel2}/fixture0002.mp4",
                DownloadedAt = 1719700300,
            },
            new VideoRow
            {
                YoutubeId = "fixture0003", ChannelId = Channel2,
                Title = "Fixture: yt-dlp retry taxonomy",
                Description = "transient vs permanent vs throttled.",
                Published = "2024-06-11T09:15:00Z", DurationS = 640,
                Tags = """["tech"]""",
                MediaPath = $"{Channel2}/fixture0003.mp4",
                DownloadedAt = 1719700400,
            },
        };
        foreach (var v in videos)
            Queries.UpsertVideo(conn, tx, v, v.ChannelId == Channel1 ? "Rick Astley" : "Tubelet Test Channel");

        var plSeq = Database.NextSeq(conn, tx);
        conn.Execute("""
            INSERT INTO playlists (playlist_id, name, description, type, active, changed_at)
            VALUES (@id, 'Favorites', 'Custom fixture playlist', 'custom', 1, @plSeq)
            """, new { id = CustomPlaylist, plSeq }, tx);
        conn.Execute("""
            INSERT INTO playlist_entries (playlist_id, youtube_id, idx) VALUES
              (@id, @v1, 0), (@id, @v2, 1)
            """, new { id = CustomPlaylist, v1 = VideoWithSegments, v2 = "fixture0001" }, tx);

        // Far-future next_check so the scheduler never fires these during (offline) tests.
        conn.Execute("""
            INSERT INTO subscriptions (kind, target_id, cron, quality_prof, filter_json, enabled, last_check, next_check)
            VALUES
              ('channel', @c1, '0 */6 * * *', 'default', NULL, 1, 1719800000, 4102444800),
              ('channel', @c2, '0 8 * * *', 'default', '{"min_duration_s":120,"title_regex":"deep dive"}', 0, NULL, 4102444800)
            """, new { c1 = Channel1, c2 = Channel2 }, tx);

        conn.Execute("""
            INSERT INTO jobs (youtube_id, channel_id, title, state, priority, attempts, progress, added_at, started_at, finished_at, last_error, error_kind)
            VALUES
              ('queuedfix01', @c2, 'Fixture queued video', 'queued', 5, 0, 0, 1719800100, NULL, NULL, NULL, NULL),
              ('downlofix01', @c2, 'Fixture downloading video', 'downloading', 1, 1, 0.42, 1719800200, 1719800300, NULL, NULL, NULL),
              ('failedfix01', @c2, 'Fixture failed video', 'failed', 5, 3, 0, 1719800400, 1719800500, 1719800600,
               'ERROR: [youtube] failedfix01: Private video. Sign in if you''ve been granted access to this video', 'permanent')
            """, new { c2 = Channel2 }, tx);

        tx.Commit();
    }
}
