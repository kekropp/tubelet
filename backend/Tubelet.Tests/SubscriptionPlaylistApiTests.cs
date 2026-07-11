using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tubelet.Tests;

public class SubscriptionApiTests(TubeletFactory factory) : IClassFixture<TubeletFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Lists_seeded_subscriptions()
    {
        var list = await Json(await _client.GetAsync("/api/v1/subscriptions"));
        Assert.True(list.GetArrayLength() >= 2);
        Assert.Contains(list.EnumerateArray(),
            s => s.GetProperty("target_id").GetString() == "UCuAXFkgsw1L7xaCfnd5JJOw");
    }

    [Fact]
    public async Task Create_update_delete_roundtrip()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/subscriptions", new
        {
            kind = "channel",
            target_id = "@RoundTripChannel",
            cron = "0 */4 * * *",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var doc = await Json(created);
        var id = doc.GetProperty("id").GetInt64();
        Assert.Equal("channel", doc.GetProperty("kind").GetString());
        Assert.True(doc.GetProperty("next_check").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var patched = await Json(await _client.PatchAsJsonAsync($"/api/v1/subscriptions/{id}", new
        {
            cron = "0 9 * * *",
            filter_json = "{\"max_items\":3}",
            enabled = false,
        }));
        Assert.Equal("0 9 * * *", patched.GetProperty("cron").GetString());
        Assert.False(patched.GetProperty("enabled").GetBoolean());
        Assert.Equal("{\"max_items\":3}", patched.GetProperty("filter_json").GetString());

        var del = await _client.DeleteAsync($"/api/v1/subscriptions/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/subscriptions/{id}")).StatusCode);
    }

    [Fact]
    public async Task Rejects_bad_kind_and_invalid_cron()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(
            "/api/v1/subscriptions", new { kind = "banana", target_id = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(
            "/api/v1/subscriptions", new { kind = "channel", target_id = "x", cron = "nope" })).StatusCode);
    }

    [Fact]
    public async Task Quality_profile_roundtrips_and_rejects_unknown_values()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/subscriptions", new
        {
            kind = "channel",
            target_id = "@QualityProfChannel",
            quality_prof = "720p",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var doc = await Json(created);
        var id = doc.GetProperty("id").GetInt64();
        Assert.Equal("720p", doc.GetProperty("quality_prof").GetString());

        var patched = await Json(await _client.PatchAsJsonAsync($"/api/v1/subscriptions/{id}",
            new { quality_prof = "custom:bv*[height<=1440]+ba/b" }));
        Assert.Equal("custom:bv*[height<=1440]+ba/b", patched.GetProperty("quality_prof").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PatchAsJsonAsync(
            $"/api/v1/subscriptions/{id}", new { quality_prof = "1440p" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(
            "/api/v1/subscriptions", new { kind = "channel", target_id = "@BadProf", quality_prof = "custom:" })).StatusCode);

        await _client.DeleteAsync($"/api/v1/subscriptions/{id}");
    }

    [Fact]
    public async Task Duplicate_target_conflicts()
    {
        var body = new { kind = "channel", target_id = "@DuplicateTarget" };
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/subscriptions", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/v1/subscriptions", body)).StatusCode);
    }

    [Fact]
    public async Task Scan_and_backlog_are_accepted()
    {
        // Uses the offline yt-dlp stub; we only assert the request is accepted (work runs in background).
        var list = await Json(await _client.GetAsync("/api/v1/subscriptions"));
        var id = list[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsync($"/api/v1/subscriptions/{id}/scan", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsync($"/api/v1/subscriptions/{id}/backlog", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/v1/subscriptions/999999/scan", null)).StatusCode);
    }
}

public class PlaylistApiTests(TubeletFactory factory) : IClassFixture<TubeletFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Lists_seeded_custom_playlist_with_count()
    {
        var list = await Json(await _client.GetAsync("/api/v1/playlists"));
        var fav = list.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Favorites");
        Assert.Equal("custom", fav.GetProperty("type").GetString());
        Assert.Equal(2, fav.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Create_custom_playlist_and_bumps_change_cursor()
    {
        var before = await Json(await _client.GetAsync("/api/jf/v1/changes?since=0"));
        var beforeCursor = long.Parse(before.GetProperty("next_cursor").GetString()!);

        var created = await _client.PostAsJsonAsync("/api/v1/playlists", new
        {
            name = "My Mix",
            description = "hand-picked",
            entries = new[] { "dQw4w9WgXcQ", "fixture0001" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var doc = await Json(created);
        var id = doc.GetProperty("id").GetString()!;
        Assert.StartsWith("TL-", id);
        Assert.Equal("custom", doc.GetProperty("type").GetString());
        Assert.Equal(2, doc.GetProperty("entries").GetArrayLength());

        // The new playlist must surface on the /changes feed past the earlier cursor.
        var changes = await Json(await _client.GetAsync($"/api/jf/v1/changes?since={beforeCursor}"));
        Assert.Contains(changes.GetProperty("playlists").EnumerateArray(),
            p => p.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task Patch_replaces_entries_and_renames()
    {
        var id = (await Json(await _client.PostAsJsonAsync("/api/v1/playlists", new
        {
            name = "Editable",
            entries = new[] { "dQw4w9WgXcQ" },
        }))).GetProperty("id").GetString()!;

        var patched = await Json(await _client.PatchAsJsonAsync($"/api/v1/playlists/{id}", new
        {
            name = "Renamed",
            entries = new[] { "fixture0001", "fixture0003" },
        }));
        Assert.Equal("Renamed", patched.GetProperty("name").GetString());
        Assert.Equal(new[] { "fixture0001", "fixture0003" },
            patched.GetProperty("entries").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task Delete_removes_playlist()
    {
        var id = (await Json(await _client.PostAsJsonAsync("/api/v1/playlists", new { name = "Trash" })))
            .GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/v1/playlists/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/playlists/{id}")).StatusCode);
    }

    [Fact]
    public async Task Rejects_nameless_playlist() =>
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/v1/playlists", new { name = "  " })).StatusCode);
}
