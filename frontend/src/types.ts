// Wire shapes from the backend (snake_case — matches the REST API and the SignalR payloads).

export type JobState =
  | 'queued' | 'fetching_meta' | 'downloading' | 'converting' | 'indexing'
  | 'done' | 'failed' | 'paused'

export interface Job {
  id: number
  youtube_id: string
  channel_id: string | null
  title: string | null
  state: JobState
  priority: number
  progress: number
  attempts: number
  max_attempts: number
  last_error: string | null
  error_kind: string | null
  added_at: number
  started_at: number | null
  finished_at: number | null
  next_retry: number | null
  thumb: string | null
}

export interface QueueDoc {
  active: Job[]
  recent: Job[]
  failed: Job[]
  stats: QueueStats
  paused: boolean
}

export interface PagedJobs {
  items: Job[]
  total: number
  page: number
  page_size: number
}

export type QueueFilter = 'active' | 'queued' | 'running' | 'failed' | 'done' | 'all'
export type BulkAction = 'cancel' | 'pause' | 'resume' | 'retry' | 'priority'
export interface QueueBulkRequest {
  action: BulkAction
  ids?: number[]
  scope?: 'queued' | 'active' | 'failed' | 'all'
  priority?: number
}

export interface QueueStats {
  queued: number
  running: number
  failed: number
  done: number
}

export interface IntakeResult {
  kind: 'video' | 'playlist' | 'channel' | 'unknown'
  id: string | null
  status: string
  enqueued: string[]
  skipped: string[]
}

// Backlog scope chosen after the metadata preview.
export type ScopeMode = 'all' | 'newest' | 'after' | 'none'
export interface ScopeInput {
  mode: ScopeMode
  n?: number       // for 'newest'
  after?: string   // for 'after' — "YYYY-MM-DD" or "YYYYMMDD"
}

export interface PreviewEntry { id: string; title: string | null; upload_date: string | null }
export interface PreviewResult {
  kind: 'video' | 'playlist' | 'channel' | 'unknown'
  id: string | null
  title: string | null
  channel_id: string | null
  channel_name: string | null
  video_count: number
  has_dates: boolean
  sample: PreviewEntry[]
}

// Live progress carried by the SignalR job.progress event.
export interface JobProgress {
  id: number
  youtube_id: string
  pct: number
  speed: string | null
  eta: string | null
}

// ---- library / channels (phase 4) --------------------------------------

export interface Chapter { title: string; start_s: number }
export interface Segment { type: string; start_s: number; end_s: number }

export interface Video {
  id: string
  channel_id: string
  title: string
  description: string
  published: string
  duration_s: number
  tags: string[]
  thumb: string | null
  chapters: Chapter[]
  segments: Segment[]
}

export interface PagedVideos {
  items: Video[]
  total: number
  page: number
  page_size: number
}

export interface ChannelSummary {
  id: string
  name: string
  thumb: string | null
  video_count: number
}

export interface Subscription {
  id: number
  kind: 'channel' | 'playlist'
  target_id: string
  cron: string
  quality_prof: string
  filter_json: string | null
  enabled: boolean
  last_check: number | null
  next_check: number | null
}

export interface SubscriptionInput {
  kind?: 'channel' | 'playlist'
  target_id?: string
  cron?: string
  quality_prof?: string
  filter_json?: string | null
  enabled?: boolean
}

export interface PlaylistSummary {
  id: string
  name: string
  description: string
  type: 'regular' | 'custom'
  active: boolean
  count: number
  thumb: string | null
}

// ---- settings / system (phase 5) ---------------------------------------

export interface Disk { free_bytes: number; total_bytes: number }

export interface SystemInfo {
  version: string
  ytdlp_version: string | null
  media: Disk | null
  cache: Disk | null
  queue: { queued: number; running: number; failed: number; done: number }
  cooldown_until: number | null
  video_count: number
  channel_count: number
  paused: boolean
}

// Settings sections are stored as free-form JSON blobs; these mirror the backend option records.
export interface NetworkSettings {
  ops_per_hour?: number
  download_workers?: number
  concurrent_fragments?: number
  sleep_requests?: number
  sleep_interval?: number
  max_sleep_interval?: number
  limit_rate?: string | null
}

export interface QualitySettings {
  profile?: string
  hwaccel?: string
  embed_subs?: boolean
  embed_thumbnail?: boolean
  sub_langs?: string
}

export interface SbSettings {
  categories?: string[]
  mapping?: Record<string, string>
}

export interface MaintenanceSettings {
  sb_refresh_cron?: string
  ytdlp_update_cron?: string
  janitor_cron?: string
  part_ttl_days?: number
  backup_enabled?: boolean
  backup_keep?: number
  ytdlp_autoupdate?: boolean
  po_token_enabled?: boolean
}

export interface CookieStatus {
  present: boolean
  valid: boolean
  identity: string | null
  uploaded_at: number | null
  validated_at: number | null
  message: string | null
}

export interface YtdlpUpdateResult { ok: boolean; version: string | null; error: string | null }
export interface BackupResult { ok: boolean; file: string | null; bytes: number | null; error: string | null }
