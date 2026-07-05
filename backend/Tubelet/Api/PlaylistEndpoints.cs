using Dapper;
using Tubelet.Data;
using Tubelet.Domain;

namespace Tubelet.Api;

/// <summary>
/// /api/v1/playlists — custom playlists (id "TL-&lt;ulid&gt;") that become Jellyfin collections.
/// Every mutation bumps changed_at via <see cref="Database.NextSeq"/> so the plugin's /changes
/// cursor carries the edit. Regular (YouTube-sourced) playlists are read-only here — they are
/// refreshed by intake/scan; edits would be overwritten.
/// </summary>
public static class PlaylistEndpoints
{
    public static void MapPlaylistApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/playlists");

        g.MapGet("/", (Database db) =>
        {
            using var conn = db.Open();
            var rows = conn.Query<(string Id, string Name, string Description, string Type, bool Active, string? Thumb, long Count)>("""
                SELECT p.playlist_id, p.name, p.description, p.type, p.active, p.thumb_path,
                       (SELECT count(*) FROM playlist_entries e WHERE e.playlist_id = p.playlist_id)
                FROM playlists p ORDER BY p.name COLLATE NOCASE
                """);
            return Results.Ok(rows.Select(r => new PlaylistSummary(
                r.Id, r.Name, r.Description, r.Type, r.Active, (int)r.Count,
                r.Thumb is null ? null : "/cache/" + r.Thumb)).ToArray());
        });

        g.MapGet("/{id}", (string id, Database db) =>
        {
            using var conn = db.Open();
            var row = conn.QuerySingleOrDefault<PlaylistRow>(
                "SELECT * FROM playlists WHERE playlist_id = @id", new { id });
            return row is null
                ? Results.NotFound()
                : Results.Ok(Mapping.ToDoc(row, JfEndpoints.PlaylistEntries(conn, id)));
        });

        g.MapPost("/", (PlaylistRequest req, Database db) =>
        {
            var name = (req.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "name is required" });

            var id = Ulid.NewPlaylistId();
            using var conn = db.Open();
            using (var tx = conn.BeginTransaction())
            {
                var seq = Database.NextSeq(conn, tx);
                conn.Execute("""
                    INSERT INTO playlists (playlist_id, name, description, type, active, changed_at)
                    VALUES (@id, @name, @description, 'custom', 1, @seq)
                    """, new { id, name, description = req.Description ?? "", seq }, tx);
                if (req.Entries is { Length: > 0 })
                    Playlists.ReplaceEntries(conn, tx, id, req.Entries);
                tx.Commit();
            }
            var created = conn.QuerySingle<PlaylistRow>(
                "SELECT * FROM playlists WHERE playlist_id = @id", new { id });
            return Results.Created($"/api/v1/playlists/{id}", Mapping.ToDoc(created, JfEndpoints.PlaylistEntries(conn, id)));
        });

        g.MapPatch("/{id}", (string id, PlaylistRequest req, Database db) =>
        {
            using var conn = db.Open();
            var row = conn.QuerySingleOrDefault<PlaylistRow>(
                "SELECT * FROM playlists WHERE playlist_id = @id", new { id });
            if (row is null) return Results.NotFound();

            using (var tx = conn.BeginTransaction())
            {
                var seq = Database.NextSeq(conn, tx);
                conn.Execute("""
                    UPDATE playlists SET name = @name, description = @description, changed_at = @seq
                    WHERE playlist_id = @id
                    """, new
                {
                    id, seq,
                    name = string.IsNullOrWhiteSpace(req.Name) ? row.Name : req.Name.Trim(),
                    description = req.Description ?? row.Description,
                }, tx);
                if (req.Entries is not null)
                    Playlists.ReplaceEntries(conn, tx, id, req.Entries);
                tx.Commit();
            }
            var updated = conn.QuerySingle<PlaylistRow>(
                "SELECT * FROM playlists WHERE playlist_id = @id", new { id });
            return Results.Ok(Mapping.ToDoc(updated, JfEndpoints.PlaylistEntries(conn, id)));
        });

        g.MapDelete("/{id}", (string id, Database db) =>
        {
            using var conn = db.Open();
            // Bump the sequence so the deletion advances the cursor even though the row is gone;
            // the plugin reconciles its collection set on the next full sync.
            using var tx = conn.BeginTransaction();
            Database.NextSeq(conn, tx);
            var removed = conn.Execute("DELETE FROM playlists WHERE playlist_id = @id", new { id }, tx);
            tx.Commit();
            return removed > 0 ? Results.NoContent() : Results.NotFound();
        });
    }
}
