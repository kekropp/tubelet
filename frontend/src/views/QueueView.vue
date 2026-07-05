<script setup lang="ts">
import { ref, reactive, computed, onMounted, watch, onUnmounted } from 'vue'
import type { Job, QueueFilter, BulkAction } from '../types'
import { api } from '../api'
import { useQueue } from '../stores/queue'
import { pct, stateLabel, relTime } from '../format'

const queue = useQueue()

const PAGE_SIZE = 50

const filter = ref<QueueFilter>('active')
const page = ref(1)
const items = ref<Job[]>([])
const total = ref(0)
const loading = ref(false)
const busy = ref(false)
const selected = reactive(new Set<number>())

const tabs: { f: QueueFilter; label: string; count: () => number | null }[] = [
  { f: 'active', label: 'Active', count: () => queue.stats.queued + queue.stats.running },
  { f: 'queued', label: 'Queued', count: () => queue.stats.queued },
  { f: 'running', label: 'Running', count: () => queue.stats.running },
  { f: 'failed', label: 'Failed', count: () => queue.stats.failed },
  { f: 'done', label: 'Done', count: () => queue.stats.done },
  { f: 'all', label: 'All', count: () => null },
]

const totalPages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))
const allOnPageSelected = computed(() => items.value.length > 0 && items.value.every(j => selected.has(j.id)))

async function fetchPage() {
  loading.value = true
  try {
    const r = await api.queueJobs(filter.value, page.value, PAGE_SIZE)
    // If a bulk delete shrank the list past the current page, step back and re-fetch.
    if (r.items.length === 0 && r.total > 0 && page.value > 1) {
      page.value = Math.min(page.value, Math.max(1, Math.ceil(r.total / PAGE_SIZE)))
      return fetchPage()
    }
    items.value = r.items
    total.value = r.total
  } finally { loading.value = false }
}

onMounted(fetchPage)

function setFilter(f: QueueFilter) {
  if (filter.value === f) return
  filter.value = f
  page.value = 1
  selected.clear()
}

watch(page, fetchPage)

// Live: when queue membership changes (job transitions / bulk ops elsewhere), re-fetch the page.
// Debounced so a burst of completions during active downloads coalesces into one request.
let timer: ReturnType<typeof setTimeout> | null = null
watch(() => queue.rev, () => {
  if (timer) clearTimeout(timer)
  timer = setTimeout(fetchPage, 400)
})
onUnmounted(() => { if (timer) clearTimeout(timer) })

function toggle(id: number) { selected.has(id) ? selected.delete(id) : selected.add(id) }
function toggleAllOnPage() {
  if (allOnPageSelected.value) items.value.forEach(j => selected.delete(j.id))
  else items.value.forEach(j => selected.add(j.id))
}

function livePct(j: Job): number { return queue.live[j.id]?.pct ?? j.progress }

async function bulk(action: BulkAction, priority?: number) {
  const ids = [...selected]
  if (ids.length === 0) return
  if (action === 'cancel' && !confirm(`Cancel ${ids.length} selected job(s)? Downloaded files are kept.`)) return
  busy.value = true
  try {
    await api.queueBulk({ action, ids, priority })
    selected.clear()
    await Promise.all([fetchPage(), queue.refresh()])
  } finally { busy.value = false }
}

async function scopeAction(action: BulkAction, scope: 'queued' | 'active', label: string) {
  if (!confirm(label)) return
  busy.value = true
  try {
    await api.queueBulk({ action, scope })
    selected.clear()
    await Promise.all([fetchPage(), queue.refresh()])
  } finally { busy.value = false }
}

async function rowCancel(j: Job) {
  busy.value = true
  try { await api.queueBulk({ action: 'cancel', ids: [j.id] }); await Promise.all([fetchPage(), queue.refresh()]) }
  finally { busy.value = false }
}
async function rowAction(j: Job, action: BulkAction) {
  busy.value = true
  try { await api.queueBulk({ action, ids: [j.id] }); await Promise.all([fetchPage(), queue.refresh()]) }
  finally { busy.value = false }
}

function hideThumb(e: Event) { (e.target as HTMLImageElement).style.visibility = 'hidden' }
</script>

