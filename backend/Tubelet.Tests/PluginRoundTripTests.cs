using System.Text.Json;
using Tubelet.Contracts;
using Tubelet.Data;
using Xunit;

namespace Tubelet.Tests;

/// <summary>
/// Round-trips real server responses through the *plugin's* deserializer configuration
/// (Web defaults, mirroring <c>TubeletClient.JsonOptions</c>) and the shared
/// <c>Tubelet.Contracts</c> DTOs the plugin compiles against. If the server's wire format
/// and the plugin's expectations ever diverge, one of these fails at build or assert time.
/// The full <c>TubeletClient</c> can't run here without dragging the entire Jellyfin ABI into
/// the test host, so we pin the exact serializer settings instead.
/// </summary>
public class PluginRoundTripTests(TubeletFactory factory) : IClassFixture<TubeletFactory>
{
    // Identical to Jellyfin.Plugin.Tubelet.TubeletClient.JsonOptions.
    private static readonly JsonSerializerOptions PluginOptions = new(JsonSerializerDefaults.Web);

    // The set the plugin's TubeletSegmentProvider parses to Jellyfin MediaSegmentType.
    private static readonly HashSet<string> KnownSegmentTypes =
        new(["Commercial", "Preview", "Recap", "Outro", "Intro"]);

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Video_doc_round_trips_through_plugin_deserializer()
    {
        var json = await _client.GetStringAsync($"/api/jf/v1/videos?ids={FixtureSeeder.VideoWithSegments}");

        var docs = JsonSerializer.Deserialize<VideoDoc[]>(json, PluginOptions)!;
        var video = Assert.Single(docs);

        Assert.Equal(FixtureSeeder.VideoWithSegments, video.Id);
        Assert.Equal("Never Gonna Give You Up", video.Title);
        Assert.Equal(FixtureSeeder.Channel1, video.ChannelId);
        Assert.Equal(213, video.DurationS);
        Assert.StartsWith("/cache/", video.Thumb);                 // server-relative; plugin makes it absolute
        Assert.NotEmpty(video.Chapters);

        // Segments arrive pre-mapped to Jellyfin type names the plugin can Enum.TryParse.
        Assert.NotEmpty(video.Segments);
        Assert.All(video.Segments, s =>
        {
            Assert.Contains(s.Type, KnownSegmentTypes);
            Assert.True(s.EndS > s.StartS);
        });

        // Playback is gone from the wire entirely — the raw JSON must not carry it.
        Assert.DoesNotContain("\"watched\"", json);
        Assert.DoesNotContain("\"position_s\"", json);
    }

    [Fact]
    public async Task Channel_doc_round_trips_with_art_urls()
    {
        var json = await _client.GetStringAsync($"/api/jf/v1/channels?ids={FixtureSeeder.Channel1}");

        var docs = JsonSerializer.Deserialize<ChannelDoc[]>(json, PluginOptions)!;
        var channel = Assert.Single(docs);

        Assert.Equal(FixtureSeeder.Channel1, channel.Id);
        Assert.Equal("Rick Astley", channel.Name);
        Assert.StartsWith("/cache/", channel.Thumb);
    }

    [Fact]
    public async Task Changes_doc_round_trips_without_playback_field()
    {
        var json = await _client.GetStringAsync("/api/jf/v1/changes?since=0");

        var changes = JsonSerializer.Deserialize<ChangesDoc>(json, PluginOptions)!;
        Assert.NotEmpty(changes.Videos);
        Assert.NotEmpty(changes.Playlists);
        Assert.False(string.IsNullOrEmpty(changes.NextCursor));

        // The delta feed no longer carries watched state.
        Assert.DoesNotContain("\"watched\"", json);
    }
}
