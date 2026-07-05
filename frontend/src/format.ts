// Small display helpers (the server already formats live speed/eta; these are for static fields).

export function pct(v: number): string {
  return Math.round(Math.max(0, Math.min(1, v)) * 100) + '%'
}

export function relTime(unix: number | null): string {
  if (!unix) return ''
  const diff = Date.now() / 1000 - unix
  if (diff < 60) return 'just now'
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`
  return `${Math.floor(diff / 86400)}d ago`
}

const stateLabels: Record<string, string> = {
  queued: 'Queued',
  fetching_meta: 'Fetching info',
  downloading: 'Downloading',
  converting: 'Converting',
  indexing: 'Indexing',
  done: 'Done',
  failed: 'Failed',
  paused: 'Paused',
}

export function stateLabel(state: string): string {
  return stateLabels[state] ?? state
}

// Seconds → "h:mm:ss" (or "m:ss" under an hour).
export function duration(s: number): string {
  s = Math.max(0, Math.round(s))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = s % 60
  const mm = h > 0 ? String(m).padStart(2, '0') : String(m)
  return (h > 0 ? `${h}:` : '') + `${mm}:${String(sec).padStart(2, '0')}`
}

// Bytes → "12.3 GB" (binary units, compact).
export function bytes(n: number | null | undefined): string {
  if (n == null) return '—'
  const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']
  let i = 0
  let v = n
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++ }
  return `${v.toFixed(v < 10 && i > 0 ? 1 : 0)} ${u[i]}`
}

// ISO timestamp → "5 Jul 2026" (locale-friendly, compact).
export function shortDate(iso: string): string {
  const d = new Date(iso)
  if (isNaN(d.getTime())) return iso
  return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })
}
