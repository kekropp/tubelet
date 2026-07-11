<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import type { PreviewResult, ScopeMode, ScopeInput } from '../types'
import { FORMAT_PRESETS } from '../types'
import { api } from '../api'

// Opened for a channel/playlist add. Either `url` (an omnibox paste) or `kind`+`id`
// (an already-classified target from the Channels page) identifies the source.
const props = defineProps<{ url?: string; kind?: 'channel' | 'playlist'; id?: string }>()
const emit = defineEmits<{ (e: 'close'): void; (e: 'done', msg: string): void }>()

const CRONS = [
  { v: '0 */6 * * *', label: 'Every 6 hours' },
  { v: '0 */12 * * *', label: 'Every 12 hours' },
  { v: '0 8 * * *', label: 'Daily at 08:00' },
  { v: '0 8 * * 1', label: 'Weekly (Mon 08:00)' },
]

const loading = ref(true)
const preview = ref<PreviewResult | null>(null)
const loadError = ref('')
const submitting = ref(false)
const submitError = ref('')

const choice = reactive({
  mode: 'newest' as ScopeMode,
  n: 20,
  after: '',
  subscribe: true,
  cron: '0 */6 * * *',
  quality: 'default',
  customFormat: '',
})

// 'default' follows Settings → Quality; applies to the initial backlog AND (when subscribed) future uploads.
const QUALITY_CHOICES = [
  { v: 'default', label: 'Default quality (global setting)' },
  ...FORMAT_PRESETS,
]

// The profile string the backend stores/stamps: preset key or custom:<-f string>.
function qualityProf(): string {
  if (choice.quality !== 'custom') return choice.quality
  const f = choice.customFormat.trim()
  return f ? `custom:${f}` : 'default'
}

onMounted(async () => {
  try {
    const p = await api.preview({ url: props.url, kind: props.kind, id: props.id })
    preview.value = p
    if (p.kind === 'channel' || p.kind === 'playlist') {
      // A brand-new subscribe defaults to a modest backlog for big channels; small ones grab all.
      choice.mode = p.video_count > 50 ? 'newest' : 'all'
    }
  } catch (e) {
    loadError.value = 'Could not read that listing. ' + (e as Error).message
  } finally {
    loading.value = false
  }
})

const title = computed(() => preview.value?.title || preview.value?.channel_name || props.id || 'this')
const isContainer = computed(() => preview.value?.kind === 'channel' || preview.value?.kind === 'playlist')

// "None" (subscribe-only, future uploads) only makes sense while subscribed.
function pick(mode: ScopeMode) {
  choice.mode = mode
  if (mode === 'none') choice.subscribe = true
}
function toggleSubscribe() {
  choice.subscribe = !choice.subscribe
  if (!choice.subscribe && choice.mode === 'none') choice.mode = 'all'
}

const confirmDisabled = computed(() =>
  submitting.value ||
  (choice.mode === 'newest' && (!choice.n || choice.n < 1)) ||
  (choice.mode === 'after' && !choice.after) ||
  (choice.mode === 'none' && !choice.subscribe))

function scopeInput(): ScopeInput {
  if (choice.mode === 'newest') return { mode: 'newest', n: choice.n }
  if (choice.mode === 'after') return { mode: 'after', after: choice.after }
  return { mode: choice.mode }
}

function todayYmd(): string {
  const d = new Date()
  return `${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}${String(d.getDate()).padStart(2, '0')}`
}

async function confirm() {
  const p = preview.value
  if (!p || !p.id) return
  submitError.value = ''
  submitting.value = true
  try {
    const scope = scopeInput()
    if (choice.subscribe) {
      const filter: Record<string, unknown> = {}
      if (choice.mode === 'after' && choice.after) filter.date_floor = choice.after.replaceAll('-', '')
      if (choice.mode === 'none') filter.date_floor = todayYmd()
      const r = await api.createSubscription({
        kind: p.kind as 'channel' | 'playlist',
        target_id: p.id,
        cron: choice.cron,
        quality_prof: qualityProf(),
        filter_json: Object.keys(filter).length ? JSON.stringify(filter) : '',
      })
      if (r.status === 409) { submitError.value = 'Already subscribed to that target.'; return }
      if (!r.ok) { submitError.value = 'Could not create the subscription.'; return }
      const sub = await r.json()
      // Kick the initial fetch (or, for "none", baseline the existing backlog onto the skip list).
      await api.backlogSubscription(sub.id, scope)
      emit('done', subscribeMsg())
    } else {
      const paste = props.url && props.url.trim().length ? props.url.trim() : buildUrl(p.kind, p.id)
      const prof = qualityProf()
      await api.intake(paste, scope, prof === 'default' ? undefined : prof)
      emit('done', 'Fetching your selection — new videos will appear in the queue.')
    }
  } catch (e) {
    submitError.value = 'Request failed: ' + (e as Error).message
  } finally {
    submitting.value = false
  }
}

