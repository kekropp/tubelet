<script setup lang="ts">
import { ref, computed } from 'vue'
import type { Video } from '../types'
import { api } from '../api'
import { duration, shortDate } from '../format'

const props = defineProps<{ video: Video }>()
const emit = defineEmits<{ close: []; deleted: [id: string] }>()

const busy = ref(false)
const redownloaded = ref(false)

// Media is served from the youtube root at <channel_id>/<id>.mp4 (range requests → seekable preview).
const mediaUrl = computed(() => `/media/${props.video.channel_id}/${props.video.id}.mp4`)

// Colour segments by Jellyfin MediaSegmentType (already mapped server-side).
const segColor: Record<string, string> = {
  Intro: '#5b8def', Outro: '#8d6ef0', Recap: '#f0b24a',
  Preview: '#4ad0c0', Commercial: '#f0603a',
}
function pctOf(s: number) { return props.video.duration_s > 0 ? (s / props.video.duration_s) * 100 : 0 }

async function redownload() {
  busy.value = true
  try { await api.redownload(props.video.id); redownloaded.value = true }
  finally { busy.value = false }
}

async function remove(alsoIgnore: boolean) {
  const msg = alsoIgnore
    ? `Delete "${props.video.title}" and never re-download it?`
    : `Delete "${props.video.title}" from the library? (the file is removed)`
  if (!confirm(msg)) return
  busy.value = true
  try {
    const r = await api.deleteVideo(props.video.id, alsoIgnore)
    if (r.ok) emit('deleted', props.video.id)
  } finally { busy.value = false }
}
</script>

<template>
  <div class="overlay" @click.self="emit('close')" @keydown.esc="emit('close')" tabindex="-1">
    <div class="sheet" role="dialog" aria-modal="true">
      <button class="x" aria-label="Close" @click="emit('close')">✕</button>

      <video class="player" :src="mediaUrl" controls preload="metadata" :poster="video.thumb || undefined"></video>

      <h2>{{ video.title }}</h2>
      <div class="sub">
        <span>{{ shortDate(video.published) }}</span>
        <span>· {{ duration(video.duration_s) }}</span>
      </div>

      <div v-if="video.segments.length" class="timeline" :title="`${video.segments.length} segment(s)`">
        <div
          v-for="(s, i) in video.segments" :key="i" class="seg"
          :style="{ left: pctOf(s.start_s) + '%', width: (pctOf(s.end_s) - pctOf(s.start_s)) + '%',
                    background: segColor[s.type] || 'var(--muted)' }"
          :title="`${s.type}: ${duration(s.start_s)}–${duration(s.end_s)}`"></div>
      </div>

      <ul v-if="video.chapters.length" class="chapters">
        <li v-for="(c, i) in video.chapters" :key="i">
          <span class="t">{{ duration(c.start_s) }}</span> {{ c.title }}
        </li>
      </ul>

      <p v-if="video.description" class="desc">{{ video.description }}</p>

      <div v-if="video.tags.length" class="tags">
        <span v-for="t in video.tags" :key="t" class="tag">{{ t }}</span>
      </div>

      <div class="actions">
        <button :disabled="busy || redownloaded" @click="redownload">
          {{ redownloaded ? '✓ Re-queued' : 'Re-download' }}
        </button>
        <button class="danger" :disabled="busy" @click="remove(false)">Delete</button>
        <button class="danger" :disabled="busy" @click="remove(true)" title="Delete and never fetch again">
          Delete &amp; ignore
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay {
  position: fixed; inset: 0; background: rgba(0,0,0,0.66); backdrop-filter: blur(2px);
  display: flex; align-items: flex-start; justify-content: center; padding: 3rem 1rem; z-index: 50; overflow: auto;
}
.sheet {
  position: relative; width: 100%; max-width: 760px; background: var(--panel);
  border: 1px solid var(--border); border-radius: 14px; padding: 1.2rem 1.4rem 1.5rem;
}
.x { position: absolute; top: 0.7rem; right: 0.8rem; background: none; border: 0; color: var(--muted);
     font-size: 1rem; cursor: pointer; }
.x:hover { color: var(--fg); }
.player { width: 100%; aspect-ratio: 16 / 9; background: #000; border-radius: 8px; }
h2 { margin: 0.9rem 0 0.3rem; font-size: 1.2rem; }
.sub { color: var(--muted); font-size: 0.88rem; display: flex; gap: 0.4rem; }
.timeline {
  position: relative; height: 10px; background: #0c0c0f; border-radius: 5px; margin: 0.9rem 0; overflow: hidden;
}
.seg { position: absolute; top: 0; bottom: 0; min-width: 2px; opacity: 0.85; }
.chapters { list-style: none; padding: 0; margin: 0.6rem 0; max-height: 9rem; overflow: auto; font-size: 0.9rem; }
.chapters li { padding: 0.15rem 0; }
.chapters .t { color: var(--accent); font-variant-numeric: tabular-nums; margin-right: 0.5rem; }
.desc { white-space: pre-wrap; color: var(--muted); font-size: 0.9rem; max-height: 10rem; overflow: auto;
        margin: 0.8rem 0; }
.tags { display: flex; flex-wrap: wrap; gap: 0.35rem; margin-bottom: 0.6rem; }
.tag { background: #24303a; color: var(--muted); border-radius: 20px; padding: 0.1rem 0.55rem; font-size: 0.78rem; }
.actions { display: flex; gap: 0.5rem; margin-top: 1rem; flex-wrap: wrap; }
.actions button {
  background: #24303a; color: var(--fg); border: 0; border-radius: 7px; padding: 0.45rem 0.8rem; cursor: pointer;
}
.actions button:hover:not(:disabled) { background: #2e3d49; }
.actions button:disabled { opacity: 0.5; cursor: default; }
.actions button.danger:hover:not(:disabled) { background: #4a2020; color: #ffb0b0; }
</style>
