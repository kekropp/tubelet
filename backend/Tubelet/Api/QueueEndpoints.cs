using Dapper;
using Tubelet.Data;
using Tubelet.Domain;
using Tubelet.Pipeline;
using Tubelet.Realtime;

namespace Tubelet.Api;

public static class QueueEndpoints
{
    public static void MapQueueApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/queue");

        g.MapGet("/", (Database db) =>
        {
            using var conn = db.Open();
            var active = conn.Query<JobRow>("""
                SELECT * FROM jobs
                WHERE state IN ('queued', 'fetching_meta', 'downloading', 'converting', 'indexing', 'paused')
                ORDER BY CASE WHEN state = 'queued' OR state = 'paused' THEN 1 ELSE 0 END, priority, added_at
                """).Select(Mapping.ToDoc).ToArray();
            var recent = conn.Query<JobRow>(
                "SELECT * FROM jobs WHERE state = 'done' ORDER BY finished_at DESC LIMIT 25")
                .Select(Mapping.ToDoc).ToArray();
            var failed = conn.Query<JobRow>(
                "SELECT * FROM jobs WHERE state = 'failed' ORDER BY finished_at DESC LIMIT 50")
                .Select(Mapping.ToDoc).ToArray();
            return Results.Ok(new QueueDoc(active, recent, failed));
        });

        g.MapPost("/{id:long}/retry", (long id, Database db, Broadcaster bc) =>
            Mutate(db, bc, id, """
                UPDATE jobs SET state = 'queued', attempts = 0, next_retry = NULL,
                                last_error = NULL, error_kind = NULL, progress = 0,
                                started_at = NULL, finished_at = NULL
                WHERE id = @id AND state IN ('failed', 'paused')
                RETURNING *
                """));

        g.MapPost("/{id:long}/pause", (long id, Database db, Broadcaster bc) =>
            Mutate(db, bc, id,
                "UPDATE jobs SET state = 'paused' WHERE id = @id AND state = 'queued' RETURNING *"));

        g.MapPost("/{id:long}/cancel", (long id, Database db, JobControl control) =>
        {
            // Kill the running subprocess first (if any), then drop the row. The worker's
            // cancellation path sees the row already gone and does nothing.
            control.Cancel(id);
            using var conn = db.Open();
            var removed = conn.Execute(
                "DELETE FROM jobs WHERE id = @id AND state <> 'done'", new { id });
            return removed > 0 ? Results.NoContent() : Results.NotFound();
        });

        g.MapPost("/{id:long}/priority", (long id, PriorityRequest req, Database db, Broadcaster bc) =>
            Mutate(db, bc, id,
                "UPDATE jobs SET priority = @priority WHERE id = @id RETURNING *",
                new { id, priority = Math.Clamp(req.Priority, 1, 9) }));
    }

    private static IResult Mutate(Database db, Broadcaster bc, long id, string sql, object? args = null)
    {
        using var conn = db.Open();
        var row = conn.QuerySingleOrDefault<JobRow>(sql, args ?? new { id });
        if (row is null) return Results.NotFound();
        var doc = Mapping.ToDoc(row);
        _ = bc.JobState(doc);
        return Results.Ok(doc);
    }
}
