import type {
  IntakeResult, QueueDoc, PagedVideos, Video, ChannelSummary,
  Subscription, SubscriptionInput, PlaylistSummary,
  SystemInfo, CookieStatus, YtdlpUpdateResult, BackupResult,
} from './types'

async function json<T>(resp: Response): Promise<T> {
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}`)
  return resp.json() as Promise<T>
}

function post(url: string, body?: unknown): Promise<Response> {
  return fetch(url, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
}

function send(method: string, url: string, body?: unknown): Promise<Response> {
  return fetch(url, {
    method,
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
}

export const api = {
  async intake(url: string): Promise<IntakeResult> {
    const resp = await post('/api/v1/intake', { url })
    // 422 for unrecognized input still carries an IntakeResult body.
    if (resp.status === 422) return resp.json()
    return json<IntakeResult>(resp)
  },

  queue: (): Promise<QueueDoc> => fetch('/api/v1/queue').then(json<QueueDoc>),

  retry: (id: number) => post(`/api/v1/queue/${id}/retry`),
  pause: (id: number) => post(`/api/v1/queue/${id}/pause`),
  cancel: (id: number) => post(`/api/v1/queue/${id}/cancel`),
  priority: (id: number, priority: number) => post(`/api/v1/queue/${id}/priority`, { priority }),

  // ---- library ----------------------------------------------------------
  videos(params: {
    query?: string; channel?: string; sort?: string; page?: number; page_size?: number
  }): Promise<PagedVideos> {
    const q = new URLSearchParams()
    if (params.query) q.set('query', params.query)
    if (params.channel) q.set('channel', params.channel)
    if (params.sort) q.set('sort', params.sort)
    if (params.page) q.set('page', String(params.page))
    if (params.page_size) q.set('page_size', String(params.page_size))
    return fetch(`/api/v1/videos?${q}`).then(json<PagedVideos>)
  },
  video: (id: string): Promise<Video> => fetch(`/api/v1/videos/${id}`).then(json<Video>),
  deleteVideo: (id: string, alsoIgnore = false) =>
    send('DELETE', `/api/v1/videos/${id}${alsoIgnore ? '?also_ignore=1' : ''}`),
  redownload: (id: string) => post('/api/v1/intake', { url: `https://youtu.be/${id}` }),

  channels: (): Promise<ChannelSummary[]> => fetch('/api/v1/channels').then(json<ChannelSummary[]>),

  // ---- subscriptions ----------------------------------------------------
  subscriptions: (): Promise<Subscription[]> =>
    fetch('/api/v1/subscriptions').then(json<Subscription[]>),
  createSubscription: (s: SubscriptionInput): Promise<Response> => post('/api/v1/subscriptions', s),
  updateSubscription: (id: number, s: SubscriptionInput): Promise<Subscription> =>
    send('PATCH', `/api/v1/subscriptions/${id}`, s).then(json<Subscription>),
  deleteSubscription: (id: number) => send('DELETE', `/api/v1/subscriptions/${id}`),
  scanSubscription: (id: number) => post(`/api/v1/subscriptions/${id}/scan`),
  backlogSubscription: (id: number) => post(`/api/v1/subscriptions/${id}/backlog`),

  // ---- playlists --------------------------------------------------------
  playlists: (): Promise<PlaylistSummary[]> => fetch('/api/v1/playlists').then(json<PlaylistSummary[]>),

  // ---- system / settings (phase 5) --------------------------------------
  system: (): Promise<SystemInfo> => fetch('/api/v1/system').then(json<SystemInfo>),

  getSettings: <T>(section: string): Promise<T> => fetch(`/api/v1/settings/${section}`).then(json<T>),
  putSettings: (section: string, body: unknown): Promise<Response> =>
    send('PUT', `/api/v1/settings/${section}`, body),

  // Cookies — the jar is write-only; only status is ever read back.
  cookies: (): Promise<CookieStatus> => fetch('/api/v1/cookies').then(json<CookieStatus>),
  uploadCookies: (text: string): Promise<CookieStatus> =>
    fetch('/api/v1/cookies', { method: 'POST', headers: { 'content-type': 'text/plain' }, body: text })
      .then(json<CookieStatus>),
  validateCookies: (): Promise<CookieStatus> => post('/api/v1/cookies/validate').then(json<CookieStatus>),
  deleteCookies: () => send('DELETE', '/api/v1/cookies'),

  // Maintenance actions
  updateYtdlp: (): Promise<YtdlpUpdateResult> => post('/api/v1/system/ytdlp/update').then(json<YtdlpUpdateResult>),
  backupNow: (): Promise<BackupResult> => post('/api/v1/system/backup').then(json<BackupResult>),
}