function subscribeMsg(): string {
  switch (choice.mode) {
    case 'none': return `Subscribed to ${title.value}. Only new uploads from now on will download.`
    case 'newest': return `Subscribed — fetching the newest ${choice.n} and watching for new uploads.`
    case 'after': return `Subscribed — fetching everything since ${choice.after} and watching for new uploads.`
    default: return `Subscribed — fetching all ${preview.value?.video_count ?? ''} videos and watching for new uploads.`
  }
}

function buildUrl(kind: string, id: string): string {
  if (kind === 'playlist') return `https://www.youtube.com/playlist?list=${id}`
  if (id.startsWith('UC')) return `https://www.youtube.com/channel/${id}`
  if (id.startsWith('@')) return `https://www.youtube.com/${id}`
  return `https://www.youtube.com/${id.replace(/^\//, '')}`
}

function fmtDate(d: string | null): string {
  if (!d || d.length !== 8) return ''
  return `${d.slice(0, 4)}-${d.slice(4, 6)}-${d.slice(6, 8)}`
}
</script>

<template>
  <div class="backdrop" @click.self="emit('close')">
    <div class="dialog" role="dialog" aria-modal="true">
      <button class="x" aria-label="Close" @click="emit('close')">✕</button>

      <div v-if="loading" class="state">Reading the listing…</div>
      <div v-else-if="loadError" class="state err">{{ loadError }}</div>
      <div v-else-if="!isContainer" class="state">
        That looks like a single video, not a channel or playlist.
        <button class="link" @click="emit('close')">Close</button>
      </div>

      <template v-else>
        <header>
          <span class="kind" :class="preview!.kind">{{ preview!.kind }}</span>
          <h2>{{ title }}</h2>
          <p class="count">{{ preview!.video_count.toLocaleString() }} video{{ preview!.video_count === 1 ? '' : 's' }} available</p>
        </header>

        <div class="scopes">
          <label class="scope" :class="{ on: choice.mode === 'all' }">
            <input type="radio" name="scope" :checked="choice.mode === 'all'" @change="pick('all')" />
            <div>
              <div class="t">Download everything</div>
              <div class="d">All {{ preview!.video_count.toLocaleString() }} videos.</div>
            </div>
          </label>

          <label class="scope" :class="{ on: choice.mode === 'newest' }">
            <input type="radio" name="scope" :checked="choice.mode === 'newest'" @change="pick('newest')" />
            <div>
              <div class="t">Only the newest
                <input class="num" type="number" min="1" v-model.number="choice.n"
                       @focus="pick('newest')" /> videos
              </div>
              <div class="d">Best for big channels — grabs just the most recent uploads.</div>
            </div>
          </label>

          <label class="scope" :class="{ on: choice.mode === 'after' }">
            <input type="radio" name="scope" :checked="choice.mode === 'after'" @change="pick('after')" />
            <div>
              <div class="t">Uploaded on or after
                <input class="date" type="date" v-model="choice.after" @focus="pick('after')" />
              </div>
              <div class="d" :class="{ warn: choice.mode === 'after' && !preview!.has_dates }">
                <template v-if="preview!.has_dates">Filters by YouTube's upload dates.</template>
                <template v-else>⚠ YouTube didn't expose dates for this listing — this may include more than expected.</template>
              </div>
            </div>
          </label>

          <label class="scope" :class="{ on: choice.mode === 'none', disabled: !choice.subscribe }">
            <input type="radio" name="scope" :checked="choice.mode === 'none'"
                   :disabled="!choice.subscribe" @change="pick('none')" />
            <div>
              <div class="t">Nothing yet — only new uploads</div>
              <div class="d">Skip the backlog; download videos posted from today onward.</div>
            </div>
          </label>
        </div>

        <div class="sub-row">
          <label class="check">
            <input type="checkbox" :checked="choice.subscribe" :disabled="choice.mode === 'none'" @change="toggleSubscribe" />
            Keep watching for new uploads
          </label>
          <select v-if="choice.subscribe" v-model="choice.cron" aria-label="Check frequency">
            <option v-for="c in CRONS" :key="c.v" :value="c.v">{{ c.label }}</option>
          </select>
        </div>

        <div class="quality-row">
          <label class="q-label">Quality
            <select v-model="choice.quality" aria-label="Download quality">
              <option v-for="q in QUALITY_CHOICES" :key="q.v" :value="q.v">{{ q.label }}</option>
            </select>
          </label>
          <input v-if="choice.quality === 'custom'" v-model="choice.customFormat" type="text"
                 class="q-custom" spellcheck="false" aria-label="Custom yt-dlp format string"
                 placeholder="bestvideo[height<=1440]+bestaudio/best" />
          <p v-if="choice.subscribe" class="q-hint">Applies to these videos and to future uploads from this {{ preview!.kind }}.</p>
        </div>

        <details v-if="preview!.sample.length" class="sample">
          <summary>Preview newest {{ preview!.sample.length }}</summary>
          <ul>
            <li v-for="s in preview!.sample" :key="s.id">
              <span class="st">{{ s.title || s.id }}</span>
              <span v-if="fmtDate(s.upload_date)" class="sd">{{ fmtDate(s.upload_date) }}</span>
            </li>
          </ul>
        </details>

        <p v-if="submitError" class="err">{{ submitError }}</p>

        <footer>
          <button class="ghost" @click="emit('close')">Cancel</button>
          <button class="go" :disabled="confirmDisabled" @click="confirm">
            {{ submitting ? '…' : (choice.subscribe ? 'Subscribe' : 'Download') }}
          </button>
        </footer>
      </template>
    </div>
  </div>