<template>
  <div class="queue">
    <div class="topbar">
      <h2>Queue</h2>
      <div class="global">
        <button v-if="!queue.paused" @click="queue.pauseAll()" title="Stop starting new downloads">⏸ Pause queue</button>
        <button v-else class="resume" @click="queue.resumeAll()">▶ Resume queue</button>
        <button :disabled="busy || !queue.stats.queued"
          @click="scopeAction('cancel', 'queued', 'Remove ALL queued (not-yet-started) jobs? Running downloads keep going.')">
          Clear queued
        </button>
        <button class="danger" :disabled="busy"
          @click="scopeAction('cancel', 'active', 'Cancel EVERYTHING in the queue, including running downloads? Files already saved are kept.')">
          Cancel all
        </button>
      </div>
    </div>

    <div class="tabs">
      <button v-for="t in tabs" :key="t.f" :class="{ on: filter === t.f }" @click="setFilter(t.f)">
        {{ t.label }}<span v-if="t.count() !== null" class="tc">{{ t.count() }}</span>
      </button>
    </div>

    <div v-if="selected.size" class="selbar">
      <span>{{ selected.size }} selected</span>
      <button @click="bulk('resume')">Start</button>
      <button @click="bulk('pause')">Pause</button>
      <button @click="bulk('priority', 1)" title="Move to front">▲ Prioritize</button>
      <button @click="bulk('retry')">Retry</button>
      <button class="danger" @click="bulk('cancel')">Cancel</button>
      <button class="ghost" @click="selected.clear()">Clear selection</button>
    </div>

    <div v-if="items.length" class="head">
      <label class="chk"><input type="checkbox" :checked="allOnPageSelected" @change="toggleAllOnPage" /></label>
      <span class="hcol">{{ total.toLocaleString() }} job{{ total === 1 ? '' : 's' }}</span>
    </div>

    <div class="rows">
      <div v-for="j in items" :key="j.id" class="row" :class="{ sel: selected.has(j.id) }">
        <label class="chk"><input type="checkbox" :checked="selected.has(j.id)" @change="toggle(j.id)" /></label>
        <div class="thumb"><img v-if="j.thumb" :src="j.thumb" alt="" loading="lazy" @error="hideThumb" /></div>
        <div class="info">
          <div class="title">{{ j.title || j.youtube_id }}</div>
          <div class="sub">
            <span class="badge" :class="j.state">{{ stateLabel(j.state) }}</span>
            <span v-if="j.priority === 1" class="prio" title="Prioritized">★</span>
            <span v-if="j.state === 'downloading'">· {{ pct(livePct(j)) }}<template v-if="queue.live[j.id]?.speed"> · {{ queue.live[j.id]!.speed }}</template></span>
            <span v-else-if="j.state === 'done'">· {{ relTime(j.finished_at) }}</span>
            <span v-else-if="j.state === 'failed'" class="ek">· {{ j.error_kind || 'error' }}</span>
          </div>
        </div>
        <div class="ract">
          <button v-if="j.state === 'queued'" @click="rowAction(j, 'pause')" title="Pause">⏸</button>
          <button v-if="j.state === 'paused'" @click="rowAction(j, 'resume')" title="Start">▶</button>
          <button v-if="j.state === 'failed'" @click="rowAction(j, 'retry')" title="Retry">↻</button>
          <button v-if="(j.state === 'queued' || j.state === 'paused') && j.priority !== 1"
            @click="rowAction(j, 'priority')" title="Prioritize">▲</button>
          <button v-if="j.state !== 'done'" class="danger" @click="rowCancel(j)" title="Cancel">✕</button>
        </div>
      </div>
    </div>

    <p v-if="!loading && items.length === 0" class="empty">Nothing here.</p>

    <div v-if="totalPages > 1" class="pager">
      <button :disabled="page <= 1" @click="page--">← Prev</button>
      <span>Page {{ page }} of {{ totalPages }}</span>
      <button :disabled="page >= totalPages" @click="page++">Next →</button>
    </div>
  </div>
</template>

