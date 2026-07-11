using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Tubelet.Pipeline;

public enum ConversionPlan
{
    /// <summary>Already an mp4-direct-play file — verify only, remux for faststart.</summary>
    Keep,
    /// <summary>Compatible codecs, wrong container / no faststart — <c>-c copy</c> into mp4.</summary>
    Remux,
    /// <summary>Incompatible streams (vp9/av1/opus) under the compat profile — re-encode to h264+aac.</summary>
    Transcode,
}

public sealed record ProbeResult(double DurationS, string? Vcodec, string? Acodec, long? Width, long? Height, string? Container);

/// <summary>
/// ffmpeg/ffprobe wrapper: probe streams, decide the Jellyfin-first conversion (§4.4), run it,
/// and sanity-check the output before it is allowed into the library.
/// </summary>
public sealed class Ffmpeg(IConfiguration config, ILogger<Ffmpeg>? log = null)
{
    private string FfmpegBin => config["TUBELET_FFMPEG"] ?? "ffmpeg";
    private string FfprobeBin => config["TUBELET_FFPROBE"] ?? "ffprobe";

    // ---- policy decision (pure, unit-tested) -------------------------------

    private static bool IsH264(string? v) => v is "h264" or "avc1";
    private static bool IsAac(string? a) => a is "aac" or "mp4a";
    private static bool IncompatibleVideo(string? v) => v is "vp9" or "vp09" or "av1" or "av01" or "vp8";
    private static bool IncompatibleAudio(string? a) => a is "opus" or "vorbis";

    /// <summary>The §4.4 decision table as a pure function of streams + quality profile.</summary>
    public static ConversionPlan Decide(string? vcodec, string? acodec, string? container, string profile)
    {
        var isMp4 = container is not null && container.Contains("mp4", StringComparison.OrdinalIgnoreCase);
        if (IsH264(vcodec) && IsAac(acodec) && isMp4) return ConversionPlan.Keep;
        if (IncompatibleVideo(vcodec) || IncompatibleAudio(acodec))
            return string.Equals(profile, "quality", StringComparison.OrdinalIgnoreCase)
                ? ConversionPlan.Remux : ConversionPlan.Transcode;
        return ConversionPlan.Remux; // compatible codecs, uncertain container/faststart
    }

