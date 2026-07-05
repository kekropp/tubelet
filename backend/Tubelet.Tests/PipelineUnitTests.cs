using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Tubelet.Api;
using Tubelet.Pipeline;
using Tubelet.Sponsorblock;
using Xunit;

namespace Tubelet.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData("ERROR: [youtube] xxx: Sign in to confirm you're not a bot", ErrorKind.Throttled)]
    [InlineData("ERROR: unable to download video data: HTTP Error 429: Too Many Requests", ErrorKind.Throttled)]
    [InlineData("ERROR: [youtube] xxx: Private video. Sign in if you've been granted access", ErrorKind.Permanent)]
    [InlineData("ERROR: [youtube] xxx: Video unavailable. This video has been removed by the uploader", ErrorKind.Permanent)]
    [InlineData("ERROR: [youtube] xxx: This video is available to members only", ErrorKind.Permanent)]
    [InlineData("ERROR: [youtube] xxx: Video unavailable. This video is not available in your country", ErrorKind.Permanent)]
    [InlineData("ERROR: unable to download video data: <urlopen error timed out>", ErrorKind.Transient)]
    [InlineData("ERROR: The read operation timed out", ErrorKind.Transient)]
    public void Classifies_stderr(string stderr, ErrorKind expected)
    {
        Assert.Equal(expected, RetryPolicy.Classify(1, stderr).Kind);
    }

    [Fact]
    public void Reason_prefers_last_error_line()
    {
        var f = RetryPolicy.Classify(1, "[download] Destination: x\nWARNING: something\nERROR: Private video");
        Assert.Equal("ERROR: Private video", f.Reason);
    }

    [Theory]
    [InlineData("ERROR: [youtube] 0U1b_A-uhGw: Requested format is not available. Use --list-formats for a list of available formats", true)]
    [InlineData("ERROR: [youtube] xxx: Requested format not available", true)]
    [InlineData("ERROR: no video formats found!; please report this issue on ...", true)]
    [InlineData("ERROR: [youtube] xxx: Private video. Sign in if you've been granted access", false)]
    [InlineData("", false)]
    public void Detects_format_unavailable(string stderr, bool expected)
    {
        Assert.Equal(expected, RetryPolicy.IsFormatUnavailable(stderr));
    }

    [Fact]
    public void Backoff_grows_and_caps_at_60_min()
    {
        Assert.True(RetryPolicy.Backoff(1, 0.5).TotalMinutes is > 1 and < 4);
        Assert.True(RetryPolicy.Backoff(10, 0.5).TotalMinutes <= 60);       // 2^10 capped
        // jitter stays within ±25%
        Assert.True(RetryPolicy.Backoff(3, 0).TotalMinutes < RetryPolicy.Backoff(3, 1).TotalMinutes);
    }
}

public class YtDlpProgressTests
{
    [Fact]
    public void Parses_downloading_line_and_computes_pct()
    {
        var line = """{"status":"downloading","downloaded_bytes":500,"total_bytes":1000,"speed":1048576.0,"eta":42}""";
        Assert.True(YtDlpProgress.TryParse(line, out var p));
        Assert.Equal("downloading", p.Status);
        Assert.Equal(0.5, p.Pct, 3);
        Assert.Equal("1 MiB/s", p.SpeedText);
        Assert.Equal("0:42", p.EtaText);
    }

    [Fact]
    public void Uses_estimate_when_total_unknown_and_finished_is_full()
    {
        Assert.True(YtDlpProgress.TryParse(
            """{"status":"downloading","downloaded_bytes":250,"total_bytes":null,"total_bytes_estimate":1000}""", out var p));
        Assert.Equal(0.25, p.Pct, 3);

        Assert.True(YtDlpProgress.TryParse("""{"status":"finished","downloaded_bytes":1000}""", out var done));
        Assert.Equal(1.0, done.Pct, 3);
    }

    [Theory]
    [InlineData("[download]  50.0% of 10MiB")]  // plain log text, not JSON
    [InlineData("")]
    [InlineData("not json at all")]
    public void Skips_non_json_lines(string line)
    {
        Assert.False(YtDlpProgress.TryParse(line, out _));
    }
}

public class RateGateTests
{
    [Fact]
    public void Starts_full_then_refills_over_time()
    {
        var now = DateTimeOffset.UnixEpoch;
        var gate = new RateGate(opsPerHour: 2, clock: () => now);

        Assert.True(gate.TryAcquire());   // bucket starts full (capacity 2)
        Assert.True(gate.TryAcquire());
        Assert.False(gate.TryAcquire());  // empty

        now = now.AddMinutes(30);         // refill 1 token (2/hour → 1 per 30 min)
        Assert.True(gate.TryAcquire());
        Assert.False(gate.TryAcquire());
    }

