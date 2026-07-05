<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted, nextTick } from 'vue'
import type { Video, ChannelSummary } from '../types'
import { api } from '../api'
import { duration, shortDate } from '../format'
import VideoDetail from '../components/VideoDetail.vue'

const PAGE = 60

const query = ref('')
const channel = ref<string | null>(null)
const sort = ref('-published')
const channels = ref<ChannelSummary[]>([])

const items = ref<Video[]>([])
const total = ref(0)
const page = ref(0)          // last loaded page (0 = none yet)
const loading = ref(false)
const done = ref(false)      // no more pages
const selected = ref<Video | null>(null)

const sentinel = ref<HTMLElement | null>(null)
let observer: IntersectionObserver | null = null
let debounce: ReturnType<typeof setTimeout> | null = null

async function loadNext() {
  if (loading.value || done.value) return
  loading.value = true
  try {
    const next = page.value + 1
    const res = await api.videos({
      query: query.value || undefined,
      channel: channel.value || undefined,
      sort: sort.value,
      page: next,
      page_size: PAGE,
    })
    if (next === 1) items.value = res.items
    else items.value.push(...res.items)
    page.value = next
    total.value = res.total
    done.value = items.value.length >= res.total || res.items.length === 0
  } finally {
    loading.value = false
    // If the sentinel is still visible (short page), keep filling.
    await nextTick()
  }
}

function reset() {
  items.value = []
  page.value = 0
  total.value = 0
  done.value = false
  void loadNext()
}

// Debounced search-as-you-type (150 ms, server-side FTS5 prefix).
watch(query, () => {
  if (debounce) clearTimeout(debounce)
  debounce = setTimeout(reset, 150)
})
watch([channel, sort], reset)

function pickChannel(id: string | null) { channel.value = channel.value === id ? null : id }

onMounted(async () => {
  channels.value = await api.channels().catch(() => [])
  reset()
  observer = new IntersectionObserver(
    (entries) => { if (entries[0].isIntersecting) void loadNext() },
    { rootMargin: '600px' },
  )
  if (sentinel.value) observer.observe(sentinel.value)
})
onUnmounted(() => { observer?.disconnect(); if (debounce) clearTimeout(debounce) })

function onDeleted(id: string) {
  items.value = items.value.filter(v => v.id !== id)
  total.value = Math.max(0, total.value - 1)
  selected.value = null
}
</script>

<template>
  <div class="library">
    <aside class="rail">
      <button class="ch" :class="{ on: channel === null }" @click="pickChannel(null)">
        All channels <span class="n">{{ total }}</span>
      </button>
      <button
        v-for="c in channels" :key="c.id" class="ch" :class="{ on: channel === c.id }"
        @click="pickChannel(c.id)">
        <img v-if="c.thumb" :src="c.thumb" alt="" />
        <span class="name">{{ c.name }}</span>
        <span class="n">{{ c.video_count }}</span>
      </button>
    </aside>

    <div class="main">
      <div class="controls">
        <input
          v-model="query" type="search" placeholder="Search title, description, tags…"
          aria-label="Search library" autocomplete="off" />
        <select v-model="sort" aria-label="Sort">
          <option value="-published">Newest first</option>
          <option value="published">Oldest first</option>
          <option value="-added">Recently added</option>
          <option value="-duration">Longest</option>
          <option value="duration">Shortest</option>
          <option value="title">Title A–Z</option>
        </select>
      </div>

      <p v-if="page > 0 && total === 0" class="empty">
        No videos match. <span class="muted">Try a different search or channel.</span>
      </p>

      <div class="grid">
        <button v-for="v in items" :key="v.id" class="card" @click="selected = v">
          <div class="thumb">
            <img v-if="v.thumb" :src="v.thumb" alt="" loading="lazy" />
            <span class="dur">{{ duration(v.duration_s) }}</span>
          </div>
          <div class="title">{{ v.title }}</div>
          <div class="meta">{{ shortDate(v.published) }}</div>
        </button>
      </div>

      <div ref="sentinel" class="sentinel">
        <span v-if="loading">Loading…</span>
        <span v-else-if="done && total > 0" class="muted">{{ total }} video{{ total === 1 ? '' : 's' }}</span>
      </div>
    </div>

    <VideoDetail
      v-if="selected" :video="selected"
      @close="selected = null" @deleted="onDeleted" />
  </div>
</template>

<style scoped>
.library { display: grid; grid-template-columns: 210px 1fr; gap: 1.2rem; align-items: start; }
.rail { position: sticky; top: 1rem; display: flex; flex-direction: column; gap: 0.15rem; max-height: 82vh; overflow: auto; }
.ch {
  display: flex; align-items: center; gap: 0.5rem; width: 100%; text-align: left;
  background: none; border: 0; color: var(--fg); padding: 0.4rem 0.5rem; border-radius: 7px; cursor: pointer;
  font-size: 0.88rem;
}
.ch:hover { background: var(--panel); }
.ch.on { background: #14313f; color: var(--accent); }
.ch img { width: 22px; height: 22px; border-radius: 50%; object-fit: cover; flex: none; }
.ch .name { flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.ch .n { color: var(--muted); font-size: 0.78rem; font-variant-numeric: tabular-nums; }

.controls { display: flex; gap: 0.6rem; margin-bottom: 1rem; }
.controls input {
  flex: 1; background: var(--panel); border: 1px solid var(--border); color: var(--fg);
  border-radius: 9px; padding: 0.55rem 0.8rem; font-size: 0.95rem;
}
.controls select {
  background: var(--panel); border: 1px solid var(--border); color: var(--fg);
  border-radius: 9px; padding: 0 0.6rem; font-size: 0.9rem;
}

.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 0.9rem; }
.card {
  background: none; border: 0; padding: 0; cursor: pointer; text-align: left; color: var(--fg);
  content-visibility: auto; contain-intrinsic-size: 180px; /* cheap offscreen skipping for 10k rows */
}
.thumb {
  position: relative; aspect-ratio: 16 / 9; background: #0c0c0f; border-radius: 9px; overflow: hidden;
  border: 1px solid var(--border);
}
.thumb img { width: 100%; height: 100%; object-fit: cover; display: block; }
.card:hover .thumb { border-color: var(--accent); }
.dur {
  position: absolute; right: 5px; bottom: 5px; background: rgba(0,0,0,0.8); color: #fff;
  font-size: 0.72rem; padding: 0.05rem 0.3rem; border-radius: 4px; font-variant-numeric: tabular-nums;
}
.title {
  font-size: 0.88rem; font-weight: 600; margin-top: 0.4rem; line-height: 1.3;
  display: -webkit-box; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden;
}
.meta { font-size: 0.78rem; color: var(--muted); margin-top: 0.15rem; }
.sentinel { text-align: center; color: var(--muted); padding: 1.4rem 0; font-size: 0.85rem; min-height: 2rem; }
.empty { color: var(--fg); padding: 2rem 0; }
.empty .muted { color: var(--muted); }
.muted { color: var(--muted); }

@media (max-width: 640px) {
  .library { grid-template-columns: 1fr; }
  .rail { flex-direction: row; position: static; overflow-x: auto; max-height: none; }
  .ch { width: auto; }
  .ch .name { max-width: 8rem; }
}
</style>