    // ---- probe -------------------------------------------------------------

    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
    {
        var r = await Proc.RunAsync(FfprobeBin,
            ["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path], ct).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new InvalidOperationException($"ffprobe failed ({r.ExitCode}) on {path}: {r.Stderr}");

        using var doc = JsonDocument.Parse(r.Stdout);
        var root = doc.RootElement;

        string? vcodec = null, acodec = null, container = null;
        long? width = null, height = null;
        double duration = 0;

        if (root.TryGetProperty("format", out var fmt))
        {
            if (fmt.TryGetProperty("format_name", out var fn)) container = fn.GetString();
            if (fmt.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                duration = dv;
        }
        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                if (type == "video" && vcodec is null)
                {
                    vcodec = codec;
                    if (s.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number) width = w.GetInt64();
                    if (s.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number) height = h.GetInt64();
                }
                else if (type == "audio" && acodec is null) acodec = codec;
            }

        log?.LogDebug("ffprobe {Path}: container={Container} v={V} a={A} {W}x{H} dur={Dur:F1}s",
            path, container, vcodec ?? "none", acodec ?? "none", width, height, duration);
        return new ProbeResult(duration, vcodec, acodec, width, height, container);
    }

    // ---- hardware acceleration (§4.4) --------------------------------------

    /// <summary>
    /// Resolve the requested hwaccel mode to a concrete encoder for this host. "auto" probes for a
    /// mapped GPU (<c>/dev/dri</c> → VAAPI, nvidia device → NVENC); an explicit mode is taken as-is.
    /// <c>TUBELET_HWACCEL</c> forces a mode (tests pin "none"). Returns "none" for the libx264 path.
    /// </summary>
    public string ResolveHwaccel(string mode)
    {
        var forced = config["TUBELET_HWACCEL"];
        if (!string.IsNullOrEmpty(forced)) mode = forced;
        return mode switch
        {
            "none" => "none",
            "vaapi" => "vaapi",
            "qsv" => "qsv",
            "nvenc" => "nvenc",
            _ => Autodetect(), // "auto"
        };
    }

    private static string Autodetect()
    {
        if (File.Exists("/dev/dri/renderD128")) return "vaapi";
        if (File.Exists("/dev/nvidia0") || File.Exists("/dev/nvidiactl")) return "nvenc";
        return "none";
    }

    // ---- convert -----------------------------------------------------------

    /// <summary>Run the chosen plan from <paramref name="input"/> → <paramref name="output"/> (.mp4, +faststart).</summary>
    /// <param name="hwaccel">Resolved encoder ("none"|"vaapi"|"qsv"|"nvenc"). Only used for Transcode.</param>
    /// <param name="copyAllStreams">Copy every stream (<c>-map 0</c>) instead of just first v/a — preserves an
    /// embedded thumbnail/subs on the Keep/Remux paths (compat containers only).</param>
    public async Task ConvertAsync(string input, string output, ConversionPlan plan,
        string hwaccel = "none", bool copyAllStreams = false, CancellationToken ct = default)
    {
        if (plan == ConversionPlan.Transcode && hwaccel != "none")
        {
            var hwArgs = TranscodeCmd(input, output, hwaccel);
            log?.LogInformation("ffmpeg {Plan} via {Hw}: {Cmd}", plan, hwaccel, Proc.Render(FfmpegBin, hwArgs));
            var hwSw = Stopwatch.StartNew();
            var r = await Proc.RunAsync(FfmpegBin, hwArgs, ct).ConfigureAwait(false);
            if (r.ExitCode == 0)
            {
                log?.LogInformation("ffmpeg {Plan} via {Hw} done in {Elapsed} → {Output} ({Size:N0} bytes)",
                    plan, hwaccel, hwSw.Elapsed, output, OutputSize(output));
                return;
            }
            log?.LogWarning("hwaccel {Hw} transcode failed (exit {Code} after {Elapsed}), falling back to libx264 — stderr tail:\n{Err}",
                hwaccel, r.ExitCode, hwSw.Elapsed, Proc.Tail(r.Stderr, 15));
            TryDeleteQuiet(output);
        }

        var mode = plan == ConversionPlan.Transcode ? "none" : "copy";
        var args = TranscodeCmd(input, output, mode, copyAllStreams);
        log?.LogInformation("ffmpeg {Plan} ({Mode}): {Cmd}", plan, mode == "copy" ? "stream copy" : "libx264",
            Proc.Render(FfmpegBin, args));
        var sw = Stopwatch.StartNew();
        var res = await Proc.RunAsync(FfmpegBin, args, ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            log?.LogError("ffmpeg {Plan} failed (exit {Code} after {Elapsed}) — stderr tail:\n{Err}",
                plan, res.ExitCode, sw.Elapsed, Proc.Tail(res.Stderr, 15));
            throw new InvalidOperationException($"ffmpeg convert failed ({res.ExitCode}): {Proc.Tail(res.Stderr, 5)}");
        }
        log?.LogInformation("ffmpeg {Plan} done in {Elapsed} → {Output} ({Size:N0} bytes)",
            plan, sw.Elapsed, output, OutputSize(output));
    }

    private static long OutputSize(string path)
    {
        try { return new FileInfo(path).Length; } catch (IOException) { return -1; }
    }

    /// <summary>
    /// Build the ffmpeg argv for a conversion. <paramref name="videoMode"/>: "copy" (remux), "none"
    /// (libx264 CPU transcode), or a hw encoder ("vaapi"/"qsv"/"nvenc").
    /// </summary>
    private static List<string> TranscodeCmd(string input, string output, string videoMode, bool copyAllStreams = false)
    {
        List<string> args = ["-v", "error", "-y"];

        // Hardware-decode/init prologue must precede -i.
        if (videoMode == "vaapi") args.AddRange(["-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi",
                                                 "-vaapi_device", "/dev/dri/renderD128"]);
        else if (videoMode == "qsv") args.AddRange(["-init_hw_device", "qsv=hw", "-filter_hw_device", "hw"]);

        args.AddRange(["-i", input]);

        if (copyAllStreams) args.AddRange(["-map", "0"]);
        else args.AddRange(["-map", "0:v:0", "-map", "0:a:0?"]);

        switch (videoMode)
        {
            case "copy":
                args.AddRange(["-c", "copy"]);
                break;
            case "vaapi":
                args.AddRange(["-vf", "scale_vaapi=format=nv12", "-c:v", "h264_vaapi", "-qp", "22",
                               "-c:a", "aac", "-b:a", "192k"]);
                break;
            case "qsv":
                args.AddRange(["-vf", "hwupload=extra_hw_frames=64,format=qsv", "-c:v", "h264_qsv",
                               "-global_quality", "22", "-c:a", "aac", "-b:a", "192k"]);
                break;
            case "nvenc":
                args.AddRange(["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "22", "-pix_fmt", "yuv420p",
                               "-c:a", "aac", "-b:a", "192k"]);
                break;
            default: // "none" — CPU libx264
                args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                               "-c:a", "aac", "-b:a", "192k"]);
                break;
        }
        args.AddRange(["-movflags", "+faststart", output]);
        return args;
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    /// <summary>Duration within 2 s of expected and a decodable first frame — else the file is rejected.</summary>
    public async Task<bool> VerifyAsync(string path, double expectedDurationS, CancellationToken ct = default)
    {
        var probe = await ProbeAsync(path, ct).ConfigureAwait(false);
        if (probe.Vcodec is null)
        {
            log?.LogWarning("verify {Path}: REJECTED — no video stream (container={Container}, a={A})",
                path, probe.Container, probe.Acodec ?? "none");
            return false;
        }
        if (expectedDurationS > 0 && Math.Abs(probe.DurationS - expectedDurationS) > 2)
        {
            log?.LogWarning("verify {Path}: REJECTED — duration {Actual:F1}s vs expected {Expected:F1}s (off by {Delta:F1}s, tolerance 2s)",
                path, probe.DurationS, expectedDurationS, Math.Abs(probe.DurationS - expectedDurationS));
            return false;
        }

        var decode = await Proc.RunAsync(FfmpegBin,
            ["-v", "error", "-xerror", "-i", path, "-frames:v", "1", "-f", "null", "-"], ct).ConfigureAwait(false);
        if (decode.ExitCode != 0)
        {
            log?.LogWarning("verify {Path}: REJECTED — first-frame decode failed (exit {Code}): {Err}",
                path, decode.ExitCode, Proc.Tail(decode.Stderr, 5));
            return false;
        }
        log?.LogDebug("verify {Path}: ok (v={V} a={A} dur={Dur:F1}s)", path, probe.Vcodec, probe.Acodec ?? "none", probe.DurationS);
        return true;
    }

    // ---- images ------------------------------------------------------------

    /// <summary>Normalize any downloaded image to a jpg (optionally width-capped).</summary>
    public async Task ToJpegAsync(string input, string output, int? maxWidth = null, CancellationToken ct = default)
    {
        List<string> args = ["-v", "error", "-y", "-i", input];
        if (maxWidth is { } w) args.AddRange(["-vf", $"scale='min({w},iw)':-2"]);
        args.AddRange(["-frames:v", "1", output]);
        var r = await Proc.RunAsync(FfmpegBin, args, ct).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new InvalidOperationException($"ffmpeg image failed ({r.ExitCode}): {r.Stderr}");
    }

    /// <summary>Center-crop/scale to an exact WxH jpg (channel _thumb square / _tvart 16:9).</summary>
    public async Task CropJpegAsync(string input, string output, int w, int h, CancellationToken ct = default)
    {
        var vf = $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}";
        var r = await Proc.RunAsync(FfmpegBin, ["-v", "error", "-y", "-i", input, "-vf", vf, "-frames:v", "1", output], ct)
            .ConfigureAwait(false);
        if (r.ExitCode != 0) throw new InvalidOperationException($"ffmpeg crop failed ({r.ExitCode}): {r.Stderr}");
    }
}
