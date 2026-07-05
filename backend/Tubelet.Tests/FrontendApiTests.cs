using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tubelet.Tests;

public class FrontendApiTests(TubeletFactory factory) : IClassFixture<TubeletFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Intake_enqueues_video_then_reports_duplicate()
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/intake",
            new { url = "https://youtu.be/abcdefghijk" });
        var body = await ReadJson(resp);
        Assert.Equal("video", body.GetProperty("kind").GetString());
        Assert.Equal("enqueued", body.GetProperty("status").GetString());
        Assert.Equal("abcdefghijk", body.GetProperty("enqueued")[0].GetString());

        var again = await _client.PostAsJsonAsync("/api/v1/intake",
            new { url = "https://www.youtube.com/watch?v=abcdefghijk" });
        var body2 = await ReadJson(again);
        Assert.Equal("duplicate", body2.GetProperty("status").GetString());

        var queue = await ReadJson(await _client.GetAsync("/api/v1/queue"));
        var active = queue.GetProperty("active").EnumerateArray().ToList();
        Assert.Contains(active, j => j.GetProperty("youtube_id").GetString() == "abcdefghijk"
                                  && j.GetProperty("priority").GetInt32() == 1);
    }

    [Fact]
    public async Task Intake_reports_archived_for_existing_video()
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/intake",
            new { url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" });
        var body = await ReadJson(resp);
        Assert.Equal("archived", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Intake_classifies_channel_and_playlist()
    {
        var ch = await ReadJson(await _client.PostAsJsonAsync("/api/v1/intake",
            new { url = "https://www.youtube.com/@RickAstleyYT" }));
        Assert.Equal("channel", ch.GetProperty("kind").GetString());
        Assert.Equal("expanding", ch.GetProperty("status").GetString());

        var pl = await ReadJson(await _client.PostAsJsonAsync("/api/v1/intake",
            new { url = "https://www.youtube.com/playlist?list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI" }));
        Assert.Equal("playlist", pl.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Intake_rejects_garbage()
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/intake", new { url = "not a url at all" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Library_search_uses_fts_prefix_matching()
    {
        var resp = await ReadJson(await _client.GetAsync("/api/v1/videos?query=sqli"));
        Assert.Equal(1, resp.GetProperty("total").GetInt64());
        Assert.Equal("fixture0001", resp.GetProperty("items")[0].GetProperty("id").GetString());

        var byChannel = await ReadJson(await _client.GetAsync(
            "/api/v1/videos?channel=UCuAXFkgsw1L7xaCfnd5JJOw&sort=published"));
        Assert.Equal(2, byChannel.GetProperty("total").GetInt64());
        // ascending sort: 2009 video first
        Assert.Equal("dQw4w9WgXcQ", byChannel.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Queue_actions_retry_and_priority()
    {
        var queue = await ReadJson(await _client.GetAsync("/api/v1/queue"));
        var failed = queue.GetProperty("failed")[0];
        var id = failed.GetProperty("id").GetInt64();

        var retried = await ReadJson(await _client.PostAsync($"/api/v1/queue/{id}/retry", null));
        Assert.Equal("queued", retried.GetProperty("state").GetString());
        Assert.Equal(0, retried.GetProperty("attempts").GetInt32());

        var bumped = await ReadJson(await _client.PostAsJsonAsync(
            $"/api/v1/queue/{id}/priority", new { priority = 2 }));
        Assert.Equal(2, bumped.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task System_reports_stats_and_settings_roundtrip()
    {
        var sys = await ReadJson(await _client.GetAsync("/api/v1/system"));
        Assert.True(sys.GetProperty("video_count").GetInt64() >= 5);
        Assert.True(sys.GetProperty("queue").GetProperty("failed").GetInt32() >= 0);

        var put = await _client.PutAsJsonAsync("/api/v1/settings/network",
            new { limit_rate = "4M", sleep_interval = 3 });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var got = await ReadJson(await _client.GetAsync("/api/v1/settings/network"));
        Assert.Equal("4M", got.GetProperty("limit_rate").GetString());
    }

    [Fact]
    public async Task Delete_video_removes_it_from_library_and_search()
    {
        var del = await _client.DeleteAsync("/api/v1/videos/fixture0002?also_ignore=1");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var gone = await _client.GetAsync("/api/v1/videos/fixture0002");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        var search = await ReadJson(await _client.GetAsync("/api/v1/videos?query=remux"));
        Assert.Equal(0, search.GetProperty("total").GetInt64());

        // ignored: re-intake is refused
        var intake = await ReadJson(await _client.PostAsJsonAsync("/api/v1/intake",
            new { url = "https://youtu.be/fixture0002" }));
        Assert.Equal("ignored", intake.GetProperty("status").GetString());
    }
}
