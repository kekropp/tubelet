<script setup lang="ts">
import { ref, reactive, onMounted, computed } from 'vue'
import type { Subscription, ChannelSummary, SubscriptionInput } from '../types'
import { api } from '../api'
import { relTime } from '../format'

const subs = ref<Subscription[]>([])
const channels = ref<ChannelSummary[]>([])
const editing = ref<number | null>(null)
const busyId = ref<number | null>(null)
const flash = reactive<Record<number, string>>({})

// Add form
const form = reactive({ target_id: '', kind: 'channel' as 'channel' | 'playlist', cron: '0 */6 * * *' })
const addError = ref('')
const adding = ref(false)

const CRONS = [
  { v: '0 */6 * * *', label: 'Every 6 hours' },
  { v: '0 */12 * * *', label: 'Every 12 hours' },
  { v: '0 8 * * *', label: 'Daily at 08:00' },
  { v: '0 8 * * 1', label: 'Weekly (Mon 08:00)' },
]

const chanById = computed(() => Object.fromEntries(channels.value.map(c => [c.id, c])))

async function load() {
  ;[subs.value, channels.value] = await Promise.all([
    api.subscriptions().catch(() => []),
    api.channels().catch(() => []),
  ])
}
onMounted(load)

async function add() {
  addError.value = ''
  if (!form.target_id.trim()) { addError.value = 'Enter a channel/playlist URL or id.'; return }
  adding.value = true
  try {
    const r = await api.createSubscription({ ...form, target_id: form.target_id.trim() })
    if (r.status === 409) { addError.value = 'Already subscribed to that target.'; return }
    if (!r.ok) { addError.value = 'Could not create subscription.'; return }
    form.target_id = ''
    await load()
  } finally { adding.value = false }
}

async function toggle(s: Subscription) {
  busyId.value = s.id
  try {
    const updated = await api.updateSubscription(s.id, { enabled: !s.enabled })
    Object.assign(s, updated)
  } finally { busyId.value = null }
}

async function scan(s: Subscription) {
  await api.scanSubscription(s.id)
  flash[s.id] = 'Checking for new uploads…'
  setTimeout(() => delete flash[s.id], 4000)
}
async function backlog(s: Subscription) {
  if (!confirm('Fetch the entire backlog? This can enqueue a lot of videos.')) return
  await api.backlogSubscription(s.id)
  flash[s.id] = 'Fetching full backlog — watch the queue.'
  setTimeout(() => delete flash[s.id], 5000)
}
async function remove(s: Subscription) {
  if (!confirm(`Unsubscribe from ${s.target_id}? Downloaded videos stay.`)) return
  await api.deleteSubscription(s.id)
  subs.value = subs.value.filter(x => x.id !== s.id)
}

// --- inline filter/cron editor ---
const draft = reactive<{ cron: string; min_duration_min: number | null; title_regex: string; max_items: number | null }>(
  { cron: '', min_duration_min: null, title_regex: '', max_items: null })

function startEdit(s: Subscription) {
  editing.value = s.id
  const f = parseFilter(s.filter_json)
  draft.cron = s.cron
  draft.min_duration_min = f.min_duration_s ? Math.round(f.min_duration_s / 60) : null
  draft.title_regex = f.title_regex ?? ''
  draft.max_items = f.max_items ?? null
}
function parseFilter(json: string | null): Record<string, any> {
  if (!json) return {}
  try { return JSON.parse(json) } catch { return {} }
}
async function saveEdit(s: Subscription) {
  const filter: Record<string, unknown> = {}
  if (draft.min_duration_min) filter.min_duration_s = draft.min_duration_min * 60
  if (draft.title_regex.trim()) filter.title_regex = draft.title_regex.trim()
  if (draft.max_items) filter.max_items = draft.max_items
  const patch: SubscriptionInput = {
    cron: draft.cron,
    filter_json: Object.keys(filter).length ? JSON.stringify(filter) : '',
  }
  busyId.value = s.id
  try {
    const updated = await api.updateSubscription(s.id, patch)
    Object.assign(s, updated)
    editing.value = null
  } finally { busyId.value = null }
}

function nextLabel(s: Subscription): string {
  if (!s.enabled) return 'paused'
  if (!s.next_check) return 'soon'
  const diff = s.next_check - Date.now() / 1000
  if (diff <= 0) return 'due now'
  if (diff < 3600) return `in ${Math.round(diff / 60)}m`
  if (diff < 86400) return `in ${Math.round(diff / 3600)}h`
  return `in ${Math.round(diff / 86400)}d`
}
</script>

