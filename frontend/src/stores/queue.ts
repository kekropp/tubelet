import { defineStore } from 'pinia'
import { api } from '../api'
import type { Job, JobProgress, QueueStats, QueueBulkRequest } from '../types'

// Live per-job progress (from SignalR job.progress) — kept separate from the row's coarse
// persisted progress so the bar animates smoothly without re-fetching.
export interface Live { pct: number; speed: string | null; eta: string | null }

interface Banner { kind: string; message: string }
interface Scan { target: string; found: number; enqueued: number; done: boolean; message: string | null }

export const useQueue = defineStore('queue', {
  state: () => ({
    active: [] as Job[],
    recent: [] as Job[],
    failed: [] as Job[],
    stats: { queued: 0, running: 0, failed: 0, done: 0 } as QueueStats,
    live: {} as Record<number, Live>,
    banner: null as Banner | null,
    scan: null as Scan | null,
    connected: false,
    loaded: false,
    paused: false,
    // Bumped whenever queue membership changes (job state transition / bulk op) so paginated
    // views can re-fetch the current page without subscribing to every individual row.
    rev: 0,
  }),

  getters: {
    // Downloading jobs drive the tab-title indicator.
    downloading: (s): Job[] => s.active.filter(j => j.state === 'downloading'),
    aggregatePct(): number {
      const d = this.downloading
      if (d.length === 0) return 0
      const sum = d.reduce((a, j) => a + (this.live[j.id]?.pct ?? j.progress), 0)
      return sum / d.length
    },
  },

  actions: {
    async refresh() {
      const q = await api.queue()
      this.active = q.active
      this.recent = q.recent
      this.failed = q.failed
      this.stats = q.stats
      this.paused = q.paused
      this.loaded = true
    },

    applyStats(s: QueueStats) { this.stats = s },
    setPaused(p: boolean) { this.paused = p },
    invalidate() { this.rev++; void this.refresh() },
    setBanner(b: Banner | null) { this.banner = b },
    setScan(s: Scan) {
      this.scan = s.done && s.enqueued === 0 && !s.message ? null : s
      // A finished scan/expansion just enqueued jobs that arrived via no per-row event — pull them in.
      if (s.done && s.enqueued > 0) this.invalidate()
    },

    applyProgress(p: JobProgress) {
      this.live[p.id] = { pct: p.pct, speed: p.speed, eta: p.eta }
      const j = this.active.find(x => x.id === p.id)
      if (j) j.progress = p.pct
    },

    // A job.state event moves a job to its correct bucket (dedup across all three).
    applyJobState(job: Job) {
      this.remove(job.id)
      if (job.state === 'done') this.recent.unshift(job)
      else if (job.state === 'failed') this.failed.unshift(job)
      else this.active.push(job)
      if (job.state === 'done' || job.state === 'failed') delete this.live[job.id]
      this.sortActive()
      this.rev++
    },

    remove(id: number) {
      this.active = this.active.filter(j => j.id !== id)
      this.recent = this.recent.filter(j => j.id !== id)
      this.failed = this.failed.filter(j => j.id !== id)
    },

    sortActive() {
      const rank = (s: string) => (s === 'queued' || s === 'paused' ? 1 : 0)
      this.active.sort((a, b) => rank(a.state) - rank(b.state) || a.priority - b.priority || a.added_at - b.added_at)
    },

    async retry(id: number) { await api.retry(id); await this.refresh() },
    async cancel(id: number) { await api.cancel(id); this.remove(id) },
    async priority(id: number, p: number) { await api.priority(id, p) },

    // ---- global / bulk queue management -----------------------------------
    async pauseAll() { await api.queuePause(); this.paused = true },
    async resumeAll() { await api.queueResume(); this.paused = false },
    async bulk(body: QueueBulkRequest) {
      await api.queueBulk(body)
      await this.refresh() // membership changed broadly; resync the dashboard buckets
    },
  },
})
