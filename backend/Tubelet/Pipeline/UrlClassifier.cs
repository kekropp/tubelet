using System.Text.RegularExpressions;

namespace Tubelet.Pipeline;

public enum UrlKind { Unknown, Video, Playlist, Channel }

/// <summary>
/// Classification of one omnibox paste. For videos, Id is the 11-char id.
/// For playlists, Id is the PL…/UU…/OL… list id. For channels, Id is either a
/// concrete "UC…" id or a "@handle" / "c/name" / "user/name" reference that
/// yt-dlp resolves at expansion time. PlaylistId rides along when a watch URL
/// also carries &list=.
/// </summary>
public sealed record Classification(UrlKind Kind, string? Id, string? PlaylistId = null);

public static partial class UrlClassifier
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex BareVideoId();

    [GeneratedRegex(@"(?:youtube\.com/(?:watch|shorts/|live/|embed/|v/)|youtu\.be/)", RegexOptions.IgnoreCase)]
    private static partial Regex VideoUrl();

    [GeneratedRegex(@"(?:youtu\.be/|/(?:shorts|live|embed|v)/)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex PathVideoId();

    [GeneratedRegex(@"[?&]v=([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex QueryVideoId();

    [GeneratedRegex(@"[?&]list=([A-Za-z0-9_-]{10,})", RegexOptions.IgnoreCase)]
    private static partial Regex ListParam();

    [GeneratedRegex(@"youtube\.com/channel/(UC[A-Za-z0-9_-]{22})", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelId();

    [GeneratedRegex(@"youtube\.com/(@[A-Za-z0-9_.\-]+|c/[^/?#\s]+|user/[^/?#\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelRef();

    [GeneratedRegex(@"^UC[A-Za-z0-9_-]{22}$")]
    private static partial Regex BareChannelId();

    public static Classification Classify(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return new(UrlKind.Unknown, null);

        // bare ids / handles (no URL)
        if (BareChannelId().IsMatch(input)) return new(UrlKind.Channel, input);
        if (input.StartsWith('@') && !input.Contains('/')) return new(UrlKind.Channel, input);
        if (BareVideoId().IsMatch(input)) return new(UrlKind.Video, input);

        var list = ListParam().Match(input);

        // video URLs (watch/shorts/live/embed/youtu.be) — may carry a list too
        if (VideoUrl().IsMatch(input))
        {
            var m = QueryVideoId().Match(input);
            if (!m.Success) m = PathVideoId().Match(input);
            if (m.Success)
                return new(UrlKind.Video, m.Groups[1].Value, list.Success ? list.Groups[1].Value : null);
            // /watch without a v= but with a list= is a playlist view
            if (list.Success) return new(UrlKind.Playlist, list.Groups[1].Value);
            return new(UrlKind.Unknown, null);
        }

        if (list.Success) return new(UrlKind.Playlist, list.Groups[1].Value);

        var ch = ChannelId().Match(input);
        if (ch.Success) return new(UrlKind.Channel, ch.Groups[1].Value);
        var chRef = ChannelRef().Match(input);
        if (chRef.Success) return new(UrlKind.Channel, chRef.Groups[1].Value);

        return new(UrlKind.Unknown, null);
    }
}