<style scoped>
.topbar { display: flex; align-items: center; justify-content: space-between; gap: 1rem; flex-wrap: wrap; margin-bottom: 1rem; }
.topbar h2 { margin: 0; font-size: 1.3rem; }
.global { display: flex; gap: 0.4rem; flex-wrap: wrap; }
.global button, .selbar button, .pager button {
  background: #24303a; color: var(--fg); border: 0; border-radius: 7px; padding: 0.4rem 0.75rem;
  cursor: pointer; font-size: 0.85rem;
}
.global button:hover:not(:disabled), .selbar button:hover, .pager button:hover:not(:disabled) { background: #2e3d49; }
.global button:disabled, .pager button:disabled { opacity: 0.4; cursor: default; }
.global .resume { background: #14313f; color: var(--accent); }
.danger { background: #3a1a1a !important; color: #ffb0b0 !important; }
.danger:hover { background: #4a2020 !important; }

.tabs { display: flex; gap: 0.3rem; flex-wrap: wrap; margin-bottom: 1rem; border-bottom: 1px solid var(--border); padding-bottom: 0.6rem; }
.tabs button {
  background: none; border: 0; color: var(--muted); cursor: pointer; font-size: 0.9rem;
  padding: 0.35rem 0.7rem; border-radius: 7px;
}
.tabs button:hover { color: var(--fg); background: var(--panel); }
.tabs button.on { color: var(--accent); background: #14313f; }
.tc { margin-left: 0.35rem; font-size: 0.75rem; opacity: 0.8; }

.selbar {
  display: flex; align-items: center; gap: 0.4rem; flex-wrap: wrap; background: #14313f;
  border: 1px solid #1d4a5e; border-radius: 9px; padding: 0.5rem 0.7rem; margin-bottom: 0.8rem;
}
.selbar span { font-size: 0.88rem; margin-right: 0.3rem; }
.selbar .ghost { background: transparent !important; color: var(--muted) !important; margin-left: auto; }

.head { display: flex; align-items: center; gap: 0.6rem; padding: 0 0.3rem 0.4rem; color: var(--muted); font-size: 0.8rem; }
.chk { display: flex; align-items: center; }
.chk input { accent-color: var(--accent); width: 16px; height: 16px; cursor: pointer; }

.rows { display: flex; flex-direction: column; gap: 0.4rem; }
.row {
  display: grid; grid-template-columns: auto 72px 1fr auto; gap: 0.6rem; align-items: center;
  background: var(--panel); border: 1px solid var(--border); border-radius: 9px; padding: 0.45rem 0.6rem;
}
.row.sel { border-color: var(--accent); }
.thumb { width: 72px; height: 40px; border-radius: 5px; overflow: hidden; background: #0c0c0f; }
.thumb img { width: 100%; height: 100%; object-fit: cover; }
.info { min-width: 0; }
.title { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; font-size: 0.92rem; }
.sub { font-size: 0.8rem; color: var(--muted); display: flex; align-items: center; gap: 0.3rem; flex-wrap: wrap; margin-top: 0.15rem; }
.badge { font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.03em; padding: 0.1rem 0.38rem; border-radius: 5px; background: #24303a; color: var(--muted); }
.badge.downloading, .badge.converting, .badge.indexing, .badge.fetching_meta { background: #14313f; color: var(--accent); }
.badge.paused { background: #2a2140; color: #b79cff; }
.badge.failed { background: #3a1a1a; color: var(--danger); }
.badge.done { background: #123528; color: #6fe0a8; }
.prio { color: var(--accent); }
.ek { color: var(--danger); text-transform: capitalize; }
.ract { display: flex; gap: 0.25rem; }
.ract button {
  background: #24303a; color: var(--fg); border: 0; border-radius: 6px; padding: 0.3rem 0.5rem;
  cursor: pointer; font-size: 0.82rem;
}
.ract button:hover { background: #2e3d49; }

.empty { color: var(--muted); text-align: center; padding: 2.5rem 0; }
.pager { display: flex; align-items: center; justify-content: center; gap: 1rem; margin-top: 1.2rem; color: var(--muted); font-size: 0.88rem; }
</style>
