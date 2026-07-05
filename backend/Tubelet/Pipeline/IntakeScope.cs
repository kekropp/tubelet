using System.Globalization;

namespace Tubelet.Pipeline;

public enum ScopeMode { All, Newest, After, None }

/// <summary>
/// How much of a channel/playlist listing to actually enqueue when adding it. Chosen by the user
/// after the metadata preview: grab everything, only the newest N, everything uploaded on/after a
/// date, or nothing at all (subscribe-only — future uploads arrive via the scheduler).
/// <para>
/// <see cref="Slice"/> assumes the listing is newest-first (yt-dlp's channel/playlist order). For
/// <see cref="ScopeMode.After"/> it walks newest→oldest and stops at the first entry dated below the
/// floor; undated entries above that point are kept (flat-playlist upload dates are extractor-dependent
/// and often absent, so this degrades gracefully — same "don't silently drop" spirit as
/// <see cref="Tubelet.Scheduling.SubscriptionFilter"/>).
/// </para>
/// </summary>
public sealed record IntakeScope(ScopeMode Mode, int N, string? DateFloor)
{
    public static readonly IntakeScope All = new(ScopeMode.All, 0, null);

    /// <summary>Normalize a wire request into a scope. Unknown/blank mode → All; malformed values fall back safely.</summary>
    public static IntakeScope From(string? mode, int? n, string? after)
    {
        switch ((mode ?? "").Trim().ToLowerInvariant())
        {
            case "none": return new(ScopeMode.None, 0, null);
            case "newest":
                var count = n is > 0 ? n.Value : 0;
                return count > 0 ? new(ScopeMode.Newest, count, null) : All;
            case "after":
                var floor = NormalizeDate(after);
                return floor is null ? All : new(ScopeMode.After, 0, floor);
            case "all":
            default:
                return All;
        }
    }

    /// <summary>Accepts "YYYYMMDD" or "YYYY-MM-DD" (any separators); returns yt-dlp's "YYYYMMDD" form, or null if not 8 digits.</summary>
    public static string? NormalizeDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return digits.Length == 8 ? digits : null;
    }

    /// <summary>Today's date in yt-dlp's "YYYYMMDD" form (UTC) — the floor for "subscribe, only new from now on".</summary>
    public static string Today() => DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>The subset of a newest-first listing this scope wants enqueued.</summary>
    public IEnumerable<FlatEntry> Slice(IReadOnlyList<FlatEntry> entries)
    {
        switch (Mode)
        {
            case ScopeMode.None:
                yield break;
            case ScopeMode.Newest:
                foreach (var e in entries.Take(N)) yield return e;
                yield break;
            case ScopeMode.After:
                foreach (var e in entries)
                {
                    if (e.UploadDate is { Length: 8 } up && string.CompareOrdinal(up, DateFloor) < 0)
                        yield break; // reached the pre-floor tail; everything older follows
                    yield return e;
                }
                yield break;
            case ScopeMode.All:
            default:
                foreach (var e in entries) yield return e;
                yield break;
        }
    }
}
