<script setup lang="ts">
import { ref, computed } from 'vue'
import { classify, chipLabel } from '../classify'
import { api } from '../api'
import { useQueue } from '../stores/queue'
import AddDialog from './AddDialog.vue'

const queue = useQueue()
const url = ref('')
const busy = ref(false)
const flash = ref<{ ok: boolean; text: string } | null>(null)
const dialogUrl = ref<string | null>(null)

const cls = computed(() => classify(url.value))
const chip = computed(() => chipLabel(cls.value))

async function submit() {
  const value = url.value.trim()
  if (!value || busy.value) return
  flash.value = null
  // Channels/playlists open the metadata-first chooser instead of downloading everything blindly.
  if (cls.value.kind === 'channel' || cls.value.kind === 'playlist') {
    dialogUrl.value = value
    return
  }
  busy.value = true
  try {
    const r = await api.intake(value)
    flash.value = { ok: r.status === 'enqueued' || r.status === 'expanding', text: describe(r.status, r.kind) }
    if (r.status === 'enqueued' || r.status === 'expanding') url.value = ''
    await queue.refresh()
  } catch (e) {
    flash.value = { ok: false, text: 'Request failed: ' + (e as Error).message }
  } finally {
    busy.value = false
  }
}

async function onDialogDone(msg: string) {
  dialogUrl.value = null
  url.value = ''
  flash.value = { ok: true, text: msg }
  await queue.refresh()
}

function describe(status: string, kind: string): string {
  switch (status) {
    case 'enqueued': return 'Added to the queue.'
    case 'expanding': return `Expanding ${kind}… new videos will appear below.`
    case 'archived': return 'Already in your library.'
    case 'duplicate': return 'Already queued.'
    case 'ignored': return 'This video is on your ignore list.'
    case 'unrecognized': return "Couldn't recognize that — paste a YouTube URL or ID."
    default: return status
  }
}
</script>

<template>
  <div class="omnibox">
    <div class="input-row">
      <span v-if="chip" class="chip" :class="cls.kind">{{ chip }}</span>
      <input
        v-model="url"
        type="text"
        placeholder="Paste a YouTube URL, playlist, channel, or video ID…"
        autofocus
        spellcheck="false"
        @keydown.enter="submit"
      />
      <button :disabled="busy || !url.trim()" @click="submit">
        {{ busy ? '…' : 'Add' }}
      </button>
    </div>
    <p v-if="flash" class="flash" :class="{ ok: flash.ok, err: !flash.ok }">{{ flash.text }}</p>
    <p v-else class="hint">Everything you add downloads, converts, and lands in your Jellyfin library.</p>

    <AddDialog v-if="dialogUrl" :url="dialogUrl" @close="dialogUrl = null" @done="onDialogDone" />
  </div>
</template>

<style scoped>
.omnibox { margin-bottom: 2rem; }
.input-row {
  display: flex; align-items: center; gap: 0.5rem;
  background: var(--panel); border: 1px solid var(--border); border-radius: 12px;
  padding: 0.5rem 0.5rem 0.5rem 0.75rem;
}
.input-row:focus-within { border-color: var(--accent); }
input {
  flex: 1; background: transparent; border: 0; outline: none;
  color: var(--fg); font-size: 1.05rem; padding: 0.4rem 0;
}
button {
  background: var(--accent); color: #04121c; border: 0; border-radius: 8px;
  font-weight: 600; padding: 0.5rem 1.1rem; cursor: pointer; font-size: 0.95rem;
}
button:disabled { opacity: 0.4; cursor: default; }
.chip {
  font-size: 0.72rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em;
  padding: 0.2rem 0.5rem; border-radius: 6px; background: #1d3b4a; color: var(--accent);
}
.chip.channel { background: #2a2140; color: #b79cff; }
.chip.playlist { background: #123528; color: #6fe0a8; }
.flash { margin: 0.5rem 0.25rem 0; font-size: 0.9rem; }
.flash.ok { color: var(--accent); }
.flash.err { color: var(--danger); }
.hint { margin: 0.5rem 0.25rem 0; font-size: 0.9rem; color: var(--muted); }
</style>
