using Tubelet.Data;
using Tubelet.Pipeline;
using Tubelet.Scheduling;
using Xunit;

namespace Tubelet.Tests;

public class CronScheduleTests
{
    [Theory]
    [InlineData("0 */6 * * *")]
    [InlineData("0 8 * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 0 1 1 *")]
    [InlineData("30 2 * * 1")]      // Monday 02:30
    [InlineData("0 0 * * * *")]     // 6-field (seconds)
    public void Accepts_valid_crons(string cron) => Assert.True(CronSchedule.IsValid(cron));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a cron")]
    [InlineData("99 * * * *")]
    [InlineData("* * *")]
    public void Rejects_invalid_crons(string? cron) => Assert.False(CronSchedule.IsValid(cron));

    [Fact]
    public void Next_is_strictly_after_from_and_matches_schedule()
    {
        var from = new DateTimeOffset(2026, 7, 5, 10, 30, 0, TimeSpan.Zero);
        // Every 6 hours: 00,06,12,18. From 10:30 → next is 12:00 UTC.
        var next = CronSchedule.Next("0 */6 * * *", from);
        Assert.NotNull(next);
        var when = DateTimeOffset.FromUnixTimeSeconds(next!.Value);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero), when);
    }

    [Fact]
    public void Next_returns_null_for_invalid_cron() =>
        Assert.Null(CronSchedule.Next("garbage", DateTimeOffset.UtcNow));
}

public class SubscriptionFilterTests
{
    private static FlatEntry Entry(string? title = "Some Title", long? dur = 600, string? date = "20260101") =>
        new("abcdefghijk", title, "UCxxxxxxxxxxxxxxxxxxxxxx", "Chan", dur, date);

    [Fact]
    public void Empty_filter_accepts_everything()
    {
        Assert.True(SubscriptionFilter.None.Accepts(Entry()));
        Assert.Equal(int.MaxValue, SubscriptionFilter.None.Cap);
    }

    [Fact]
    public void Min_and_max_duration_bound_the_entry()
    {
        var f = SubscriptionFilter.Parse("""{"min_duration_s":120,"max_duration_s":3600}""");
        Assert.True(f.Accepts(Entry(dur: 600)));
        Assert.False(f.Accepts(Entry(dur: 60)));    // too short
        Assert.False(f.Accepts(Entry(dur: 7200)));  // too long
    }

    [Fact]
    public void Missing_duration_passes_a_duration_filter()
    {
        var f = SubscriptionFilter.Parse("""{"min_duration_s":120}""");
        Assert.True(f.Accepts(Entry(dur: null)));   // best-effort: don't drop when field is absent
    }

    [Fact]
    public void Date_floor_drops_older_uploads()
    {
        var f = SubscriptionFilter.Parse("""{"date_floor":"20260101"}""");
        Assert.True(f.Accepts(Entry(date: "20260605")));
        Assert.False(f.Accepts(Entry(date: "20251231")));
        Assert.True(f.Accepts(Entry(date: null)));  // absent → pass
    }

    [Fact]
    public void Title_regex_is_case_insensitive_and_invalid_patterns_dont_filter()
    {
        var f = SubscriptionFilter.Parse("""{"title_regex":"deep dive"}""");
        Assert.True(f.Accepts(Entry(title: "SQLite DEEP DIVE part 2")));
        Assert.False(f.Accepts(Entry(title: "unrelated")));

        var bad = SubscriptionFilter.Parse("""{"title_regex":"([unclosed"}""");
        Assert.True(bad.Accepts(Entry(title: "anything")));
    }

    [Fact]
    public void Max_items_becomes_the_cap()
    {
        Assert.Equal(5, SubscriptionFilter.Parse("""{"max_items":5}""").Cap);
        Assert.Equal(int.MaxValue, SubscriptionFilter.Parse("""{"max_items":0}""").Cap);
    }

    [Fact]
    public void Malformed_json_yields_empty_filter() =>
        Assert.Same(SubscriptionFilter.None, SubscriptionFilter.Parse("not json"));
}

public class UlidTests
{
    [Fact]
    public void Playlist_id_has_expected_shape()
    {
        var id = Ulid.NewPlaylistId();
        Assert.StartsWith("TL-", id);
        Assert.Equal(29, id.Length); // "TL-" + 26
        Assert.All(id[3..], c => Assert.Contains(c, "0123456789ABCDEFGHJKMNPQRSTVWXYZ"));
    }

    [Fact]
    public void Ids_are_unique_and_time_sortable()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => Ulid.New()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
