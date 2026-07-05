using Dapper;
using Tubelet.Data;
using Tubelet.Pipeline;
using Xunit;

namespace Tubelet.Tests;

public class MigratorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tubelet-mig-").FullName;

    private Database NewDb() => new(Path.Combine(_dir, "test.db"));

    [Fact]
    public void Migrates_fresh_database_and_is_idempotent()
    {
        var db = NewDb();
        Migrator.Migrate(db);
        Migrator.Migrate(db); // second run is a no-op

        using var conn = db.Open();
        Assert.Equal(1, conn.ExecuteScalar<long>("PRAGMA user_version"));
        Assert.Equal(0, conn.ExecuteScalar<long>("SELECT count(*) FROM videos"));
        Assert.Equal(0, Database.CurrentSeq(conn));
        Assert.Equal("wal", conn.ExecuteScalar<string>("PRAGMA journal_mode"));
    }

    [Fact]
    public void Change_sequence_is_strictly_monotonic()
    {
        var db = NewDb();
        Migrator.Migrate(db);
        using var conn = db.Open();
        var a = Database.NextSeq(conn);
        var b = Database.NextSeq(conn);
        Assert.Equal(a + 1, b);
        Assert.Equal(b, Database.CurrentSeq(conn));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

public class ClassifierTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", UrlKind.Video, "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42", UrlKind.Video, "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", UrlKind.Video, "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", UrlKind.Video, "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", UrlKind.Video, "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", UrlKind.Video, "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/playlist?list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", UrlKind.Playlist, "PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI")]
    [InlineData("https://www.youtube.com/channel/UCuAXFkgsw1L7xaCfnd5JJOw", UrlKind.Channel, "UCuAXFkgsw1L7xaCfnd5JJOw")]
    [InlineData("https://www.youtube.com/@RickAstleyYT", UrlKind.Channel, "@RickAstleyYT")]
    [InlineData("https://www.youtube.com/c/RickAstley", UrlKind.Channel, "c/RickAstley")]
    [InlineData("https://www.youtube.com/user/rickastley", UrlKind.Channel, "user/rickastley")]
    [InlineData("UCuAXFkgsw1L7xaCfnd5JJOw", UrlKind.Channel, "UCuAXFkgsw1L7xaCfnd5JJOw")]
    [InlineData("@RickAstleyYT", UrlKind.Channel, "@RickAstleyYT")]
    [InlineData("what even is this", UrlKind.Unknown, null)]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ", UrlKind.Unknown, null)]
    public void Classifies_pastes(string input, UrlKind kind, string? id)
    {
        var c = UrlClassifier.Classify(input);
        Assert.Equal(kind, c.Kind);
        Assert.Equal(id, c.Id);
    }

    [Fact]
    public void Watch_url_with_list_is_video_with_playlist_context()
    {
        var c = UrlClassifier.Classify(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI&index=3");
        Assert.Equal(UrlKind.Video, c.Kind);
        Assert.Equal("dQw4w9WgXcQ", c.Id);
        Assert.Equal("PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", c.PlaylistId);
    }
}

public class QueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tubelet-queue-").FullName;
    private readonly Database _db;

    public QueueTests()
    {
        _db = new Database(Path.Combine(_dir, "test.db"));
        Migrator.Migrate(_db);
    }

    [Fact]
    public void Claim_is_atomic_and_respects_priority_and_backoff()
    {
        var now = 1_720_000_000L;
        using var conn = _db.Open();
        Assert.True(Queries.EnqueueJob(conn, "aaaaaaaaaaa", priority: 5));
        Assert.True(Queries.EnqueueJob(conn, "bbbbbbbbbbb", priority: 1)); // user paste jumps queue
        Assert.True(Queries.EnqueueJob(conn, "ccccccccccc", priority: 5));
        conn.Execute("UPDATE jobs SET next_retry = @later WHERE youtube_id = 'ccccccccccc'",
            new { later = now + 3600 }); // backing off — not claimable yet

        using var conn2 = _db.Open();
        var first = Queries.ClaimNextJob(conn, now);
        var second = Queries.ClaimNextJob(conn2, now);
        var third = Queries.ClaimNextJob(conn, now);

        Assert.Equal("bbbbbbbbbbb", first!.YoutubeId);   // priority 1 first
        Assert.Equal("aaaaaaaaaaa", second!.YoutubeId);  // distinct row for second claimant
        Assert.Null(third);                              // c is gated by next_retry
        Assert.Equal(1, first.Attempts);
        Assert.Equal("fetching_meta", first.State);
    }

    [Fact]
    public void Enqueue_refuses_duplicates_but_revives_failed_jobs()
    {
        using var conn = _db.Open();
        Assert.True(Queries.EnqueueJob(conn, "ddddddddddd", priority: 5));
        Assert.False(Queries.EnqueueJob(conn, "ddddddddddd", priority: 1)); // live job → refused

        conn.Execute("UPDATE jobs SET state = 'failed', attempts = 3, last_error = 'x' WHERE youtube_id = 'ddddddddddd'");
        Assert.True(Queries.EnqueueJob(conn, "ddddddddddd", priority: 1));  // failed → revived

        var job = conn.QuerySingle<Tubelet.Domain.JobRow>(
            "SELECT * FROM jobs WHERE youtube_id = 'ddddddddddd'");
        Assert.Equal("queued", job.State);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(1, job.Priority);
        Assert.Null(job.LastError);
    }

    [Fact]
    public void Startup_recovery_requeues_jobs_left_mid_flight()
    {
        using var conn = _db.Open();
        Queries.EnqueueJob(conn, "eeeeeeeeeee", priority: 5);
        var claimed = Queries.ClaimNextJob(conn, 1_720_000_000L);
        Assert.NotNull(claimed);

        var reset = Queries.ResetStuckJobs(conn);
        Assert.Equal(1, reset);
        var job = conn.QuerySingle<Tubelet.Domain.JobRow>(
            "SELECT * FROM jobs WHERE youtube_id = 'eeeeeeeeeee'");
        Assert.Equal("queued", job.State);
        Assert.Equal(1, job.Attempts); // attempts survive the crash
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
