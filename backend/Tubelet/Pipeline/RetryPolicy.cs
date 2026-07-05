using System.Text.RegularExpressions;

namespace Tubelet.Pipeline;

public enum ErrorKind
{
    /// <summary>Network/5xx/timeout — back off and retry until max_attempts.</summary>
    Transient,
    /// <summary>429 / bot-check — pause the whole queue for a cooldown, don't burn attempts.</summary>
    Throttled,
    /// <summary>Private/deleted/geo/members-only — fail immediately, no retries.</summary>
    Permanent,
}

public sealed record Failure(ErrorKind Kind, string Reason);

/// <summary>
/// Classifies a failed yt-dlp run (exit code + stderr) into the DESIGN §4.3 taxonomy,
/// and computes retry backoff. Pure and deterministic so it can be unit-tested against
/// canned stderr samples.
/// </summary>
public static partial class RetryPolicy
{
    // Order matters: throttling and permanence are checked before the transient fallback.
    [GeneratedRegex(@"sign in to confirm|not a bot|http error 429|\b429\b|too many requests|rate.?limit",
        RegexOptions.IgnoreCase)]
    private static partial Regex Throttle();

    [GeneratedRegex(
        @"private video|video is private|this video is unavailable|video unavailable|has been removed|" +
        @"account.*(has been )?terminated|no longer available|members[- ]only|join this channel|" +
        @"not available in your country|blocked it in your country|geo|copyright grounds|" +
        @"who has blocked it|removed by the uploader|video has been removed|deleted video|" +
        @"unavailable.*country|is not available|inappropriate|age.?restrict",
        RegexOptions.IgnoreCase)]
    private static partial Regex Permanent();

    [GeneratedRegex(@"requested format( is)? not available|no video formats found|no formats found",
        RegexOptions.IgnoreCase)]
    private static partial Regex FormatUnavailable();

    /// <summary>
    /// True when the run failed because the <c>-f</c> selector matched nothing. The caller retries once
    /// with <see cref="YtDlp.FallbackFormat"/> (grab-anything) and lets the postprocess transcode fix it.
    /// </summary>
    public static bool IsFormatUnavailable(string? stderr) =>
        !string.IsNullOrEmpty(stderr) && FormatUnavailable().IsMatch(stderr);

    public static Failure Classify(int exitCode, string? stderr)
    {
        var text = stderr ?? "";
        if (Throttle().IsMatch(text))
            return new(ErrorKind.Throttled, FirstError(text) ?? "Throttled by YouTube (429 / bot check)");
        if (Permanent().IsMatch(text))
            return new(ErrorKind.Permanent, FirstError(text) ?? "Video permanently unavailable");
        return new(ErrorKind.Transient, FirstError(text) ?? $"yt-dlp exited {exitCode}");
    }

    /// <summary>Backoff for a transient retry: min(2^attempts, 60) minutes, ±25% jitter.</summary>
    public static TimeSpan Backoff(int attempts, double jitter01)
    {
        var minutes = Math.Min(Math.Pow(2, Math.Max(1, attempts)), 60);
        var jittered = minutes * (0.75 + 0.5 * Math.Clamp(jitter01, 0, 1));
        return TimeSpan.FromMinutes(jittered);
    }

    /// <summary>Pull the last "ERROR:"-prefixed line for the UI, else the last non-empty line.</summary>
    private static string? FirstError(string stderr)
    {
        string? lastError = null, lastLine = null;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            lastLine = line;
            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                lastError = line;
        }
        return lastError ?? lastLine;
    }
}
