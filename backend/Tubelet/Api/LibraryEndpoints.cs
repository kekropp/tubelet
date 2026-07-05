using System.Text;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Tubelet.Data;
using Tubelet.Domain;
using Tubelet.Sponsorblock;

namespace Tubelet.Api;

public static class LibraryEndpoints
{
    private const int DefaultPageSize = 60;
    private const int MaxPageSize = 200;

    private static readonly Dictionary<string, string> SortColumns = new()
    {
        ["published"] = "v.published",
        ["added"] = "v.downloaded_at",
        ["duration"] = "v.duration_s",
        ["title"] = "v.title COLLATE NOCASE",
    };

    public static void MapLibraryApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1");

        g.MapGet("/videos", (Database db,
            string? query, string? channel, string? sort, int page = 1,
            [FromQuery(Name = "page_size")] int pageSize = DefaultPageSize) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
            var orderBy = ParseSort(sort);

            using var conn = db.Open();
            var sbMapping = SbMapping.Load(conn);

            var where = new StringBuilder("WHERE 1=1");
            var ftsQuery = BuildFtsQuery(query);
            string from = "videos v";
            if (ftsQuery is not null)
            {
                from = "videos_fts JOIN videos v ON v.rowid = videos_fts.rowid";
                where.Append(" AND videos_fts MATCH @ftsQuery");
            }
            if (!string.IsNullOrEmpty(channel)) where.Append(" AND v.channel_id = @channel");

            var args = new { ftsQuery, channel, limit = pageSize, offset = (page - 1) * pageSize };
            var total = conn.ExecuteScalar<long>($"SELECT count(*) FROM {from} {where}", args);
            var rows = conn.Query<VideoRow>(
                $"SELECT v.rowid AS rid, v.* FROM {from} {where} ORDER BY {orderBy} LIMIT @limit OFFSET @offset",
                args);

            return Results.Ok(new PagedVideos(
                rows.Select(r => Mapping.ToDoc(r, sbMapping)).ToArray(), total, page, pageSize));
        });

        g.MapGet("/videos/{id}", (string id, Database db) =>
        {
            using var conn = db.Open();
            var row = conn.QuerySingleOrDefault<VideoRow>(
                $"SELECT {Queries.VideoColumns} FROM videos WHERE youtube_id = @id", new { id });
            return row is null
                ? Results.NotFound()
                : Results.Ok(Mapping.ToDoc(row, SbMapping.Load(conn)));
        });

        g.MapDelete("/videos/{id}", (string id, HttpContext ctx, Database db, AppPaths paths) =>
        {
            var flag = ctx.Request.Query["also_ignore"].ToString();
            var alsoIgnore = flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase);
            using var conn = db.Open();
            var row = conn.QuerySingleOrDefault<VideoRow>(
                $"SELECT {Queries.VideoColumns} FROM videos WHERE youtube_id = @id", new { id });
            if (row is null) return Results.NotFound();

            using (var tx = conn.BeginTransaction())
            {
                Queries.DeleteVideo(conn, tx, id);
                if (alsoIgnore)
                    conn.Execute("INSERT OR IGNORE INTO ignored (youtube_id) VALUES (@id)", new { id }, tx);
                tx.Commit();
            }

            DeleteFileIfInside(paths.MediaDir, row.MediaPath);
            if (row.ThumbPath is not null) DeleteFileIfInside(paths.CacheDir, row.ThumbPath);
            return Results.NoContent();
        });

        g.MapGet("/channels", (Database db) =>
        {
            using var conn = db.Open();
            var rows = conn.Query<(string Id, string Name, string? Thumb, long Count)>("""
                SELECT c.channel_id, c.name, c.thumb_path, count(v.youtube_id)
                FROM channels c LEFT JOIN videos v ON v.channel_id = c.channel_id
                GROUP BY c.channel_id ORDER BY c.name COLLATE NOCASE
                """);
            return Results.Ok(rows.Select(r => new ChannelSummary(
                r.Id, r.Name, r.Thumb is null ? null : "/cache/" + r.Thumb, r.Count)).ToArray());
        });
    }

    private static string ParseSort(string? sort)
    {
        var desc = true;
        var key = "published";
        if (!string.IsNullOrEmpty(sort))
        {
            desc = sort.StartsWith('-');
            key = sort.TrimStart('-', '+');
            if (!SortColumns.ContainsKey(key)) { key = "published"; desc = true; }
        }
        return $"{SortColumns[key]} {(desc ? "DESC" : "ASC")}";
    }

    /// <summary>User text → FTS5 prefix query: each token quoted and starred, implicit AND.</summary>
    internal static string? BuildFtsQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var tokens = query
            .Split([' ', '\t', '"', '\'', '(', ')', '*', ':', '^', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Take(16)
            .Select(t => $"\"{t}\"*");
        var q = string.Join(" ", tokens);
        return q.Length == 0 ? null : q;
    }

    private static void DeleteFileIfInside(string root, string relative)
    {
        var full = AppPaths.SafeResolve(root, relative);
        if (full is not null && File.Exists(full)) File.Delete(full);
    }
}
