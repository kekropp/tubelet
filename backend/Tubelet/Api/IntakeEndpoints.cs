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
                    expander.ExpandInBackground(UrlKind.Playlist, c.Id!, priority: 1, ScopeOf(req));
                    return Results.Ok(new IntakeResult("playlist", c.Id, "expanding", [], []));

                case UrlKind.Channel:
                default:
                    expander.ExpandInBackground(UrlKind.Channel, c.Id!, priority: 1, ScopeOf(req));
                    return Results.Ok(new IntakeResult("channel", c.Id, "expanding", [], []));
            }
        });

        // Metadata-first preview: fetch the flat listing so the caller can choose how much of a
        // channel/playlist to actually download (all / newest N / since a date / none). Videos and
        // unrecognized input come straight back so the frontend can fall through to a normal intake.
        app.MapPost("/api/v1/intake/preview", async (PreviewRequest req, IntakeExpander expander) =>
        {
            UrlKind kind;
            string? id;
            var explicitKind = (req.Kind ?? "").Trim().ToLowerInvariant();
            if ((explicitKind == "channel" || explicitKind == "playlist") && !string.IsNullOrWhiteSpace(req.Id))
            {
                kind = explicitKind == "playlist" ? UrlKind.Playlist : UrlKind.Channel;
                id = req.Id!.Trim();
            }
            else
            {
                var c = UrlClassifier.Classify(req.Url ?? "");
                kind = c.Kind;
                id = c.Id;
            }

            if (kind is UrlKind.Video or UrlKind.Unknown || id is null)
                return Results.Ok(new PreviewResult(kind.ToString().ToLowerInvariant(), id, null, null, null, 0, false, []));

            var (listing, error) = await expander.FetchListingAsync(kind, id, CancellationToken.None);
            if (listing is null)
                return Results.UnprocessableEntity(new { error });

            var entries = listing.Entries;
            var sample = entries.Take(8)
                .Select(e => new PreviewEntry(e.Id, e.Title, e.UploadDate)).ToArray();
            return Results.Ok(new PreviewResult(
                kind.ToString().ToLowerInvariant(), id,
                listing.Title, listing.ChannelId, listing.ChannelName,
                entries.Length,
                HasDates: entries.Any(e => e.UploadDate is { Length: 8 }),
                Sample: sample));
        });
    }

    private static IntakeScope ScopeOf(IntakeRequest req)
        => req.Scope is { } s ? IntakeScope.From(s.Mode, s.N, s.After) : IntakeScope.All;

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
