<script setup lang="ts">
import { onMounted, onUnmounted, watch } from 'vue'
import { useQueue } from './stores/queue'
import { connectHub } from './signalr'
import { route, go, type Route } from './router'
import HomeView from './views/HomeView.vue'
import LibraryView from './views/LibraryView.vue'
import ChannelsView from './views/ChannelsView.vue'
import SettingsView from './views/SettingsView.vue'

const queue = useQueue()
let hub: { stop: () => void } | null = null

onMounted(() => { hub = connectHub(queue) })
onUnmounted(() => hub?.stop())

// Tab title reflects live queue progress (⬇ N · pct%).
watch(
  () => [queue.downloading.length, Math.round(queue.aggregatePct * 100)] as const,
  ([n, p]) => { document.title = n > 0 ? `⬇ ${n} · ${p}% — Tubelet` : 'Tubelet' },
  { immediate: true },
)

const views = { home: HomeView, library: LibraryView, channels: ChannelsView, settings: SettingsView }
const nav: { r: Route; label: string }[] = [
  { r: 'home', label: 'Home' },
  { r: 'library', label: 'Library' },
  { r: 'channels', label: 'Channels' },
  { r: 'settings', label: 'Settings' },
]
</script>

<template>
  <div class="app">
    <header>
      <h1 @click="go('home')">Tubelet</h1>
      <nav>
        <button v-for="n in nav" :key="n.r" :class="{ on: route === n.r }" @click="go(n.r)">{{ n.label }}</button>
      </nav>
      <span class="spacer"></span>
      <span class="conn" :class="{ up: queue.connected }" :title="queue.connected ? 'Live' : 'Reconnecting…'"></span>
    </header>

    <div v-if="queue.banner" class="banner" @click="queue.setBanner(null)">
      {{ queue.banner.message }} <span class="dismiss">✕</span>
    </div>

    <div v-if="queue.scan" class="scan">
      {{ queue.scan.message || `Scanning ${queue.scan.target}…` }}
      <span v-if="!queue.scan.done">({{ queue.scan.enqueued }} queued)</span>
    </div>

    <component :is="views[route]" />
  </div>
</template>

<style scoped>
.app { max-width: 980px; margin: 0 auto; padding: 1.5rem 1.25rem 4rem; }
header { display: flex; align-items: center; gap: 1rem; margin-bottom: 1.5rem; }
h1 { font-size: 1.35rem; margin: 0; letter-spacing: -0.02em; cursor: pointer; }
h1::before { content: '▶ '; color: var(--accent); }
nav { display: flex; gap: 0.2rem; }
nav button {
  background: none; border: 0; color: var(--muted); font-size: 0.92rem; cursor: pointer;
  padding: 0.35rem 0.7rem; border-radius: 7px;
}
nav button:hover { color: var(--fg); background: var(--panel); }
nav button.on { color: var(--accent); background: #14313f; }
.spacer { flex: 1; }
.conn { width: 9px; height: 9px; border-radius: 50%; background: var(--danger); transition: background 0.3s; flex: none; }
.conn.up { background: #4ad07a; }
.banner {
  background: #3a2e12; border: 1px solid #6a5320; color: #ffd98a; border-radius: 8px;
  padding: 0.7rem 0.9rem; margin-bottom: 1rem; cursor: pointer; font-size: 0.9rem;
  display: flex; justify-content: space-between; align-items: center;
}
.dismiss { opacity: 0.6; }
.scan {
  background: #12313f; border: 1px solid #1d4a5e; color: var(--accent); border-radius: 8px;
  padding: 0.6rem 0.9rem; margin-bottom: 1rem; font-size: 0.9rem;
}
</style>
