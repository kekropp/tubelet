using Tubelet.Data;
using Tubelet.Pipeline;
using Tubelet.Realtime;

namespace Tubelet.Api;

public static class IntakeEndpoints
{
    public static void MapIntakeApi(this WebApplication app)
    {
        // One omnibox: paste anything. Videos enqueue immediately at priority 1 and wake the
        // coordinator. Channels/playlists are classified now and expanded to video jobs in the
        // background (yt-dlp --flat-playlist); progress streams over SignalR scan.progress.
        app.MapPost("/api/v1/intake", async (IntakeRequest req, Database db, Broadcaster bc,
            PipelineSignal signal, IntakeExpander expander) =>
        {
            var c = UrlClassifier.Classify(req.Url ?? "");
            switch (c.Kind)
            {
                case UrlKind.Unknown:
                    return Results.UnprocessableEntity(
                        new IntakeResult("unknown", null, "unrecognized", [], []));

                case UrlKind.Video:
                {
                    using var conn = db.Open();
                    var enqueued = Queries.EnqueueJob(conn, c.Id!, priority: 1);
                    var status = enqueued ? "enqueued" : StatusForSkip(conn, c.Id!);
                    if (enqueued)
                    {
                        signal.Signal();
                        var stats = Queries.QueueStats(conn);
                        await bc.QueueStats(new QueueStatsDoc(stats.Queued, stats.Running, stats.Failed, stats.Done));
                    }
                    return Results.Ok(new IntakeResult("video", c.Id, status,
                        enqueued ? [c.Id!] : [], enqueued ? [] : [c.Id!]));
                }

                case UrlKind.Playlist:
                    expander.ExpandInBackground(UrlKind.Playlist, c.Id!, priority: 1);
                    return Results.Ok(new IntakeResult("playlist", c.Id, "expanding", [], []));

                case UrlKind.Channel:
                default:
                    expander.ExpandInBackground(UrlKind.Channel, c.Id!, priority: 1);
                    return Results.Ok(new IntakeResult("channel", c.Id, "expanding", [], []));
            }
        });
    }

    private static string StatusForSkip(Microsoft.Data.Sqlite.SqliteConnection conn, string id)
    {
        if (Dapper.SqlMapper.ExecuteScalar<long>(conn,
                "SELECT count(*) FROM videos WHERE youtube_id = @id", new { id }) > 0)
            return "archived";
        if (Dapper.SqlMapper.ExecuteScalar<long>(conn,
                "SELECT count(*) FROM ignored WHERE youtube_id = @id", new { id }) > 0)
            return "ignored";
        return "duplicate"; // live job already queued/running
    }
}
