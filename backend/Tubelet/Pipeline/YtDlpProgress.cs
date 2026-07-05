using System.Globalization;
using System.Text.Json;

namespace Tubelet.Pipeline;

/// <summary>One parsed <c>--progress-template "%(progress)j"</c> line from yt-dlp stdout.</summary>
public sealed record DownloadProgress(
    string Status,           // downloading | finished | error | …
    double Pct,              // 0..1 (best-effort; 0 when totals unknown)
    double? SpeedBytesPerSec,
    double? EtaSeconds,
    long? DownloadedBytes,
    long? TotalBytes)
{
    public string? SpeedText => SpeedBytesPerSec is { } s and > 0 ? Humanize.Rate(s) : null;
    public string? EtaText => EtaSeconds is { } e and >= 0 ? Humanize.Duration(e) : null;
}

public static class YtDlpProgress
{
    /// <summary>
    /// Parse a single JSON progress line. Returns false for non-JSON lines (yt-dlp interleaves
    /// plain log text on stdout) so callers can simply skip them.
    /// </summary>
    public static bool TryParse(string line, out DownloadProgress progress)
    {
        progress = default!;
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{') return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var status = Str(root, "status") ?? "downloading";
            var downloaded = Num(root, "downloaded_bytes");
            var total = Num(root, "total_bytes") ?? Num(root, "total_bytes_estimate");
            var speed = Num(root, "speed");
            var eta = Num(root, "eta");

            double pct = 0;
            if (status == "finished") pct = 1;
            else if (total is > 0 && downloaded is not null) pct = Math.Clamp(downloaded.Value / total.Value, 0, 1);

            progress = new DownloadProgress(status, pct, speed, eta,
                downloaded is null ? null : (long)downloaded, total is null ? null : (long)total);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // yt-dlp emits numbers, occasionally as strings, and null when unknown.
    private static double? Num(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }
}

/// <summary>Human-readable formatting for the queue UI (transfer rate, ETA).</summary>
public static class Humanize
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Rate(double bytesPerSec) => Size(bytesPerSec) + "/s";

    public static string Size(double bytes)
    {
        var i = 0;
        while (bytes >= 1024 && i < Units.Length - 1) { bytes /= 1024; i++; }
        return string.Create(CultureInfo.InvariantCulture, $"{bytes:0.#} {Units[i]}");
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
    }
}
