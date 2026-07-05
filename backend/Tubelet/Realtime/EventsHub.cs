using Microsoft.AspNetCore.SignalR;

namespace Tubelet.Realtime;

/// <summary>
/// Server→client only. All mutations go through REST; clients re-GET /api/v1/queue
/// on reconnect, so a missed transient event is never fatal.
/// </summary>
public sealed class EventsHub : Hub;
