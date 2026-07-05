using Dapper;
using Microsoft.Data.Sqlite;
using Tubelet.Pipeline;

namespace Tubelet.Data;

/// <summary>
/// Playlist mutations. Every write bumps <c>changed_at</c> via <see cref="Database.NextSeq"/> so the
/// Jellyfin plugin's <c>/changes</c> cursor picks up collection edits with no extra plumbing.
/// </summary>
public static class Playlists
{
    /// <summary>Upsert a "regular" (YouTube-sourced) playlist and replace its ordered entries.</summary>
    public static void UpsertRegular(SqliteConnection conn, string playlistId, FlatListing listing)
    {
        using var tx = conn.BeginTransaction();
        var seq = Database.NextSeq(conn, tx);
        conn.Execute("""
            INSERT INTO playlists (playlist_id, name, channel_id, channel_name, description, type, active, changed_at)
            VALUES (@playlistId, @name, @channelId, @channelName, @description, 'regular', 1, @seq)
            ON CONFLICT(playlist_id) DO UPDATE SET
                name = excluded.name, channel_id = excluded.channel_id,
                channel_name = excluded.channel_name, description = excluded.description,
                active = 1, changed_at = excluded.changed_at
            """, new
        {
            playlistId,
            name = listing.Title ?? playlistId,
            channelId = listing.ChannelId,
            channelName = listing.ChannelName,
            description = listing.Description ?? "",
            seq,
        }, tx);
        ReplaceEntries(conn, tx, playlistId, listing.Entries.Select(e => e.Id));
        tx.Commit();
    }

    public static void ReplaceEntries(SqliteConnection conn, System.Data.IDbTransaction tx,
        string playlistId, IEnumerable<string> youtubeIds)
    {
        conn.Execute("DELETE FROM playlist_entries WHERE playlist_id = @playlistId", new { playlistId }, tx);
        var idx = 0;
        foreach (var id in youtubeIds)
            conn.Execute(
                "INSERT OR IGNORE INTO playlist_entries (playlist_id, youtube_id, idx) VALUES (@playlistId, @id, @idx)",
                new { playlistId, id, idx = idx++ }, tx);
    }
}
