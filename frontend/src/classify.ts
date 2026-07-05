// Mirror of the backend UrlClassifier (Pipeline/UrlClassifier.cs) for instant omnibox feedback.
// The server re-classifies authoritatively on intake; this only drives the chip.

export type UrlKind = 'video' | 'playlist' | 'channel' | 'unknown'
export interface Classification { kind: UrlKind; id: string | null }

const bareVideo = /^[A-Za-z0-9_-]{11}$/
const bareChannel = /^UC[A-Za-z0-9_-]{22}$/
const videoUrl = /(?:youtube\.com\/(?:watch|shorts\/|live\/|embed\/|v\/)|youtu\.be\/)/i
const pathVideoId = /(?:youtu\.be\/|\/(?:shorts|live|embed|v)\/)([A-Za-z0-9_-]{11})/i
const queryVideoId = /[?&]v=([A-Za-z0-9_-]{11})/i
const listParam = /[?&]list=([A-Za-z0-9_-]{10,})/i
const channelId = /youtube\.com\/channel\/(UC[A-Za-z0-9_-]{22})/i
const channelRef = /youtube\.com\/(@[A-Za-z0-9_.\-]+|c\/[^/?#\s]+|user\/[^/?#\s]+)/i

export function classify(raw: string): Classification {
  const input = raw.trim()
  if (input.length === 0) return { kind: 'unknown', id: null }

  if (bareChannel.test(input)) return { kind: 'channel', id: input }
  if (input.startsWith('@') && !input.includes('/')) return { kind: 'channel', id: input }
  if (bareVideo.test(input)) return { kind: 'video', id: input }

  const list = listParam.exec(input)

  if (videoUrl.test(input)) {
    const m = queryVideoId.exec(input) ?? pathVideoId.exec(input)
    if (m) return { kind: 'video', id: m[1] }
    if (list) return { kind: 'playlist', id: list[1] }
    return { kind: 'unknown', id: null }
  }

  if (list) return { kind: 'playlist', id: list[1] }

  const ch = channelId.exec(input)
  if (ch) return { kind: 'channel', id: ch[1] }
  const cr = channelRef.exec(input)
  if (cr) return { kind: 'channel', id: cr[1] }

  return { kind: 'unknown', id: null }
}

export function chipLabel(c: Classification): string {
  switch (c.kind) {
    case 'video': return 'Video'
    case 'playlist': return 'Playlist'
    case 'channel': return 'Channel'
    default: return ''
  }
}