<template>
  <div class="channels">
    <form class="add" @submit.prevent="add">
      <select v-model="form.kind" aria-label="Type">
        <option value="channel">Channel</option>
        <option value="playlist">Playlist</option>
      </select>
      <input
        v-model="form.target_id" type="text" placeholder="Channel URL, @handle, UC… id, or playlist id"
        aria-label="Target" autocomplete="off" />
      <select v-model="form.cron" aria-label="Check frequency">
        <option v-for="c in CRONS" :key="c.v" :value="c.v">{{ c.label }}</option>
      </select>
      <button type="submit" :disabled="adding">Subscribe</button>
    </form>
    <p v-if="addError" class="err">{{ addError }}</p>

    <p v-if="subs.length === 0" class="empty">
      No subscriptions yet. <span class="muted">Subscribe to a channel to auto-fetch new uploads.</span>
    </p>

    <div class="list">
      <div v-for="s in subs" :key="s.id" class="sub" :class="{ off: !s.enabled }">
        <div class="row">
          <img v-if="chanById[s.target_id]?.thumb" :src="chanById[s.target_id].thumb!" alt="" class="avatar" />
          <div class="who">
            <div class="name">{{ chanById[s.target_id]?.name || s.target_id }}</div>
            <div class="tags">
              <span class="kind">{{ s.kind }}</span>
              <span>· {{ nextLabel(s) }}</span>
              <span v-if="s.last_check">· checked {{ relTime(s.last_check) }}</span>
              <span v-if="s.filter_json" title="Has filters">· filtered</span>
            </div>
          </div>
          <div class="ops">
            <label class="sw" :title="s.enabled ? 'Enabled' : 'Paused'">
              <input type="checkbox" :checked="s.enabled" :disabled="busyId === s.id" @change="toggle(s)" />
              <span></span>
            </label>
            <button @click="scan(s)" title="Check now">Scan</button>
            <button @click="backlog(s)" title="Fetch entire backlog">Backlog</button>
            <button @click="editing === s.id ? editing = null : startEdit(s)">Edit</button>
            <button class="danger" @click="remove(s)" title="Unsubscribe">✕</button>
          </div>
        </div>

        <p v-if="flash[s.id]" class="flash">{{ flash[s.id] }}</p>

        <div v-if="editing === s.id" class="editor">
          <label>Frequency
            <select v-model="draft.cron">
              <option v-for="c in CRONS" :key="c.v" :value="c.v">{{ c.label }}</option>
              <option v-if="!CRONS.some(c => c.v === draft.cron)" :value="draft.cron">{{ draft.cron }} (custom)</option>
            </select>
          </label>
          <label>Min length (min)
            <input v-model.number="draft.min_duration_min" type="number" min="0" placeholder="any" />
          </label>
          <label>Title matches
            <input v-model="draft.title_regex" type="text" placeholder="regex (optional)" />
          </label>
          <label>Max per check
            <input v-model.number="draft.max_items" type="number" min="1" placeholder="30" />
          </label>
          <button class="save" :disabled="busyId === s.id" @click="saveEdit(s)">Save</button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.add { display: flex; gap: 0.5rem; margin-bottom: 0.4rem; flex-wrap: wrap; }
.add input { flex: 1; min-width: 12rem; }
.add input, .add select, .add button {
  background: var(--panel); border: 1px solid var(--border); color: var(--fg);
  border-radius: 9px; padding: 0.5rem 0.7rem; font-size: 0.9rem;
}
.add button { background: #14313f; color: var(--accent); cursor: pointer; border-color: #1d4a5e; }
.add button:hover:not(:disabled) { background: #185066; }
.err { color: var(--danger); font-size: 0.85rem; margin: 0.2rem 0 0.8rem; }
.empty { color: var(--fg); padding: 2rem 0; }
.empty .muted, .muted { color: var(--muted); }

.list { display: flex; flex-direction: column; gap: 0.55rem; margin-top: 0.8rem; }
.sub { background: var(--panel); border: 1px solid var(--border); border-radius: 11px; padding: 0.7rem 0.85rem; }
.sub.off { opacity: 0.62; }
.row { display: flex; align-items: center; gap: 0.7rem; }
.avatar { width: 38px; height: 38px; border-radius: 50%; object-fit: cover; flex: none; }
.who { flex: 1; min-width: 0; }
.name { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.tags { font-size: 0.8rem; color: var(--muted); display: flex; gap: 0.35rem; flex-wrap: wrap; }
.kind { text-transform: capitalize; }
.ops { display: flex; align-items: center; gap: 0.35rem; flex: none; }
.ops button {
  background: #24303a; color: var(--fg); border: 0; border-radius: 6px; padding: 0.32rem 0.55rem;
  cursor: pointer; font-size: 0.82rem;
}
.ops button:hover { background: #2e3d49; }
.ops button.danger:hover { background: #4a2020; color: #ffb0b0; }

.sw { position: relative; width: 34px; height: 20px; flex: none; }
.sw input { opacity: 0; width: 0; height: 0; }
.sw span { position: absolute; inset: 0; background: #3a4048; border-radius: 20px; cursor: pointer; transition: 0.2s; }
.sw span::before {
  content: ''; position: absolute; width: 14px; height: 14px; left: 3px; top: 3px;
  background: #fff; border-radius: 50%; transition: 0.2s;
}
.sw input:checked + span { background: var(--accent); }
.sw input:checked + span::before { transform: translateX(14px); }

.flash { color: var(--accent); font-size: 0.82rem; margin: 0.5rem 0 0; }
.editor {
  display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 0.6rem; align-items: end;
  margin-top: 0.8rem; padding-top: 0.8rem; border-top: 1px solid var(--border);
}
.editor label { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.78rem; color: var(--muted); }
.editor input, .editor select {
  background: var(--bg); border: 1px solid var(--border); color: var(--fg); border-radius: 7px;
  padding: 0.4rem 0.5rem; font-size: 0.85rem;
}
.editor .save { background: #14313f; color: var(--accent); border: 1px solid #1d4a5e; border-radius: 7px;
                padding: 0.45rem; cursor: pointer; }
.editor .save:hover:not(:disabled) { background: #185066; }
</style>