    [Fact]
    public void Reports_time_until_next_token()
    {
        var now = DateTimeOffset.UnixEpoch;
        var gate = new RateGate(opsPerHour: 60, clock: () => now); // 1 token/min
        while (gate.TryAcquire()) { }
        Assert.InRange(gate.SecondsUntilNext(), 59, 61);
    }
}

public class SbClientTests
{
    private static readonly HashSet<string> Cats = ["sponsor", "intro", "outro"];

    [Fact]
    public void Hash_prefix_is_four_lowercase_hex()
    {
        var p = SbClient.HashPrefix("dQw4w9WgXcQ");
        Assert.Equal(4, p.Length);
        Assert.Matches("^[0-9a-f]{4}$", p);
        Assert.EndsWith(p, SbClient.BuildUrl("dQw4w9WgXcQ"));
    }

    [Fact]
    public void Filters_by_video_id_category_and_validates_bounds()
    {
        var json = """
        [
          {"videoID":"dQw4w9WgXcQ","segments":[
             {"category":"sponsor","segment":[12.3,56.7]},
             {"category":"music_offtopic","segment":[0,5]},
             {"category":"intro","segment":[70,60]},
             {"category":"outro","segment":[200,999]}
          ]},
          {"videoID":"OTHERvideo0","segments":[{"category":"sponsor","segment":[0,10]}]}
        ]
        """;
        var segs = SbClient.Parse(json, "dQw4w9WgXcQ", Cats, durationS: 213);

        Assert.Single(segs);                       // music_offtopic filtered out; intro inverted; outro past duration
        Assert.Equal("sponsor", segs[0].Category);
        Assert.Equal(12.3, segs[0].StartS, 3);
        Assert.Equal(56.7, segs[0].EndS, 3);
    }

    [Fact]
    public void Bad_json_yields_no_segments()
    {
        Assert.Empty(SbClient.Parse("not json", "x", Cats, 100));
        Assert.Empty(SbClient.Parse("{}", "x", Cats, 100));
    }

    [Fact]
    public void Fetch_url_carries_all_categories()
    {
        // Regression: without ?categories the SponsorBlock API returns ONLY sponsor segments,
        // silently dropping intro/outro/preview/etc.
        var q = SbClient.CategoriesQuery(["sponsor", "outro", "preview"]);
        Assert.StartsWith("?categories=", q);
        var decoded = Uri.UnescapeDataString(q["?categories=".Length..]);
        Assert.Equal("""["sponsor","outro","preview"]""", decoded);
    }
}

public class YtDlpArgsTests
{
    [Theory]
    [InlineData("--n1s8IoOuc")]  // real YouTube id that begins with '-'
    [InlineData("-abc_def123")]
    [InlineData("dQw4w9WgXcQ")]
    public void WatchUrl_never_looks_like_a_cli_option(string id)
    {
        var url = YtDlp.WatchUrl(id);
        Assert.Equal("https://www.youtube.com/watch?v=" + id, url);
        Assert.StartsWith("https://", url);   // never begins with '-', so yt-dlp treats it as a URL
    }
}

public class FfmpegPolicyTests
{
    [Theory]
    // h264+aac in mp4 → keep, both profiles
    [InlineData("h264", "aac", "mov,mp4,m4a,3gp,3g2,mj2", "compat", ConversionPlan.Keep)]
    [InlineData("avc1", "mp4a", "mp4", "quality", ConversionPlan.Keep)]
    // vp9/av1/opus → transcode under compat, remux under quality
    [InlineData("vp9", "opus", "webm", "compat", ConversionPlan.Transcode)]
    [InlineData("av1", "opus", "webm", "compat", ConversionPlan.Transcode)]
    [InlineData("vp9", "opus", "webm", "quality", ConversionPlan.Remux)]
    // compatible codecs, wrong container → remux either way
    [InlineData("h264", "aac", "matroska,webm", "compat", ConversionPlan.Remux)]
    [InlineData("h264", "aac", "matroska,webm", "quality", ConversionPlan.Remux)]
    public void Decides_per_streams_and_profile(string v, string a, string container, string profile, ConversionPlan expected)
    {
        Assert.Equal(expected, Ffmpeg.Decide(v, a, container, profile));
    }
}