</template>

<style scoped>
.backdrop {
  position: fixed; inset: 0; background: rgba(0, 0, 0, 0.6); z-index: 50;
  display: flex; align-items: center; justify-content: center; padding: 1rem;
}
.dialog {
  position: relative; width: 100%; max-width: 34rem; max-height: 90vh; overflow-y: auto;
  background: var(--panel); border: 1px solid var(--border); border-radius: 14px; padding: 1.3rem;
}
.x { position: absolute; top: 0.8rem; right: 0.9rem; background: none; border: 0; color: var(--muted);
     font-size: 1rem; cursor: pointer; }
.x:hover { color: var(--fg); }
.state { padding: 1.5rem 0.5rem; color: var(--muted); text-align: center; }
.state.err, .err { color: var(--danger); }
.link { background: none; border: 0; color: var(--accent); cursor: pointer; margin-left: 0.4rem; }

header { margin-bottom: 1rem; padding-right: 1.5rem; }
.kind { font-size: 0.68rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em;
        padding: 0.15rem 0.45rem; border-radius: 6px; background: #2a2140; color: #b79cff; }
.kind.playlist { background: #123528; color: #6fe0a8; }
header h2 { margin: 0.5rem 0 0.2rem; font-size: 1.25rem; overflow-wrap: anywhere; }
.count { margin: 0; color: var(--muted); font-size: 0.88rem; }

.scopes { display: flex; flex-direction: column; gap: 0.5rem; margin-bottom: 1rem; }
.scope {
  display: flex; gap: 0.6rem; align-items: flex-start; padding: 0.65rem 0.75rem; cursor: pointer;
  background: var(--bg); border: 1px solid var(--border); border-radius: 10px;
}
.scope.on { border-color: var(--accent); }
.scope.disabled { opacity: 0.45; cursor: not-allowed; }
.scope input[type=radio] { margin-top: 0.2rem; accent-color: var(--accent); }
.scope .t { font-size: 0.95rem; display: flex; align-items: center; gap: 0.35rem; flex-wrap: wrap; }
.scope .d { font-size: 0.8rem; color: var(--muted); margin-top: 0.15rem; }
.scope .d.warn { color: #e8b661; }
.num { width: 4.5rem; }
.num, .date {
  background: var(--panel); border: 1px solid var(--border); color: var(--fg);
  border-radius: 6px; padding: 0.2rem 0.4rem; font-size: 0.9rem;
}

.sub-row { display: flex; align-items: center; justify-content: space-between; gap: 0.6rem;
           flex-wrap: wrap; padding: 0.6rem 0; border-top: 1px solid var(--border); }
.check { display: flex; align-items: center; gap: 0.5rem; font-size: 0.9rem; cursor: pointer; }
.check input { accent-color: var(--accent); }
.sub-row select {
  background: var(--bg); border: 1px solid var(--border); color: var(--fg);
  border-radius: 8px; padding: 0.4rem 0.55rem; font-size: 0.85rem;
}

.quality-row { display: flex; flex-direction: column; gap: 0.45rem; padding: 0.6rem 0;
               border-top: 1px solid var(--border); }
.q-label { display: flex; align-items: center; gap: 0.6rem; font-size: 0.9rem; }
.q-label select {
  flex: 1; background: var(--bg); border: 1px solid var(--border); color: var(--fg);
  border-radius: 8px; padding: 0.4rem 0.55rem; font-size: 0.85rem;
}
.q-custom {
  background: var(--bg); border: 1px solid var(--border); color: var(--fg); border-radius: 8px;
  padding: 0.4rem 0.55rem; font-size: 0.85rem; font-family: ui-monospace, monospace; width: 100%;
}
.q-hint { margin: 0; font-size: 0.78rem; color: var(--muted); }

.sample { margin-top: 0.4rem; font-size: 0.85rem; }
.sample summary { cursor: pointer; color: var(--muted); }
.sample ul { list-style: none; margin: 0.5rem 0 0; padding: 0; display: flex; flex-direction: column; gap: 0.25rem; }
.sample li { display: flex; justify-content: space-between; gap: 0.6rem; }
.sample .st { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sample .sd { color: var(--muted); flex: none; }

footer { display: flex; justify-content: flex-end; gap: 0.6rem; margin-top: 1.1rem; }
footer button { border-radius: 9px; padding: 0.55rem 1.2rem; font-size: 0.92rem; cursor: pointer; font-weight: 600; }
.ghost { background: transparent; border: 1px solid var(--border); color: var(--fg); }
.ghost:hover { background: var(--bg); }
.go { background: var(--accent); color: #04121c; border: 0; }
.go:disabled { opacity: 0.4; cursor: default; }
</style>
