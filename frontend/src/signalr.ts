import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import type { useQueue } from './stores/queue'
import type { Job, JobProgress, QueueStats } from './types'

// Wires the hub to the queue store. Server→client only; on (re)connect we re-GET the queue,
// so a missed transient event is never fatal (DESIGN §7).
export function connectHub(queue: ReturnType<typeof useQueue>) {
  const conn = new HubConnectionBuilder()
    .withUrl('/hub')
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
    .configureLogging(LogLevel.Warning)
    .build()

  conn.on('job.progress', (p: JobProgress) => queue.applyProgress(p))
  conn.on('job.state', (j: Job) => queue.applyJobState(j))
  conn.on('queue.stats', (s: QueueStats) => queue.applyStats(s))
  conn.on('queue.paused', (p: { paused: boolean }) => queue.setPaused(p.paused))
  conn.on('queue.invalidated', () => queue.invalidate())
  conn.on('video.added', () => { /* Home cares about the queue; Library (later) refetches. */ })
  conn.on('channel.added', () => {})
  conn.on('system.banner', (b: { kind: string; message: string }) => queue.setBanner(b))
  conn.on('scan.progress', (s: {
    target: string; found: number; enqueued: number; done: boolean; message: string | null
  }) => queue.setScan(s))

  conn.onreconnected(() => { queue.connected = true; void queue.refresh() })
  conn.onreconnecting(() => { queue.connected = false })
  conn.onclose(() => { queue.connected = false })

  const start = async () => {
    try {
      await conn.start()
      queue.connected = true
      await queue.refresh()
    } catch {
      queue.connected = false
      setTimeout(start, 2000) // retry initial connection
    }
  }
  void start()

  return {
    stop: () => { if (conn.state !== HubConnectionState.Disconnected) void conn.stop() },
  }
}
