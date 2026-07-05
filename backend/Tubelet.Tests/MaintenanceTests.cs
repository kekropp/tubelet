using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Tubelet;
using Tubelet.Data;
using Tubelet.Scheduling;
using Tubelet.Sponsorblock;
using Xunit;

namespace Tubelet.Tests;

public class CookiesApiTests(TubeletFactory factory) : IClassFixture<TubeletFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly TubeletFactory _factory = factory;

    private const string ValidJar =
        "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tPREF\tf1=50000000\n";

    private static StringContent Text(string s) => new(s, Encoding.UTF8, "text/plain");

    [Fact]
    public async Task Rejects_non_netscape_body()
    {
        var r = await _client.PostAsync("/api/v1/cookies", Text("just some random text with no cookie lines"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Upload_validate_delete_lifecycle()
    {
        // Upload — the offline stub yt-dlp fails, so the jar stores but is marked not-valid.
        var up = await _client.PostAsync("/api/v1/cookies", Text(ValidJar));
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var status = JsonDocument.Parse(await up.Content.ReadAsStringAsync()).RootElement;
        Assert.True(status.GetProperty("present").GetBoolean());
        Assert.False(status.GetProperty("valid").GetBoolean());

        // The jar is on disk and is 0600 (owner-only) on POSIX.
        var paths = _factory.Services.GetRequiredService<AppPaths>();
        var jar = Path.Combine(paths.CookiesDir, "cookies.txt");
        Assert.True(File.Exists(jar));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(jar);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        // Status GET reflects presence; the jar contents are never served back.
        var get = JsonDocument.Parse(await _client.GetStringAsync("/api/v1/cookies")).RootElement;
        Assert.True(get.GetProperty("present").GetBoolean());

        var val = await _client.PostAsync("/api/v1/cookies/validate", null);
        Assert.Equal(HttpStatusCode.OK, val.StatusCode);

        var del = await _client.DeleteAsync("/api/v1/cookies");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.False(File.Exists(jar));
        var after = JsonDocument.Parse(await _client.GetStringAsync("/api/v1/cookies")).RootElement;
        Assert.False(after.GetProperty("present").GetBoolean());
    }
}

public class MaintenanceApiTests(TubeletFactory factory) : IClassFixture<TubeletFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Maintenance_settings_roundtrip()
    {
        var put = await _client.PutAsJsonAsync("/api/v1/settings/maintenance", new
        {
            sb_refresh_cron = "0 6 * * 0",
            part_ttl_days = 3,
            backup_enabled = false,
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var body = JsonDocument.Parse(await _client.GetStringAsync("/api/v1/settings/maintenance")).RootElement;
        Assert.Equal("0 6 * * 0", body.GetProperty("sb_refresh_cron").GetString());
        Assert.Equal(3, body.GetProperty("part_ttl_days").GetInt32());
        Assert.False(body.GetProperty("backup_enabled").GetBoolean());
    }

    [Fact]
    public async Task Healthz_is_ok()
    {
        var r = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Backup_endpoint_writes_a_snapshot()
    {
        var r = await _client.PostAsync("/api/v1/system/backup", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("bytes").GetInt64() > 0);
        Assert.EndsWith(".db", doc.GetProperty("file").GetString());
    }
}

public class JanitorTests(TubeletFactory factory) : IClassFixture<TubeletFactory>
{
    private readonly TubeletFactory _factory = factory;

    [Fact]
    public void Deletes_only_aged_orphans_keeping_live_and_recent()
    {
        _ = _factory.CreateClient(); // force the host (and services) to build
        var paths = _factory.Services.GetRequiredService<AppPaths>();
        var db = _factory.Services.GetRequiredService<Database>();
        var janitor = _factory.Services.GetRequiredService<Janitor>();

        var oldOrphan = Path.Combine(paths.IncompleteDir, "orphanoldxx.mp4.part");
        var newOrphan = Path.Combine(paths.IncompleteDir, "orphannewxx.mp4.part");
        var liveFile = Path.Combine(paths.IncompleteDir, "livejobxxxx.f399.webm.part");
        File.WriteAllText(oldOrphan, "x");
        File.WriteAllText(newOrphan, "x");
        File.WriteAllText(liveFile, "x");

        var old = DateTime.UtcNow.AddDays(-30);
        File.SetLastWriteTimeUtc(oldOrphan, old);
        File.SetLastWriteTimeUtc(liveFile, old);

        using (var conn = db.Open())
            conn.Execute("INSERT INTO jobs(youtube_id, state, added_at) VALUES('livejobxxxx','queued',0)");

        var removed = janitor.DeleteOrphanParts(7);

        Assert.False(File.Exists(oldOrphan));  // aged + no job → gone
        Assert.True(File.Exists(newOrphan));   // orphan but not old enough → kept
        Assert.True(File.Exists(liveFile));    // aged but a live job could resume it → kept
        Assert.True(removed >= 1);

        using (var conn = db.Open()) conn.Execute("DELETE FROM jobs WHERE youtube_id = 'livejobxxxx'");
    }

    [Fact]
    public void Backup_creates_a_vacuumed_copy()
    {
        _ = _factory.CreateClient();
        var paths = _factory.Services.GetRequiredService<AppPaths>();
        var janitor = _factory.Services.GetRequiredService<Janitor>();

        var file = janitor.BackupDatabase();
        Assert.NotNull(file);
        Assert.True(File.Exists(file));
        Assert.StartsWith(paths.BackupDir, file);
    }
}

public class SbRefresherTests(TubeletFactory factory) : IClassFixture<TubeletFactory>
{
    private readonly TubeletFactory _factory = factory;

    [Fact]
    public async Task Refreshes_only_recent_videos()
    {
        _ = _factory.CreateClient();
        var db = _factory.Services.GetRequiredService<Database>();
        var refresher = _factory.Services.GetRequiredService<SbRefresher>();

        var recentPublished = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        using (var conn = db.Open())
        {
            conn.Execute("INSERT OR IGNORE INTO channels(channel_id, name) VALUES('UCsbtestchan','SB Test')");
            conn.Execute("""
                INSERT OR REPLACE INTO videos(youtube_id, channel_id, title, published, duration_s,
                    media_path, downloaded_at, changed_at)
                VALUES('sbrecent000','UCsbtestchan','recent', @pub, 100, 'UCsbtestchan/sbrecent000.mp4', 0, 1),
                      ('sbold000000','UCsbtestchan','old', '2001-01-01T00:00:00Z', 100, 'UCsbtestchan/sbold000000.mp4', 0, 1)
                """, new { pub = recentPublished });
        }

        await refresher.RefreshRecentAsync();

        using (var conn = db.Open())
        {
            var recent = conn.ExecuteScalar<long?>("SELECT sb_refreshed FROM videos WHERE youtube_id='sbrecent000'");
            var old = conn.ExecuteScalar<long?>("SELECT sb_refreshed FROM videos WHERE youtube_id='sbold000000'");
            Assert.NotNull(recent);   // within 30 days → re-checked and stamped
            Assert.Null(old);         // outside the window → untouched
        }
    }
}
