using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Tubelet.Domain;

namespace Tubelet.Data;

public static class Queries
{
    public const string VideoColumns = "rowid AS rid, *";

    // ---- videos + FTS ------------------------------------------------------

    /// <summary>Insert or replace a video row, stamp changed_at, and sync its FTS row.</summary>
    public static void UpsertVideo(SqliteConnection conn, IDbTransaction tx, VideoRow v, string channelName)
    {
        var seq = Database.NextSeq(conn, tx);
        conn.Execute("""
            INSERT INTO videos (youtube_id, channel_id, title, description, published, duration_s,
                                tags, chapters, media_path, media_size, width, height, vcodec, acodec,
                                thumb_path, segments, sb_refreshed,
                                downloaded_at, changed_at, info_json)
            VALUES (@YoutubeId, @ChannelId, @Title, @Description, @Published, @DurationS,
                    @Tags, @Chapters, @MediaPath, @MediaSize, @Width, @Height, @Vcodec, @Acodec,
                    @ThumbPath, @Segments, @SbRefreshed,
                    @DownloadedAt, @seq, @InfoJson)
            ON CONFLICT(youtube_id) DO UPDATE SET
                channel_id = excluded.channel_id, title = excluded.title,
                description = excluded.description, published = excluded.published,
                duration_s = excluded.duration_s, tags = excluded.tags,
                chapters = excluded.chapters, media_path = excluded.media_path,
                media_size = excluded.media_size, width = excluded.width, height = excluded.height,
                vcodec = excluded.vcodec, acodec = excluded.acodec,
                thumb_path = excluded.thumb_path, segments = excluded.segments,
                sb_refreshed = excluded.sb_refreshed,
                downloaded_at = excluded.downloaded_at, changed_at = excluded.changed_at,
                info_json = COALESCE(excluded.info_json, videos.info_json)
            """, new
        {
            v.YoutubeId, v.ChannelId, v.Title, v.Description, v.Published, v.DurationS,
            v.Tags, v.Chapters, v.MediaPath, v.MediaSize, v.Width, v.Height, v.Vcodec, v.Acodec,
            v.ThumbPath, v.Segments, v.SbRefreshed,
            v.DownloadedAt, seq, v.InfoJson,
        }, tx);

        var rid = conn.ExecuteScalar<long>(
            "SELECT rowid FROM videos WHERE youtube_id = @id", new { id = v.YoutubeId }, tx);
        conn.Execute("DELETE FROM videos_fts WHERE rowid = @rid", new { rid }, tx);
        conn.Execute("""
            INSERT INTO videos_fts (rowid, title, description, channel_name, tags)
            VALUES (@rid, @title, @description, @channelName, @tags)
            """,
            new { rid, title = v.Title, description = v.Description, channelName, tags = v.Tags }, tx);
    }

    public static void DeleteVideo(SqliteConnection conn, IDbTransaction tx, string youtubeId)
    {
        var rid = conn.ExecuteScalar<long?>(
            "SELECT rowid FROM videos WHERE youtube_id = @youtubeId", new { youtubeId }, tx);
        if (rid is null) return;
        conn.Execute("DELETE FROM videos_fts WHERE rowid = @rid", new { rid }, tx);
        conn.Execute("DELETE FROM playlist_entries WHERE youtube_id = @youtubeId", new { youtubeId }, tx);
        conn.Execute("DELETE FROM videos WHERE youtube_id = @youtubeId", new { youtubeId }, tx);
    }

    // ---- channels ----------------------------------------------------------

    /// <summary>
    /// Insert or update a channel row. Art paths use COALESCE and description keeps its old value
    /// unless the new one is non-empty, so a later refresh that lacks them (e.g. an upsert from a
    /// video infojson, which never carries channel description/art) can't wipe fetched data.
    /// Returns true if the channel row did not exist before (first sight → fetch art).
    /// </summary>
    public static bool UpsertChannel(SqliteConnection conn, IDbTransaction tx, ChannelRow c)
    {
        var existed = conn.ExecuteScalar<long>(
            "SELECT count(*) FROM channels WHERE channel_id = @ChannelId", new { c.ChannelId }, tx) > 0;
        conn.Execute("""
            INSERT INTO channels (channel_id, name, description, tags, thumb_path, banner_path, tvart_path, last_refresh)
            VALUES (@ChannelId, @Name, @Description, @Tags, @ThumbPath, @BannerPath, @TvartPath, @LastRefresh)
            ON CONFLICT(channel_id) DO UPDATE SET
                name = excluded.name, tags = excluded.tags,
                description = COALESCE(NULLIF(excluded.description, ''), channels.description),
                thumb_path  = COALESCE(excluded.thumb_path,  channels.thumb_path),
                banner_path = COALESCE(excluded.banner_path, channels.banner_path),
                tvart_path  = COALESCE(excluded.tvart_path,  channels.tvart_path),
                last_refresh = excluded.last_refresh
            """, new
        {
            c.ChannelId, c.Name, c.Description, c.Tags, c.ThumbPath, c.BannerPath, c.TvartPath, c.LastRefresh,
        }, tx);
        return !existed;
    }

