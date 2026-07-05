using System.Globalization;
using System.Text.Json;
using Dapper;
using Tubelet.Api;
using Tubelet.Data;
using Tubelet.Domain;
using Tubelet.Realtime;

namespace Tubelet.Sponsorblock;

/// <summary>
/// Weekly SponsorBlock refresh (DESIGN §5). Segments accrete after upload, so we re-fetch for videos
/// newer than 30 days. When the segment set actually changes we bump <c>changed_at</c> (the plugin's
/// next delta poll then triggers Jellyfin's segment update); <c>sb_refreshed</c> is always stamped.
/// </summary>
public sealed class SbRefresher(Database db, SbClient sb, Broadcaster bc, ILogger<SbRefresher> log)
{
    /// <summary>Only videos published within this window are worth re-checking.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>Re-fetch SB for recent videos. Returns the number whose segments changed.</summary>
    public async Task<int> RefreshRecentAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(Window).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        List<VideoRow> recent;
        IReadOnlySet<string> categories;
        Dictionary<string, string> mapping;
        using (var conn = db.Open())
        {
            recent = conn.Query<VideoRow>(
                $"SELECT {Queries.VideoColumns} FROM videos WHERE published >= @cutoff", new { cutoff }).ToList();
            categories = SbMapping.LoadCategories(conn);
            mapping = SbMapping.Load(conn);
        }

        var changed = 0;
        foreach (var v in recent)
        {
            ct.ThrowIfCancellationRequested();
            var fresh = await sb.FetchAsync(v.YoutubeId, categories, v.DurationS, ct).ConfigureAwait(false);
            var freshJson = fresh.Length > 0 ? JsonSerializer.Serialize(fresh, ApiJsonContext.Default.SbSegmentArray) : null;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var segmentsChanged = !string.Equals(freshJson, v.Segments, StringComparison.Ordinal);

            using var conn = db.Open();
            if (segmentsChanged)
            {
                using var tx = conn.BeginTransaction();
                var seq = Database.NextSeq(conn, tx);
                conn.Execute(
                    "UPDATE videos SET segments = @seg, sb_refreshed = @now, changed_at = @seq WHERE youtube_id = @id",
                    new { seg = freshJson, now, seq, id = v.YoutubeId }, tx);
                tx.Commit();
                v.Segments = freshJson;
                await bc.VideoAdded(Mapping.ToDoc(v, mapping)).ConfigureAwait(false);
                changed++;
            }
            else
            {
                conn.Execute("UPDATE videos SET sb_refreshed = @now WHERE youtube_id = @id",
                    new { now, id = v.YoutubeId });
            }
        }

        if (changed > 0) log.LogInformation("SponsorBlock refresh: {Changed}/{Total} videos updated", changed, recent.Count);
        return changed;
    }
}
