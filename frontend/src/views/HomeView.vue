<script setup lang="ts">
import { computed } from 'vue'
import { useQueue } from '../stores/queue'
import { go } from '../router'
import { pct } from '../format'
import Omnibox from '../components/Omnibox.vue'
import QueueCard from '../components/QueueCard.vue'

const queue = useQueue()
const hasActive = computed(() => queue.active.length > 0)
// Home is a dashboard; the server caps the active list. Use live stats for the true count.
const queueCount = computed(() => Math.max(queue.active.length, queue.stats.queued + queue.stats.running))
const hiddenCount = computed(() => Math.max(0, queueCount.value - queue.active.length))
</script>

<template>
  <Omnibox />

  <main>
    <section v-if="hasActive">
      <h2>
        Queue <span class="count">{{ queueCount }}</span>
        <button class="manage" @click="go('queue')">Manage →</button>
      </h2>
      <QueueCard v-for="j in queue.active" :key="j.id" :job="j" />
      <button v-if="hiddenCount > 0" class="more" @click="go('queue')">
        + {{ hiddenCount }} more in the queue — manage all →
      </button>
    </section>

    <section v-else-if="queue.loaded && queue.recent.length === 0 && queue.failed.length === 0" class="empty">
      <p>Nothing in the queue.</p>
      <p class="muted">Paste a URL above to archive your first video.</p>
    </section>

    <section v-if="queue.failed.length" class="failed-section">
      <h2>Failed <span class="count danger">{{ queue.failed.length }}</span></h2>
      <QueueCard v-for="j in queue.failed" :key="j.id" :job="j" />
    </section>

    <section v-if="queue.recent.length">
      <h2>Recently done</h2>
      <QueueCard v-for="j in queue.recent" :key="j.id" :job="j" />
    </section>
  </main>

  <footer v-if="queue.loaded">
    <span>{{ queue.stats.queued }} queued</span>
    <span>{{ queue.stats.running }} active</span>
    <span>{{ queue.stats.done }} done</span>
    <span v-if="queue.stats.failed">· {{ queue.stats.failed }} failed</span>
    <span class="agg" v-if="queue.downloading.length">· {{ pct(queue.aggregatePct) }}</span>
  </footer>
</template>

<style scoped>
h2 { font-size: 0.95rem; text-transform: uppercase; letter-spacing: 0.05em; color: var(--muted);
     margin: 1.6rem 0 0.8rem; display: flex; align-items: center; gap: 0.5rem; }
.count { background: #24303a; color: var(--fg); border-radius: 20px; padding: 0.05rem 0.55rem; font-size: 0.8rem; }
.count.danger { background: #3a1a1a; color: var(--danger); }
.manage { margin-left: auto; background: none; border: 0; color: var(--accent); cursor: pointer;
          font-size: 0.82rem; text-transform: none; letter-spacing: 0; }
.more { width: 100%; background: var(--panel); border: 1px dashed var(--border); color: var(--accent);
        border-radius: 9px; padding: 0.6rem; cursor: pointer; font-size: 0.88rem; margin-top: 0.2rem; }
.more:hover { background: #14313f; }
.empty { text-align: center; color: var(--muted); padding: 3rem 0; }
.empty .muted { font-size: 0.9rem; margin-top: 0.3rem; }
footer { display: flex; gap: 0.8rem; margin-top: 2rem; color: var(--muted); font-size: 0.85rem;
         border-top: 1px solid var(--border); padding-top: 1rem; }
.agg { color: var(--accent); }
</style>
