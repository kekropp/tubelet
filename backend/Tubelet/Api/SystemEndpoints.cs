using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Tubelet.Data;

namespace Tubelet.Api;

public static partial class SystemEndpoints
{
    [GeneratedRegex("^[a-z0-9_-]{1,64}$")]
    private static partial Regex SectionName();

    public static void MapSystemApi(this WebApplication app)
    {
        app.MapGet("/api/v1/system", async (Database db, AppPaths paths, Tubelet.Pipeline.YtDlpLocator ytdlp) =>
        {
            using var conn = db.Open();
            var stats = Queries.QueueStats(conn);
            var counts = conn.QuerySingle<(long Videos, long Channels)>(
                "SELECT (SELECT count(*) FROM videos), (SELECT count(*) FROM channels)");
            var cooldownRaw = Database.GetSetting(conn, "cooldown_until");

            return Results.Ok(new SystemDoc(
                Version: Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
                YtdlpVersion: await ytdlp.VersionAsync(),
                Media: DiskInfo(paths.MediaDir),
                Cache: DiskInfo(paths.CacheDir),
                Queue: new QueueStatsDoc(stats.Queued, stats.Running, stats.Failed, stats.Done),
                CooldownUntil: long.TryParse(cooldownRaw, out var cd) && cd > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? cd : null,
                VideoCount: counts.Videos,
                ChannelCount: counts.Channels));
        });

        app.MapGet("/api/v1/settings/{section}", (string section, Database db) =>
        {
            if (!SectionName().IsMatch(section)) return Results.NotFound();
            using var conn = db.Open();
            var raw = Database.GetSetting(conn, "section:" + section);
            return Results.Content(raw ?? "{}", "application/json");
        });

        app.MapPut("/api/v1/settings/{section}", (string section, JsonElement body, Database db) =>
        {
            if (!SectionName().IsMatch(section)) return Results.NotFound();
            using var conn = db.Open();
            Database.SetSetting(conn, "section:" + section, body.GetRawText());
            return Results.NoContent();
        });

        // Maintenance actions (Settings → Maintenance). Both are also driven by scheduler crons.
        app.MapPost("/api/v1/system/ytdlp/update",
            async (Tubelet.Pipeline.YtDlpLocator locator, IHttpClientFactory httpFactory, Database db) =>
        {
            try
            {
                var version = await locator.DownloadLatestAsync(httpFactory.CreateClient());
                using var conn = db.Open();
                Database.SetSetting(conn, "extract_fail_count", "0");
                return Results.Ok(new YtdlpUpdateResult(true, version, null));
            }
            catch (Exception e)
            {
                return Results.Ok(new YtdlpUpdateResult(false, null, e.Message));
            }
        });

        app.MapPost("/api/v1/system/backup", (Tubelet.Scheduling.Janitor janitor) =>
        {
            var file = janitor.BackupDatabase();
            if (file is null) return Results.Ok(new BackupResult(false, null, null, "backup failed — see server logs"));
            var bytes = new FileInfo(file).Length;
            return Results.Ok(new BackupResult(true, Path.GetFileName(file), bytes, null));
        });
    }

    private static DiskDoc? DiskInfo(string dir)
    {
        try
        {
            var d = new DriveInfo(dir);
            return new DiskDoc(d.AvailableFreeSpace, d.TotalSize);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
