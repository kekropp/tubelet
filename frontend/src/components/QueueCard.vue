<script setup lang="ts">
import { ref, computed } from 'vue'
import type { Job } from '../types'
import { useQueue } from '../stores/queue'
import { pct, stateLabel, relTime } from '../format'

const props = defineProps<{ job: Job }>()
const queue = useQueue()
const showError = ref(false)

const live = computed(() => queue.live[props.job.id])
const progress = computed(() => live.value?.pct ?? props.job.progress)
const isActive = computed(() =>
  ['queued', 'fetching_meta', 'downloading', 'converting', 'indexing', 'paused'].includes(props.job.state))
const busy = computed(() =>
  ['fetching_meta', 'downloading', 'converting', 'indexing'].includes(props.job.state))
const retryAt = computed(() =>
  props.job.next_retry && props.job.next_retry * 1000 > Date.now()
    ? new Date(props.job.next_retry * 1000).toLocaleTimeString() : null)

function hideThumb(e: Event) { (e.target as HTMLImageElement).style.visibility = 'hidden' }
</script>

<template>
  <div class="card" :class="job.state">
    <div class="thumb">
      <img v-if="job.thumb" :src="job.thumb" alt="" loading="lazy" @error="hideThumb" />
    </div>

    <div class="body">
      <div class="title-row">
        <span class="title">{{ job.title || job.youtube_id }}</span>
        <span class="badge" :class="job.state">{{ stateLabel(job.state) }}</span>
        <span v-if="job.priority === 1" class="prio" title="User paste — jumps the queue">★</span>
      </div>

      <div v-if="busy" class="bar">
        <div class="fill" :style="{ width: pct(progress) }" :class="{ indeterminate: !live && job.state !== 'downloading' }"></div>
      </div>

      <div class="meta">
        <template v-if="job.state === 'downloading' && live">
          <span>{{ pct(progress) }}</span>
          <span v-if="live.speed">· {{ live.speed }}</span>
          <span v-if="live.eta">· {{ live.eta }} left</span>
        </template>
        <template v-else-if="job.state === 'done'">
          <span>Finished {{ relTime(job.finished_at) }}</span>
        </template>
        <template v-else-if="job.state === 'failed'">
          <span class="err-kind">{{ job.error_kind || 'error' }}</span>
          <button class="link" @click="showError = !showError">
            {{ showError ? 'hide' : 'details' }}
          </button>
        </template>
        <template v-else-if="retryAt">
          <span>Retry at {{ retryAt }} · attempt {{ job.attempts }}/{{ job.max_attempts }}</span>
        </template>
        <template v-else>
          <span>{{ stateLabel(job.state) }}</span>
        </template>
      </div>

      <pre v-if="job.state === 'failed' && showError" class="stderr">{{ job.last_error }}</pre>
    </div>

    <div class="actions">
      <button v-if="job.state === 'failed'" @click="queue.retry(job.id)">Retry</button>
      <button v-if="isActive && job.priority !== 1" title="Bump priority" @click="queue.priority(job.id, 1)">▲</button>
      <button v-if="isActive" class="danger" @click="queue.cancel(job.id)">Cancel</button>
    </div>
  </div>
</template>

<style scoped>
.card {
  display: grid; grid-template-columns: 96px 1fr auto; gap: 0.85rem; align-items: center;
  background: var(--panel); border: 1px solid var(--border); border-radius: 10px;
  padding: 0.6rem; margin-bottom: 0.6rem;
}
.card.failed { border-color: #4a2020; }
.thumb {
  width: 96px; height: 54px; border-radius: 6px; overflow: hidden;
  background: #0c0c0f; display: flex; align-items: center; justify-content: center;
}
.thumb img { width: 100%; height: 100%; object-fit: cover; }
.body { min-width: 0; }
.title-row { display: flex; align-items: center; gap: 0.5rem; }
.title { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.badge {
  font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.03em;
  padding: 0.12rem 0.4rem; border-radius: 5px; background: #24303a; color: var(--muted); flex: none;
}
.badge.downloading, .badge.converting, .badge.indexing, .badge.fetching_meta { background: #14313f; color: var(--accent); }
.badge.done { background: #123528; color: #6fe0a8; }
.badge.failed { background: #3a1a1a; color: var(--danger); }
.prio { color: var(--accent); flex: none; }
.bar { height: 6px; background: #0c0c0f; border-radius: 4px; overflow: hidden; margin: 0.45rem 0; }
.fill { height: 100%; background: var(--accent); transition: width 0.25s ease; }
.fill.indeterminate { width: 40% !important; animation: slide 1.2s ease-in-out infinite; }
@keyframes slide { 0% { margin-left: -40%; } 100% { margin-left: 100%; } }
.meta { font-size: 0.85rem; color: var(--muted); display: flex; gap: 0.4rem; align-items: center; }
.err-kind { color: var(--danger); text-transform: capitalize; }
.stderr {
  margin: 0.5rem 0 0; padding: 0.5rem; background: #0c0c0f; border-radius: 6px;
  font-size: 0.78rem; color: #e0a0a0; white-space: pre-wrap; word-break: break-word; max-height: 8rem; overflow: auto;
}
.actions { display: flex; gap: 0.35rem; align-items: center; }
.actions button {
  background: #24303a; color: var(--fg); border: 0; border-radius: 6px;
  padding: 0.35rem 0.6rem; cursor: pointer; font-size: 0.85rem;
}
.actions button:hover { background: #2e3d49; }
.actions button.danger:hover { background: #4a2020; color: #ffb0b0; }
.link { background: none !important; color: var(--accent) !important; padding: 0 !important; text-decoration: underline; }
</style>