    // ---- job queue ---------------------------------------------------------

    /// <summary>
    /// Enqueue a download. Returns true if a job was (re)queued, false if the id is
    /// already archived, ignored, or has a live job. A failed job is revived instead
    /// of duplicated.
    /// </summary>
    public static bool EnqueueJob(SqliteConnection conn, string youtubeId, int priority,
        string? channelId = null, string? title = null)
    {
        var blocked = conn.ExecuteScalar<long>("""
            SELECT (SELECT count(*) FROM videos WHERE youtube_id = @youtubeId)
                 + (SELECT count(*) FROM ignored WHERE youtube_id = @youtubeId)
                 + (SELECT count(*) FROM jobs WHERE youtube_id = @youtubeId AND state <> 'failed')
            """, new { youtubeId });
        if (blocked > 0) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return conn.Execute("""
            INSERT INTO jobs (youtube_id, channel_id, title, priority, added_at)
            VALUES (@youtubeId, @channelId, @title, @priority, @now)
            ON CONFLICT(youtube_id) DO UPDATE SET
                state = 'queued', priority = @priority, attempts = 0, next_retry = NULL,
                last_error = NULL, error_kind = NULL, progress = 0,
                started_at = NULL, finished_at = NULL
            WHERE jobs.state = 'failed'
            """, new { youtubeId, channelId, title, priority, now }) > 0;
    }

    /// <summary>
    /// Baseline a "subscribe, only new from now on" choice: mark the current backlog ids as ignored so
    /// intake/scanner skip them regardless of whether YouTube exposed upload dates. Ids already known
    /// (archived / queued / already ignored) are left as-is. Returns how many ids are now on the skip list.
    /// </summary>
    public static int IgnoreExisting(SqliteConnection conn, IEnumerable<string> youtubeIds)
    {
        var ids = youtubeIds.Where(id => id is { Length: 11 }).Distinct().ToArray();
        if (ids.Length == 0) return 0;
        using var tx = conn.BeginTransaction();
        var n = conn.Execute("INSERT OR IGNORE INTO ignored (youtube_id) VALUES (@id)",
            ids.Select(id => new { id }), tx);
        tx.Commit();
        return n;
    }

    /// <summary>
    /// Atomically claim the next ready job (single UPDATE…RETURNING — SQLite serializes
    /// writers, so two concurrent claimants can never get the same row).
    /// </summary>
    public static JobRow? ClaimNextJob(SqliteConnection conn, long nowUnix)
    {
        return conn.QuerySingleOrDefault<JobRow>("""
            UPDATE jobs
            SET state = 'fetching_meta', started_at = @nowUnix, attempts = attempts + 1
            WHERE id = (
                SELECT id FROM jobs
                WHERE state = 'queued' AND (next_retry IS NULL OR next_retry <= @nowUnix)
                ORDER BY priority, added_at
                LIMIT 1)
            RETURNING *
            """, new { nowUnix });
    }

    /// <summary>Startup recovery: jobs a crashed/killed worker left mid-flight go back to queued.</summary>
    public static int ResetStuckJobs(SqliteConnection conn) =>
        conn.Execute("""
            UPDATE jobs SET state = 'queued', started_at = NULL
            WHERE state IN ('fetching_meta', 'downloading', 'converting', 'indexing')
            """);

    public static (int Queued, int Running, int Failed, int Done) QueueStats(SqliteConnection conn)
    {
        var row = conn.QuerySingle<(long, long, long, long)>("""
            SELECT
              (SELECT count(*) FROM jobs WHERE state IN ('queued', 'paused')),
              (SELECT count(*) FROM jobs WHERE state IN ('fetching_meta', 'downloading', 'converting', 'indexing')),
              (SELECT count(*) FROM jobs WHERE state = 'failed'),
              (SELECT count(*) FROM jobs WHERE state = 'done')
            """);
        return ((int)row.Item1, (int)row.Item2, (int)row.Item3, (int)row.Item4);
    }
}