public class InfoJsonTests
{
    [Fact]
    public void Maps_core_fields_chapters_and_published_timestamp()
    {
        var json = """
        {
          "id":"dQw4w9WgXcQ","channel_id":"UCuAXFkgsw1L7xaCfnd5JJOw","channel":"Rick Astley",
          "title":"Never Gonna Give You Up","description":"the song","duration":213,
          "timestamp":1256454453,"tags":["music","80s"],
          "chapters":[{"start_time":0.0,"end_time":10.0,"title":"Intro"},{"start_time":10.0,"title":"Verse"}],
          "thumbnails":[{"url":"http://x/lo.jpg","width":120,"height":90},{"url":"http://x/hi.jpg","width":1280,"height":720}]
        }
        """;
        var m = YtDlp.ParseInfoJson(json);
        Assert.Equal("dQw4w9WgXcQ", m.Id);
        Assert.Equal("UCuAXFkgsw1L7xaCfnd5JJOw", m.ChannelId);
        Assert.Equal("Rick Astley", m.ChannelName);
        Assert.Equal(213, m.DurationS);
        Assert.Equal(["music", "80s"], m.Tags);
        Assert.Equal(2, m.Chapters.Length);
        Assert.Equal("Intro", m.Chapters[0].Title);
        Assert.Equal("2009-10-25T07:07:33Z", m.PublishedIso);
        Assert.Equal("http://x/hi.jpg", m.ThumbnailUrl);   // highest resolution wins
    }

    [Fact]
    public void Falls_back_to_upload_date_and_uploader_id()
    {
        var m = YtDlp.ParseInfoJson("""
            {"id":"abc","uploader_id":"@handle","uploader":"Some One","title":"t","upload_date":"20200115"}
            """);
        Assert.Equal("@handle", m.ChannelId);
        Assert.Equal("Some One", m.ChannelName);
        Assert.Equal("2020-01-15T00:00:00Z", m.PublishedIso);
        Assert.Empty(m.Chapters);
    }
}

public class FlatPlaylistTests
{
    [Fact]
    public void Parses_flat_playlist_entries_and_metadata()
    {
        var json = """
        {"id":"PL123","title":"My List","channel":"Someone","channel_id":"UCxyz","description":"d",
         "entries":[
            {"id":"aaaaaaaaaaa","title":"One"},
            {"id":"bbbbbbbbbbb","title":"Two"},
            {"id":"short","title":"skip"}
         ]}
        """;
        var l = FlatPlaylist.Parse(json);
        Assert.Equal("PL123", l.Id);
        Assert.Equal("My List", l.Title);
        Assert.Equal("UCxyz", l.ChannelId);
        Assert.Equal(2, l.Entries.Length);          // 5-char id rejected (not 11)
        Assert.Equal("aaaaaaaaaaa", l.Entries[0].Id);
    }

    [Fact]
    public void Flattens_nested_channel_tabs_and_dedupes()
    {
        var json = """
        {"id":"UCabc","title":"Chan",
         "entries":[
            {"title":"Videos","entries":[{"id":"aaaaaaaaaaa"},{"id":"bbbbbbbbbbb"}]},
            {"title":"Live","entries":[{"id":"bbbbbbbbbbb"},{"id":"ccccccccccc"}]}
         ]}
        """;
        var l = FlatPlaylist.Parse(json);
        Assert.Equal(["aaaaaaaaaaa", "bbbbbbbbbbb", "ccccccccccc"], l.Entries.Select(e => e.Id));
    }
}

public class PipelineOptionsTests
{
    [Fact]
    public void Network_defaults_apply_on_empty_or_partial()
    {
        Assert.Equal(30, NetworkOptions.LoadRaw(null).Resolved().OpsPerHour);
        var partial = NetworkOptions.LoadRaw("""{"download_workers":4}""");
        Assert.Equal(4, partial.DownloadWorkers);
        Assert.Equal(30, partial.OpsPerHour);      // untouched field keeps its default
        Assert.Equal(4, partial.ConcurrentFragments);
    }

    [Fact]
    public void Network_clamps_insane_values()
    {
        var r = NetworkOptions.LoadRaw("""{"download_workers":9999,"ops_per_hour":0}""");
        Assert.Equal(16, r.DownloadWorkers);
        Assert.Equal(1, r.OpsPerHour);
    }

    [Fact]
    public void Quality_parses_new_fields_and_normalizes_hwaccel()
    {
        var q = JsonSerializer.Deserialize(
            """{"profile":"quality","hwaccel":"NVENC","embed_subs":true,"embed_thumbnail":false,"sub_langs":"en,es"}""",
            ApiJsonContext.Default.QualityOptions)!;
        Assert.Equal("quality", q.ResolvedProfile());
        Assert.Equal("nvenc", q.ResolvedHwaccel());
        Assert.True(q.WantsSubs);
        Assert.False(q.WantsThumbnail);
        Assert.Equal("en,es", q.ResolvedSubLangs());
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("off", "none")]
    [InlineData("libx264", "none")]
    [InlineData("vaapi", "vaapi")]
    [InlineData("cuda", "nvenc")]
    [InlineData("garbage", "auto")]
    public void Quality_hwaccel_mapping(string? input, string expected)
    {
        Assert.Equal(expected, new QualityOptions(Profile: "compat", Hwaccel: input).ResolvedHwaccel());
        Assert.Equal("en.*", new QualityOptions("compat").ResolvedSubLangs()); // default langs
    }
}

