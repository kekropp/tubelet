using Microsoft.AspNetCore.SignalR;
using Tubelet.Api;
using Tubelet.Contracts;

namespace Tubelet.Realtime;

/// <summary>Typed facade over the SignalR hub — the only place event names are spelled.</summary>
public sealed class Broadcaster(IHubContext<EventsHub> hub)
{
    public Task JobProgress(long id, string youtubeId, double pct, string? speed, string? eta) =>
        hub.Clients.All.SendAsync("job.progress", new { id, youtubeId, pct, speed, eta });

    public Task JobState(JobDoc job) =>
        hub.Clients.All.SendAsync("job.state", job);

    public Task VideoAdded(VideoDoc video) =>
        hub.Clients.All.SendAsync("video.added", video);

    public Task ChannelAdded(ChannelDoc channel) =>
        hub.Clients.All.SendAsync("channel.added", channel);

    public Task QueueStats(QueueStatsDoc stats) =>
        hub.Clients.All.SendAsync("queue.stats", stats);

    public Task SystemBanner(string kind, string message) =>
        hub.Clients.All.SendAsync("system.banner", new { kind, message });

    public Task ScanProgress(string target, int found, int enqueued, bool done, string? message = null) =>
        hub.Clients.All.SendAsync("scan.progress", new { target, found, enqueued, done, message });
}
