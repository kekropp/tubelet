using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Tubelet.Data;
using Tubelet.Domain;
using Tubelet.Pipeline;
using Tubelet.Scheduling;

namespace Tubelet.Api;

/// <summary>
/// /api/v1/subscriptions — CRUD over the things the <see cref="Scheduler"/> polls. Creating a
/// subscription only registers it for future cron ticks; "scan now" and "backlog" trigger work
/// immediately. Backlog reuses the intake channel/playlist expander (whole listing, priority 5).
/// </summary>
public static class SubscriptionEndpoints
{
    public static void MapSubscriptionApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/subscriptions");

        g.MapGet("/", (Database db) =>
        {
            using var conn = db.Open();
            var rows = conn.Query<SubscriptionRow>("SELECT * FROM subscriptions ORDER BY id");
            return Results.Ok(rows.Select(Mapping.ToDoc).ToArray());
        });

        g.MapGet("/{id:long}", (long id, Database db) =>
        {
            using var conn = db.Open();
            var row = conn.QuerySingleOrDefault<SubscriptionRow>(
                "SELECT * FROM subscriptions WHERE id = @id", new { id });
            return row is null ? Results.NotFound() : Results.Ok(Mapping.ToDoc(row));
        });

        g.MapPost("/", (SubscriptionRequest req, Database db) =>
        {
            var kind = (req.Kind ?? "").Trim().ToLowerInvariant();
            if (kind != "channel" && kind != "playlist")
                return Results.BadRequest(new { error = "kind must be 'channel' or 'playlist'" });
            var target = (req.TargetId ?? "").Trim();
            if (target.Length == 0)
                return Results.BadRequest(new { error = "target_id is required" });
            var cron = string.IsNullOrWhiteSpace(req.Cron) ? CronSchedule.Default : req.Cron.Trim();
            if (!CronSchedule.IsValid(cron))
                return Results.BadRequest(new { error = "cron is not a valid cron expression" });
            if (!FormatPresets.IsValidProfile(req.QualityProf))
                return Results.BadRequest(new { error = "quality_prof must be 'default', a preset (directplay|best|720p), or 'custom:<-f string>'" });

            using var conn = db.Open();
            var exists = conn.ExecuteScalar<long>(
                "SELECT count(*) FROM subscriptions WHERE target_id = @target", new { target }) > 0;
            if (exists) return Results.Conflict(new { error = "already subscribed to this target" });

            var next = CronSchedule.Next(cron, DateTimeOffset.UtcNow);
            var row = conn.QuerySingle<SubscriptionRow>("""
                INSERT INTO subscriptions (kind, target_id, cron, quality_prof, filter_json, enabled, next_check)
                VALUES (@kind, @target, @cron, @quality, @filter, @enabled, @next)
                RETURNING *
                """, new
            {
                kind, target, cron,
                quality = string.IsNullOrWhiteSpace(req.QualityProf) ? "default" : req.QualityProf,
                filter = req.FilterJson,
                enabled = req.Enabled ?? true,
                next,
            });
            return Results.Created($"/api/v1/subscriptions/{row.Id}", Mapping.ToDoc(row));
        });

        g.MapPatch("/{id:long}", (long id, SubscriptionRequest req, Database db) =>
        {
            using var conn = db.Open();
            var row = conn.QuerySingleOrDefault<SubscriptionRow>(
                "SELECT * FROM subscriptions WHERE id = @id", new { id });
            if (row is null) return Results.NotFound();

            // target_id / kind are immutable — a subscription is identified by what it watches.
            var cron = row.Cron;
            if (req.Cron is not null)
            {
                cron = req.Cron.Trim();
                if (!CronSchedule.IsValid(cron))
                    return Results.BadRequest(new { error = "cron is not a valid cron expression" });
            }
            if (!FormatPresets.IsValidProfile(req.QualityProf))
                return Results.BadRequest(new { error = "quality_prof must be 'default', a preset (directplay|best|720p), or 'custom:<-f string>'" });
            var enabled = req.Enabled ?? row.Enabled;
            // Recompute next_check when the cadence changed or the sub was just (re)enabled.
            var next = row.NextCheck;
            if (cron != row.Cron || (enabled && !row.Enabled) || (enabled && row.NextCheck is null))
                next = CronSchedule.Next(cron, DateTimeOffset.UtcNow);

            var updated = conn.QuerySingle<SubscriptionRow>("""
                UPDATE subscriptions SET
                    cron = @cron,
                    quality_prof = @quality,
                    filter_json = @filter,
                    enabled = @enabled,
                    next_check = @next
                WHERE id = @id
                RETURNING *
                """, new
            {
                id, cron, enabled, next,
                quality = req.QualityProf is null ? row.QualityProf
                    : (string.IsNullOrWhiteSpace(req.QualityProf) ? "default" : req.QualityProf),
                filter = req.FilterJson is null ? row.FilterJson
                    : (req.FilterJson.Length == 0 ? null : req.FilterJson),
            });
            return Results.Ok(Mapping.ToDoc(updated));
        });

        g.MapDelete("/{id:long}", (long id, Database db) =>
        {
            using var conn = db.Open();
            var removed = conn.Execute("DELETE FROM subscriptions WHERE id = @id", new { id });
            return removed > 0 ? Results.NoContent() : Results.NotFound();
        });

        // Check for new uploads right now (background); progress rides queue.stats.
        g.MapPost("/{id:long}/scan", (long id, Database db, SubscriptionScanner scanner) =>
        {
            using var conn = db.Open();
            var exists = conn.ExecuteScalar<long>(
                "SELECT count(*) FROM subscriptions WHERE id = @id", new { id }) > 0;
            if (!exists) return Results.NotFound();
            scanner.ScanInBackground(id);
            return Results.Accepted($"/api/v1/subscriptions/{id}");
        });

        // Fetch the backlog. With no body it's the whole listing; an optional scope narrows it to the
        // newest N or everything since a date (chosen after the add-time preview). Streams scan.progress.
        g.MapPost("/{id:long}/backlog", (long id,
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ScopeRequest? scope,
            Database db, IntakeExpander expander) =>
        {
            using var conn = db.Open();
            var row = conn.QuerySingleOrDefault<SubscriptionRow>(
                "SELECT * FROM subscriptions WHERE id = @id", new { id });
            if (row is null) return Results.NotFound();
            var kind = row.Kind == "playlist" ? UrlKind.Playlist : UrlKind.Channel;
            var s = scope is null ? IntakeScope.All : IntakeScope.From(scope.Mode, scope.N, scope.After);
            expander.ExpandInBackground(kind, row.TargetId, SubscriptionScanner.SubscriptionPriority, s,
                FormatPresets.Normalize(row.QualityProf));
            return Results.Accepted($"/api/v1/subscriptions/{id}");
        });
    }
}