public class MaintenanceOptionsTests
{
    [Fact]
    public void Defaults_survive_resolve()
    {
        var d = MaintenanceOptions.Defaults.Resolved();
        Assert.Equal("0 4 * * 0", d.SbRefreshCron);
        Assert.Equal(7, d.PartTtlDays);
        Assert.True(d.BackupEnabled);
        Assert.False(d.PoTokenEnabled);
    }

    [Fact]
    public void Invalid_cron_falls_back_to_default()
    {
        var r = new MaintenanceOptions("not a cron", null, "", null, null, null, null, null).Resolved();
        Assert.Equal(MaintenanceOptions.Defaults.SbRefreshCron, r.SbRefreshCron);
        Assert.Equal(MaintenanceOptions.Defaults.JanitorCron, r.JanitorCron);
    }

    [Fact]
    public void Clamps_ttl_and_keep()
    {
        var hi = new MaintenanceOptions(null, null, null, PartTtlDays: 9999, null, BackupKeep: 9999, null, null).Resolved();
        Assert.Equal(365, hi.PartTtlDays);
        Assert.Equal(100, hi.BackupKeep);
        var lo = new MaintenanceOptions(null, null, null, PartTtlDays: 0, null, BackupKeep: 0, null, null).Resolved();
        Assert.Equal(1, lo.PartTtlDays);
        Assert.Equal(1, lo.BackupKeep);
    }
}

public class IntakeScopeTests
{
    // Newest-first listing, like yt-dlp returns for a channel/playlist.
    private static FlatEntry E(string id, string? date = null) => new(id, id, null, null, UploadDate: date);
    private static readonly FlatEntry[] Listing =
    [
        E("a", "20260601"), E("b", "20260501"), E("c"), E("d", "20260101"), E("e", "20251201"),
    ];

    private static string[] Ids(IntakeScope s) => s.Slice(Listing).Select(x => x.Id).ToArray();

    [Fact]
    public void All_takes_everything()
        => Assert.Equal(new[] { "a", "b", "c", "d", "e" }, Ids(IntakeScope.All));

    [Fact]
    public void None_takes_nothing()
        => Assert.Empty(Ids(IntakeScope.From("none", null, null)));

    [Fact]
    public void Newest_takes_the_first_n()
        => Assert.Equal(new[] { "a", "b" }, Ids(IntakeScope.From("newest", 2, null)));

    [Fact]
    public void Newest_without_a_count_falls_back_to_all()
        => Assert.Equal(ScopeMode.All, IntakeScope.From("newest", null, null).Mode);

    [Fact]
    public void After_stops_at_the_first_entry_below_the_floor()
        // Undated "c" sits above the floor cutoff so it's kept; "d"/"e" (dated before) are dropped.
        => Assert.Equal(new[] { "a", "b", "c" }, Ids(IntakeScope.From("after", null, "2026-02-01")));

    [Fact]
    public void After_normalizes_dashed_and_plain_dates()
    {
        Assert.Equal("20260201", IntakeScope.From("after", null, "2026-02-01").DateFloor);
        Assert.Equal("20260201", IntakeScope.From("after", null, "20260201").DateFloor);
    }

    [Fact]
    public void After_with_a_bad_date_falls_back_to_all()
        => Assert.Equal(ScopeMode.All, IntakeScope.From("after", null, "nonsense").Mode);

    [Fact]
    public void Unknown_or_blank_mode_is_all()
    {
        Assert.Equal(ScopeMode.All, IntakeScope.From(null, null, null).Mode);
        Assert.Equal(ScopeMode.All, IntakeScope.From("wat", null, null).Mode);
    }
}

public class FfmpegHwaccelTests
{
    private static Ffmpeg Make(string? forced = null)
    {
        var cfg = new ConfigurationBuilder();
        if (forced is not null) cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["TUBELET_HWACCEL"] = forced });
        return new Ffmpeg(cfg.Build());
    }

    [Theory]
    [InlineData("none", "none")]
    [InlineData("vaapi", "vaapi")]
    [InlineData("qsv", "qsv")]
    [InlineData("nvenc", "nvenc")]
    public void Explicit_modes_pass_through(string mode, string expected)
    {
        Assert.Equal(expected, Make().ResolveHwaccel(mode));
    }

    [Fact]
    public void Env_override_forces_mode()
    {
        Assert.Equal("none", Make(forced: "none").ResolveHwaccel("nvenc"));
    }
}
