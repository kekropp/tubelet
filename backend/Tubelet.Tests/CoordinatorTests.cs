using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Tubelet.Tests;

/// <summary>
/// End-to-end pipeline test with a <b>fake yt-dlp</b> (a shell stub that emits progress JSON and
/// copies a real sample mp4) and the real ffmpeg. No network: intake → download → convert → index,
/// asserting the file lands in the library and a video doc appears.
/// </summary>
public sealed class PipelineFactory : WebApplicationFactory<Program>
{
    public string Root { get; } = Directory.CreateTempSubdirectory("tubelet-pipe-").FullName;
    public string SampleMp4 => Path.Combine(Root, "sample.mp4");
    public string MediaDir => Path.Combine(Root, "youtube");

    public bool ToolsAvailable { get; private set; }

    public PipelineFactory()
    {
        ToolsAvailable = Which("ffmpeg") && Which("ffprobe");
        if (ToolsAvailable) ToolsAvailable = MakeSampleMp4();
        if (ToolsAvailable) WriteFakeYtDlp();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("TUBELET_CACHE", Path.Combine(Root, "cache"));
        builder.UseSetting("TUBELET_MEDIA", MediaDir);
        builder.UseSetting("TUBELET_FIXTURES", "0");                         // clean db — only our job runs
        builder.UseSetting("TUBELET_YTDLP", Path.Combine(Root, "yt-dlp"));
        builder.UseSetting("TUBELET_SB_ENDPOINT", "http://127.0.0.1:9/");    // refused fast, never external
    }

    private bool MakeSampleMp4() => Run("ffmpeg",
        $"-v error -y -f lavfi -i testsrc=duration=1:size=320x240:rate=15 " +
        $"-f lavfi -i sine=frequency=440:duration=1 -c:v libx264 -pix_fmt yuv420p -c:a aac " +
        $"-shortest -movflags +faststart {SampleMp4}") && File.Exists(SampleMp4);

    private void WriteFakeYtDlp()
    {
        var path = Path.Combine(Root, "yt-dlp");
        File.WriteAllText(path, $$"""
            #!/usr/bin/env bash
            args="$*"
            if [[ "$args" == *"--version"* ]]; then echo "2099.01.01"; exit 0; fi
            for last in "$@"; do :; done
            id="${last##*v=}"   # the app now passes a https://www.youtube.com/watch?v=<id> URL
            if [[ "$args" == *"--progress-template"* ]]; then
              out=""; prev=""
              for a in "$@"; do [[ "$prev" == "-o" ]] && out="$a"; prev="$a"; done
              out="${out//%(id)s/$id}"; out="${out//%(ext)s/mp4}"
              mkdir -p "$(dirname "$out")"
              cp "{{SampleMp4}}" "$out"
              echo '{"status":"downloading","downloaded_bytes":500,"total_bytes":1000,"speed":100000,"eta":5}'
              echo '{"status":"finished","downloaded_bytes":1000,"total_bytes":1000}'
              exit 0
            fi
            if [[ "$args" == *"-J"* ]]; then
              printf '{"id":"%s","channel_id":"UCfake000000000000000001","channel":"Fake Channel","title":"Fake Video","description":"desc","duration":1,"timestamp":1600000000,"tags":["t"],"thumbnails":[]}\n' "$id"
              exit 0
            fi
            exit 1
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static bool Which(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(d => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, tool)));

    private static bool Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardError = true, UseShellExecute = false })!;
            p.WaitForExit(30_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class CoordinatorTests(PipelineFactory factory) : IClassFixture<PipelineFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Paste_url_downloads_converts_and_indexes_into_library()
    {
        if (!factory.ToolsAvailable) return; // ffmpeg/ffprobe absent — pipeline can't run here

        var resp = await _client.PostAsJsonAsync("/api/v1/intake", new { url = "https://youtu.be/abcdefghijk" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Poll until the pipeline indexes the video (yt-dlp → ffmpeg → move → row).
        HttpResponseMessage? video = null;
        for (var i = 0; i < 100; i++)
        {
            video = await _client.GetAsync("/api/v1/videos/abcdefghijk");
            if (video.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(200);
        }

        Assert.Equal(HttpStatusCode.OK, video!.StatusCode);

        // File landed in the Jellyfin library layout: <media>/<channel_id>/<id>.mp4
        var expected = Path.Combine(factory.MediaDir, "UCfake000000000000000001", "abcdefghijk.mp4");
        Assert.True(File.Exists(expected), $"expected media file at {expected}");

        // And the job finished.
        var queue = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/queue");
        var recent = queue.GetProperty("recent").EnumerateArray().ToList();
        Assert.Contains(recent, j => j.GetProperty("youtube_id").GetString() == "abcdefghijk"
                                  && j.GetProperty("state").GetString() == "done");
    }
}
