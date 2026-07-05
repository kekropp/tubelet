using Dapper;
using Tubelet.Data;
using Tubelet.Domain;
using Tubelet.Pipeline;
using Tubelet.Realtime;

namespace Tubelet.Api;

public static class QueueEndpoints
{
    // States that mean "in flight" (a worker owns it) vs. "waiting" (claimable).
    private const string RunningStates = "'fetching_meta', 'downloading', 'converting', 'indexing'";
    private const string WaitingStates = "'queued', 'paused'";

    public static void MapQueueApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/queue");

        g.MapGet("/", (Database db) =>
        {
            using var conn = db.Open();
            // Home is a dashboard, not the full queue — bound the active list so a huge backlog stays
            // light. The paginated /jobs endpoint (Queue view) shows everything.
            var active = conn.Query<JobRow>($"""
                SELECT * FROM jobs
                WHERE state IN ({RunningStates}, {WaitingStates})
                ORDER BY CASE WHEN state IN ({WaitingStates}) THEN 1 ELSE 0 END, priority, added_at
                LIMIT 60
                """).Select(Mapping.ToDoc).ToArray();
            var recent = conn.Query<JobRow>(
                "SELECT * FROM jobs WHERE state = 'done' ORDER BY finished_at DESC LIMIT 25")
                .Select(Mapping.ToDoc).ToArray();
            var failed = conn.Query<JobRow>(
                "SELECT * FROM jobs WHERE state = 'failed' ORDER BY finished_at DESC LIMIT 50")
                .Select(Mapping.ToDoc).ToArray();
            var s = Queries.QueueStats(conn);
            return Results.Ok(new QueueDoc(active, recent, failed,
                new QueueStatsDoc(s.Queued, s.Running, s.Failed, s.Done), QueuePaused(conn)));
        });

        // Full, paginated queue for the management view. filter: active | queued | running | failed | done | all.
        g.MapGet("/jobs", (string? filter, int? page, int? page_size, Database db) =>
        {
            var (where, order) = FilterClause(filter);
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(page_size ?? 50, 1, 200);
            using var conn = db.Open();
            var total = conn.ExecuteScalar<long>($"SELECT count(*) FROM jobs WHERE {where}");
            var items = conn.Query<JobRow>(
                $"SELECT * FROM jobs WHERE {where} ORDER BY {order} LIMIT @size OFFSET @off",
                new { size, off = (p - 1) * size }).Select(Mapping.ToDoc).ToArray();
            return Results.Ok(new PagedJobs(items, total, p, size));
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

        // Multi-item / global action over an explicit id list or a scope.
        g.MapPost("/bulk", async (QueueBulkRequest req, Database db, JobControl control,
            Broadcaster bc, PipelineSignal signal) =>
        {
            // Target predicate: an explicit selection (id IN @ids) or a whitelisted scope clause.
            string pred;
            var args = new DynamicParameters();
            if (req.Ids is { Length: > 0 } ids)
            {
                pred = "id IN @ids";
                args.Add("ids", ids);
            }
            else if (ScopeWhere(req.Scope) is { } scopeWhere) pred = scopeWhere;
            else return Results.BadRequest(new { error = "provide ids or a valid scope" });

            using var conn = db.Open();
            int affected;
            switch ((req.Action ?? "").Trim().ToLowerInvariant())
            {
                case "cancel":
                    // Kill any in-flight subprocesses among the targets, then delete every non-done row.
                    foreach (var rid in conn.Query<long>(
                                 $"SELECT id FROM jobs WHERE ({pred}) AND state IN ({RunningStates})", args))
                        control.Cancel(rid);
                    affected = conn.Execute($"DELETE FROM jobs WHERE ({pred}) AND state <> 'done'", args);
                    break;
                case "pause":
                    affected = conn.Execute($"UPDATE jobs SET state = 'paused' WHERE ({pred}) AND state = 'queued'", args);
                    break;
                case "resume":
                    affected = conn.Execute(
                        $"UPDATE jobs SET state = 'queued', next_retry = NULL WHERE ({pred}) AND state = 'paused'", args);
                    break;
                case "retry":
                    affected = conn.Execute($"""
                        UPDATE jobs SET state = 'queued', attempts = 0, next_retry = NULL, last_error = NULL,
                            error_kind = NULL, progress = 0, started_at = NULL, finished_at = NULL
                        WHERE ({pred}) AND state IN ('failed', 'paused')
                        """, args);
                    break;
                case "priority":
                    args.Add("priority", Math.Clamp(req.Priority ?? 1, 1, 9));
                    affected = conn.Execute(
                        $"UPDATE jobs SET priority = @priority WHERE ({pred}) AND state NOT IN ('done', 'failed')", args);
                    break;
                default:
                    return Results.BadRequest(new { error = "unknown action" });
            }

            signal.Signal(); // resume/retry create claimable work; wake the coordinator
            var s = Queries.QueueStats(conn);
            await bc.QueueStats(new QueueStatsDoc(s.Queued, s.Running, s.Failed, s.Done));
            await bc.QueueInvalidated();
            return Results.Ok(new QueueBulkResult(affected));
        });

        // Global coordinator pause — running jobs finish, nothing new is claimed until resume.
        g.MapPost("/pause", async (Database db, Broadcaster bc) =>
        {
            using (var conn = db.Open()) Database.SetSetting(conn, "queue_paused", "1");
            await bc.QueuePaused(true);
            return Results.Ok(new { paused = true });
        });

        g.MapPost("/resume", async (Database db, Broadcaster bc, PipelineSignal signal) =>
        {
            using (var conn = db.Open()) Database.SetSetting(conn, "queue_paused", "0");
            signal.Signal();
            await bc.QueuePaused(false);
            return Results.Ok(new { paused = false });
        });
    }

    private static bool QueuePaused(Microsoft.Data.Sqlite.SqliteConnection conn) =>
        Database.GetSetting(conn, "queue_paused") == "1";

    /// <summary>Whitelisted WHERE clause for a bulk scope; null when the scope is unrecognized.</summary>
    private static string? ScopeWhere(string? scope) => (scope ?? "").Trim().ToLowerInvariant() switch
    {
        "queued" => $"state IN ({WaitingStates})",
        "active" => "state NOT IN ('done', 'failed')",
        "failed" => "state = 'failed'",
        "all" => "1 = 1",
        _ => null,
    };

    /// <summary>(where, order) for the paginated listing. Defaults to the "active" bucket.</summary>
    private static (string Where, string Order) FilterClause(string? filter) =>
        (filter ?? "").Trim().ToLowerInvariant() switch
        {
            "queued" => ($"state IN ({WaitingStates})", "priority, added_at"),
            "running" => ($"state IN ({RunningStates})", "started_at"),
            "failed" => ("state = 'failed'", "finished_at DESC"),
            "done" => ("state = 'done'", "finished_at DESC"),
            "all" => ("1 = 1",
                $"CASE WHEN state IN ({RunningStates}) THEN 0 WHEN state IN ({WaitingStates}) THEN 1 ELSE 2 END, priority, added_at"),
            _ => ($"state IN ({RunningStates}, {WaitingStates})",
                $"CASE WHEN state IN ({WaitingStates}) THEN 1 ELSE 0 END, priority, added_at"),
        };

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
