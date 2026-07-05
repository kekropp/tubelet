using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tubelet.Contracts;
using Tubelet.Data;
using Xunit;

namespace Tubelet.Tests;

/// <summary>
/// Contract tests for /api/jf/v1 — deserialized through the *actual plugin DTOs*
/// (Tubelet.Contracts), so the wire contract cannot drift between server and plugin.
/// </summary>
public class JfContractTests(TubeletFactory factory) : IClassFixture<TubeletFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static async Task<T> ReadAs<T>(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json)!;
    }

    [Fact]
    public async Task Videos_batch_returns_full_docs_and_skips_unknown_ids()
    {
        var resp = await _client.GetAsync(
            $"/api/jf/v1/videos?ids={FixtureSeeder.VideoWithSegments},doesnotexis,fixture0001");
        var docs = await ReadAs<VideoDoc[]>(resp);

        Assert.Equal(2, docs.Length);
        var rick = Assert.Single(docs, d => d.Id == FixtureSeeder.VideoWithSegments);
        Assert.Equal(FixtureSeeder.Channel1, rick.ChannelId);
        Assert.Equal("Never Gonna Give You Up", rick.Title);
        Assert.Contains("\n", rick.Description);                      // multi-line survives
        Assert.Equal("2009-10-25T06:57:33Z", rick.Published);          // full timestamp
        Assert.Equal(213, rick.DurationS);
        Assert.Contains("music", rick.Tags);
        Assert.Equal("/cache/videos/d/dQw4w9WgXcQ.jpg", rick.Thumb);
        Assert.Equal(2, rick.Chapters.Length);
        Assert.Equal("Intro", rick.Chapters[0].Title);
    }

    [Fact]
    public async Task Segments_are_mapped_to_jellyfin_types_server_side()
    {
        var resp = await _client.GetAsync($"/api/jf/v1/videos?ids={FixtureSeeder.VideoWithSegments}");
        var doc = (await ReadAs<VideoDoc[]>(resp)).Single();

        Assert.Equal(2, doc.Segments.Length);
        Assert.Equal("Commercial", doc.Segments[0].Type);   // sponsor → Commercial
        Assert.Equal(12.34, doc.Segments[0].StartS);
        Assert.Equal(56.78, doc.Segments[0].EndS);
        Assert.Equal("Outro", doc.Segments[1].Type);        // outro → Outro
    }

    [Fact]
    public async Task Channels_batch_returns_docs_with_art_urls()
    {
        var resp = await _client.GetAsync(
            $"/api/jf/v1/channels?ids={FixtureSeeder.Channel1},{FixtureSeeder.Channel2}");
        var docs = await ReadAs<ChannelDoc[]>(resp);

        Assert.Equal(2, docs.Length);
        var rick = Assert.Single(docs, d => d.Id == FixtureSeeder.Channel1);
        Assert.Equal("Rick Astley", rick.Name);
        Assert.StartsWith("/cache/channels/", rick.Thumb);
        Assert.NotNull(rick.Banner);
        Assert.NotNull(rick.Tvart);
    }

    [Fact]
    public async Task Changes_cursor_delta_flow()
    {
        // 1. full resync from cursor 0 sees the fixture catalog + playlists
        var first = await _client.GetAsync("/api/jf/v1/changes?since=0");
        var changes = await ReadAs<ChangesDoc>(first);
        Assert.True(changes.Videos.Length >= 5);
        Assert.Contains(FixtureSeeder.VideoWithSegments, changes.Videos);
        Assert.Single(changes.Playlists, p => p.Id == FixtureSeeder.CustomPlaylist);
        Assert.Equal(2, changes.Playlists[0].Entries.Length);

        // 2. caught up → 204 (Tubelet tracks no playback, so an idle library stays idle)
        var idle = await _client.GetAsync($"/api/jf/v1/changes?since={changes.NextCursor}");
        Assert.Equal(HttpStatusCode.NoContent, idle.StatusCode);
    }

    [Fact]
    public async Task Repo_manifest_is_served()
    {
        var resp = await _client.GetAsync("/repo/manifest.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var pkg = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("Tubelet", pkg.GetProperty("name").GetString());
        Assert.True(pkg.TryGetProperty("guid", out _));
        Assert.True(pkg.TryGetProperty("versions", out _));
    }
}
