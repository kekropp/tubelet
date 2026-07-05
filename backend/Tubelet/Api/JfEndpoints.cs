using Dapper;
using Tubelet.Contracts;
using Tubelet.Data;
using Tubelet.Domain;
using Tubelet.Sponsorblock;

namespace Tubelet.Api;

/// <summary>
/// /api/jf/v1 — the Jellyfin plugin API. Two shapes only: batch (ids in, docs out)
/// and delta (cursor in, changes out). See DESIGN.md §1.3.
/// </summary>
public static class JfEndpoints
{
    private const int MaxBatch = 500;

    public static void MapJfApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/jf/v1");

        g.MapGet("/videos", (string ids, Database db) =>
        {
            var idList = SplitIds(ids);
            using var conn = db.Open();
            var sbMapping = SbMapping.Load(conn);
            var rows = conn.Query<VideoRow>(
                $"SELECT {Queries.VideoColumns} FROM videos WHERE youtube_id IN @idList",
                new { idList });
            return Results.Ok(rows.Select(r => Mapping.ToDoc(r, sbMapping)).ToArray());
        });

        g.MapGet("/channels", (string ids, Database db) =>
        {
            var idList = SplitIds(ids);
            using var conn = db.Open();
            var rows = conn.Query<ChannelRow>(
                "SELECT * FROM channels WHERE channel_id IN @idList", new { idList });
            return Results.Ok(rows.Select(Mapping.ToDoc).ToArray());
        });

        g.MapGet("/changes", (Database db, string? since) =>
        {
            long cursor = 0;
            if (since is not null && !long.TryParse(since, out cursor))
                return Results.BadRequest();

            using var conn = db.Open();
            var current = Database.CurrentSeq(conn);
            if (cursor >= current) return Results.NoContent();

            var changedVideos = conn.Query<string>(
                "SELECT youtube_id FROM videos WHERE changed_at > @cursor ORDER BY changed_at",
                new { cursor }).ToArray();

            var playlists = conn.Query<PlaylistRow>(
                "SELECT * FROM playlists WHERE changed_at > @cursor AND active = 1 ORDER BY changed_at",
                new { cursor }).ToList();
            var playlistDocs = playlists.Select(p => Mapping.ToDoc(p, PlaylistEntries(conn, p.PlaylistId))).ToArray();

            if (changedVideos.Length == 0 && playlistDocs.Length == 0) return Results.NoContent();

            return Results.Ok(new ChangesDoc(
                Videos: changedVideos,
                Playlists: playlistDocs,
                NextCursor: current.ToString()));
        });
    }

    internal static string[] PlaylistEntries(Microsoft.Data.Sqlite.SqliteConnection conn, string playlistId) =>
        conn.Query<string>(
            "SELECT youtube_id FROM playlist_entries WHERE playlist_id = @playlistId ORDER BY idx",
            new { playlistId }).ToArray();

    private static string[] SplitIds(string ids) =>
        ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Distinct()
           .Take(MaxBatch)
           .ToArray();
}
