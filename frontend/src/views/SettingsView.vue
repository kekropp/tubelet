<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import type {
  NetworkSettings, QualitySettings, SbSettings, MaintenanceSettings,
  CookieStatus, SystemInfo,
} from '../types'
import { FORMAT_PRESETS } from '../types'
import { api } from '../api'
import { bytes, relTime } from '../format'

type Tab = 'network' | 'cookies' | 'quality' | 'sponsorblock' | 'storage' | 'maintenance'
const TABS: { id: Tab; label: string }[] = [
  { id: 'network', label: 'Network' },
  { id: 'cookies', label: 'Cookies' },
  { id: 'quality', label: 'Quality' },
  { id: 'sponsorblock', label: 'SponsorBlock' },
  { id: 'storage', label: 'Storage' },
  { id: 'maintenance', label: 'Maintenance' },
]
const tab = ref<Tab>('network')

// A transient "Saved ✓" flash per section.
const saved = reactive<Record<string, boolean>>({})
function flash(k: string) { saved[k] = true; setTimeout(() => (saved[k] = false), 1800) }

// ---- network -----------------------------------------------------------
const net = reactive<NetworkSettings>({})
async function saveNet() { await api.putSettings('network', clean(net)); flash('network') }

// ---- quality -----------------------------------------------------------
const quality = reactive<QualitySettings>({ profile: 'compat', hwaccel: 'auto', format_preset: 'directplay' })
async function saveQuality() { await api.putSettings('quality', clean(quality)); flash('quality') }

// ---- sponsorblock ------------------------------------------------------
const SB_CATEGORIES = ['sponsor', 'selfpromo', 'interaction', 'intro', 'outro', 'preview', 'filler', 'music_offtopic']
const SEGMENT_TYPES = ['Commercial', 'Intro', 'Outro', 'Preview', 'Recap', 'Unknown']
const DEFAULT_CATEGORIES = ['sponsor', 'selfpromo', 'interaction', 'intro', 'outro', 'preview', 'music_offtopic']
const DEFAULT_MAPPING: Record<string, string> = {
  sponsor: 'Commercial', intro: 'Intro', outro: 'Outro', preview: 'Preview',
  filler: 'Preview', interaction: 'Recap', selfpromo: 'Recap',
}
const sbCats = ref<Set<string>>(new Set(DEFAULT_CATEGORIES))
const sbMap = reactive<Record<string, string>>({ ...DEFAULT_MAPPING })
function toggleCat(c: string) { sbCats.value.has(c) ? sbCats.value.delete(c) : sbCats.value.add(c); sbCats.value = new Set(sbCats.value) }
async function saveSb() {
  const body: SbSettings = { categories: [...sbCats.value], mapping: { ...sbMap } }
  await api.putSettings('sponsorblock', body); flash('sponsorblock')
}

// ---- maintenance -------------------------------------------------------
const maint = reactive<MaintenanceSettings>({})
async function saveMaint() { await api.putSettings('maintenance', clean(maint)); flash('maintenance') }

const updating = ref(false)
const updateMsg = ref('')
async function updateYtdlp() {
  updating.value = true; updateMsg.value = ''
  try {
    const r = await api.updateYtdlp()
    updateMsg.value = r.ok ? `Updated to ${r.version}` : `Failed: ${r.error}`
    if (r.ok && sys.value) sys.value.ytdlp_version = r.version
  } finally { updating.value = false }
}

const backingUp = ref(false)
const backupMsg = ref('')
async function backupNow() {
  backingUp.value = true; backupMsg.value = ''
  try {
    const r = await api.backupNow()
    backupMsg.value = r.ok ? `Saved ${r.file} (${bytes(r.bytes)})` : `Failed: ${r.error}`
  } finally { backingUp.value = false }
}

// ---- cookies -----------------------------------------------------------
const cookie = ref<CookieStatus | null>(null)
const cookieText = ref('')
const cookieBusy = ref(false)
const cookieErr = ref('')

async function loadCookies() { cookie.value = await api.cookies().catch(() => null) }
async function uploadCookies() {
  cookieErr.value = ''
  if (!cookieText.value.trim()) { cookieErr.value = 'Paste a cookies.txt or choose a file.'; return }
  cookieBusy.value = true
  try {
    cookie.value = await api.uploadCookies(cookieText.value)
    cookieText.value = ''
  } catch (e) { cookieErr.value = 'Upload rejected — not a valid Netscape cookies.txt.' }
  finally { cookieBusy.value = false }
}
async function onFile(e: Event) {
  const f = (e.target as HTMLInputElement).files?.[0]
  if (f) cookieText.value = await f.text()
}
async function validateCookies() {
  cookieBusy.value = true
  try { cookie.value = await api.validateCookies() } finally { cookieBusy.value = false }
}
async function deleteCookies() {
  if (!confirm('Remove the stored cookie jar? Downloads will run without cookies.')) return
  await api.deleteCookies(); await loadCookies()
}

// ---- system / storage --------------------------------------------------
const sys = ref<SystemInfo | null>(null)
function diskPct(d: { free_bytes: number; total_bytes: number } | null): number {
  if (!d || !d.total_bytes) return 0
  return Math.round(((d.total_bytes - d.free_bytes) / d.total_bytes) * 100)
}

onMounted(async () => {
  const [n, q, sb, m] = await Promise.all([
    api.getSettings<NetworkSettings>('network').catch(() => ({}) as NetworkSettings),
    api.getSettings<QualitySettings>('quality').catch(() => ({}) as QualitySettings),
    api.getSettings<SbSettings>('sponsorblock').catch(() => ({}) as SbSettings),
    api.getSettings<MaintenanceSettings>('maintenance').catch(() => ({}) as MaintenanceSettings),
  ])
  Object.assign(net, n)
  Object.assign(quality, { profile: 'compat', hwaccel: 'auto', format_preset: 'directplay', ...q })
  if (sb.categories?.length) sbCats.value = new Set(sb.categories)
  if (sb.mapping) Object.assign(sbMap, sb.mapping)
  Object.assign(maint, m)
  await Promise.all([loadCookies(), api.system().then(s => (sys.value = s)).catch(() => {})])
})

// Drop empty-string / null keys so unset fields fall back to backend defaults.
function clean<T extends Record<string, unknown>>(o: T): Partial<T> {
  const out: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(o)) if (v !== '' && v != null) out[k] = v
  return out as Partial<T>
}
</script>

<template>
  <div class="settings">
    <nav class="tabs">
      <button v-for="t in TABS" :key="t.id" :class="{ on: tab === t.id }" @click="tab = t.id">{{ t.label }}</button>
    </nav>

    <!-- NETWORK -->
    <section v-if="tab === 'network'" class="panel">
      <p class="hint">Rate limits keep Tubelet a polite bot. Changes apply live — no restart.</p>
      <div class="grid">
        <label>Ops / hour (subscriptions)
          <input v-model.number="net.ops_per_hour" type="number" min="1" placeholder="30" /></label>
        <label>Download workers
          <input v-model.number="net.download_workers" type="number" min="1" max="16" placeholder="2" /></label>
        <label>Concurrent fragments
          <input v-model.number="net.concurrent_fragments" type="number" min="1" max="16" placeholder="4" /></label>
        <label>Sleep between requests (s)
          <input v-model.number="net.sleep_requests" type="number" min="0" placeholder="1" /></label>
        <label>Sleep interval (s)
          <input v-model.number="net.sleep_interval" type="number" min="0" placeholder="3" /></label>
        <label>Max sleep interval (s)
          <input v-model.number="net.max_sleep_interval" type="number" min="0" placeholder="8" /></label>
        <label>Rate limit (e.g. 2M)
          <input v-model="net.limit_rate" type="text" placeholder="off" /></label>
      </div>
      <div class="actions"><button class="save" @click="saveNet">Save</button>
        <span v-if="saved.network" class="ok">Saved ✓</span></div>
    </section>

    <!-- COOKIES -->
    <section v-if="tab === 'cookies'" class="panel">
      <p class="hint">Upload a Netscape <code>cookies.txt</code> to download age-gated / members / private-to-you
        videos and to reduce bot checks. The jar is stored write-only (0600) and never shown back.</p>

      <div class="status" :class="{ good: cookie?.valid, bad: cookie?.present && !cookie?.valid }">
        <template v-if="cookie?.present">
          <strong>{{ cookie.valid ? 'Valid' : 'Not working' }}</strong>
          <span v-if="cookie.identity">· {{ cookie.identity }}</span>
          <span v-if="cookie.uploaded_at">· uploaded {{ relTime(cookie.uploaded_at) }}</span>
          <span v-if="cookie.validated_at">· checked {{ relTime(cookie.validated_at) }}</span>
          <div v-if="cookie.message && !cookie.valid" class="msg">{{ cookie.message }}</div>
        </template>
        <template v-else><span class="muted">No cookie jar uploaded.</span></template>
      </div>

      <textarea v-model="cookieText" rows="5" placeholder="# Netscape HTTP Cookie File …" spellcheck="false"></textarea>
      <div class="actions">
        <input type="file" accept=".txt" @change="onFile" aria-label="Cookie file" />
        <button class="save" :disabled="cookieBusy" @click="uploadCookies">Upload &amp; validate</button>
        <button v-if="cookie?.present" :disabled="cookieBusy" @click="validateCookies">Re-validate</button>
        <button v-if="cookie?.present" class="danger" :disabled="cookieBusy" @click="deleteCookies">Remove</button>
      </div>
      <p v-if="cookieErr" class="err">{{ cookieErr }}</p>
    </section>

    <!-- QUALITY -->
    <section v-if="tab === 'quality'" class="panel">
      <p class="hint">Downloads prefer h264+aac mp4 (direct-play). This controls what happens when only
        vp9/av1 is available, plus hardware transcode and optional extras.</p>
      <label class="row">Profile
        <select v-model="quality.profile">
          <option value="compat">compat — transcode incompatible streams to h264</option>
          <option value="quality">quality — remux only, rely on client direct-play</option>
        </select>
      </label>
      <label class="row">Download quality
        <select v-model="quality.format_preset">
          <option v-for="p in FORMAT_PRESETS" :key="p.v" :value="p.v">{{ p.label }}</option>
        </select>
      </label>
      <label v-if="quality.format_preset === 'custom'" class="row">Custom <code>-f</code> format string
        <input v-model="quality.custom_format" type="text" spellcheck="false"
               placeholder="bestvideo[height<=1440]+bestaudio/best" />
      </label>
      <label class="row">Hardware transcode
        <select v-model="quality.hwaccel">
          <option value="auto">auto-detect (GPU if mapped, else CPU)</option>
          <option value="none">CPU only (libx264)</option>
          <option value="vaapi">VAAPI (/dev/dri)</option>
          <option value="qsv">Intel QSV</option>
          <option value="nvenc">NVIDIA NVENC</option>
        </select>
      </label>
      <label class="check"><input v-model="quality.embed_subs" type="checkbox" /> Write subtitle sidecars (.srt)</label>
      <label v-if="quality.embed_subs" class="row">Subtitle languages
        <input v-model="quality.sub_langs" type="text" placeholder="en.*" /></label>
      <label class="check"><input v-model="quality.embed_thumbnail" type="checkbox" /> Embed thumbnail in the file</label>
      <div class="actions"><button class="save" @click="saveQuality">Save</button>
        <span v-if="saved.quality" class="ok">Saved ✓</span></div>
    </section>

    <!-- SPONSORBLOCK -->
    <section v-if="tab === 'sponsorblock'" class="panel">
      <p class="hint">Fetched categories become Jellyfin media segments via the mapping below (applied
        server-side). Fetches use the k-anonymity endpoint — raw video ids are never sent.</p>
      <div class="cats">
        <label v-for="c in SB_CATEGORIES" :key="c" class="check">
          <input type="checkbox" :checked="sbCats.has(c)" @change="toggleCat(c)" /> {{ c }}
        </label>
      </div>
      <h4>Category → Jellyfin segment type</h4>
      <div class="grid map">
        <label v-for="c in SB_CATEGORIES" :key="c">{{ c }}
          <select v-model="sbMap[c]">
            <option v-for="t in SEGMENT_TYPES" :key="t" :value="t">{{ t }}</option>
          </select>
        </label>
      </div>
      <div class="actions"><button class="save" @click="saveSb">Save</button>
        <span v-if="saved.sponsorblock" class="ok">Saved ✓</span></div>
    </section>

    <!-- STORAGE -->
    <section v-if="tab === 'storage'" class="panel">
      <template v-if="sys">
        <div class="disk">
          <div class="disk-head"><span>Media (/youtube)</span>
            <span class="muted">{{ bytes(sys.media?.free_bytes) }} free of {{ bytes(sys.media?.total_bytes) }}</span></div>
          <div class="bar"><div :style="{ width: diskPct(sys.media) + '%' }"></div></div>
        </div>
        <div class="disk">
          <div class="disk-head"><span>Cache (/cache)</span>
            <span class="muted">{{ bytes(sys.cache?.free_bytes) }} free of {{ bytes(sys.cache?.total_bytes) }}</span></div>
          <div class="bar"><div :style="{ width: diskPct(sys.cache) + '%' }"></div></div>
        </div>
        <div class="counts">
          <div><b>{{ sys.video_count }}</b> videos</div>
          <div><b>{{ sys.channel_count }}</b> channels</div>
          <div><b>{{ sys.queue.queued }}</b> queued</div>
          <div><b>{{ sys.queue.failed }}</b> failed</div>
        </div>
      </template>
      <p v-else class="muted">Loading…</p>
    </section>

    <!-- MAINTENANCE -->
    <section v-if="tab === 'maintenance'" class="panel">
      <div class="tool">
        <div>
          <strong>yt-dlp</strong>
          <span class="muted"> · {{ sys?.ytdlp_version || 'not installed' }}</span>
        </div>
        <button :disabled="updating" @click="updateYtdlp">{{ updating ? 'Updating…' : 'Update now' }}</button>
      </div>
      <p v-if="updateMsg" class="flashline">{{ updateMsg }}</p>

      <div class="tool">
        <div><strong>Database backup</strong><span class="muted"> · VACUUM INTO /cache/backup</span></div>
        <button :disabled="backingUp" @click="backupNow">{{ backingUp ? 'Backing up…' : 'Back up now' }}</button>
      </div>
      <p v-if="backupMsg" class="flashline">{{ backupMsg }}</p>

      <h4>Schedules &amp; policy</h4>
      <div class="grid">
        <label>SB refresh cron<input v-model="maint.sb_refresh_cron" type="text" placeholder="0 4 * * 0" /></label>
        <label>yt-dlp update cron<input v-model="maint.ytdlp_update_cron" type="text" placeholder="0 5 * * 1" /></label>
        <label>Janitor cron<input v-model="maint.janitor_cron" type="text" placeholder="0 3 * * *" /></label>
        <label>Orphan .part TTL (days)<input v-model.number="maint.part_ttl_days" type="number" min="1" placeholder="7" /></label>
        <label>Backups to keep<input v-model.number="maint.backup_keep" type="number" min="1" placeholder="7" /></label>
      </div>
      <label class="check"><input v-model="maint.backup_enabled" type="checkbox" /> Nightly database backup</label>
      <label class="check"><input v-model="maint.ytdlp_autoupdate" type="checkbox" /> Auto-update yt-dlp (cron + on repeated breakage)</label>
      <label class="check"><input v-model="maint.po_token_enabled" type="checkbox" /> PO-token provider passthrough (advanced)</label>
      <div class="actions"><button class="save" @click="saveMaint">Save</button>
        <span v-if="saved.maintenance" class="ok">Saved ✓</span></div>
    </section>
  </div>
</template>

<style scoped>
.settings { display: flex; flex-direction: column; gap: 1rem; }
.tabs { display: flex; gap: 0.2rem; flex-wrap: wrap; border-bottom: 1px solid var(--border); padding-bottom: 0.5rem; }
.tabs button {
  background: none; border: 0; color: var(--muted); font-size: 0.9rem; cursor: pointer;
  padding: 0.4rem 0.7rem; border-radius: 7px;
}
.tabs button:hover { color: var(--fg); background: var(--panel); }
.tabs button.on { color: var(--accent); background: #14313f; }

.panel { background: var(--panel); border: 1px solid var(--border); border-radius: 12px; padding: 1.1rem 1.2rem; }
.hint { color: var(--muted); font-size: 0.86rem; margin: 0 0 1rem; }
.hint code { background: var(--bg); padding: 0.1rem 0.3rem; border-radius: 4px; }

.grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 0.7rem; }
.grid.map { grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); }
label { display: flex; flex-direction: column; gap: 0.3rem; font-size: 0.8rem; color: var(--muted); }
label.row { max-width: 460px; margin-bottom: 0.7rem; }
input, select, textarea {
  background: var(--bg); border: 1px solid var(--border); color: var(--fg); border-radius: 8px;
  padding: 0.45rem 0.55rem; font-size: 0.9rem; width: 100%;
}
textarea { font-family: ui-monospace, monospace; font-size: 0.8rem; resize: vertical; }
.check { flex-direction: row; align-items: center; gap: 0.5rem; color: var(--fg); font-size: 0.9rem; margin: 0.4rem 0; }
.check input { width: auto; }

.actions { display: flex; align-items: center; gap: 0.7rem; margin-top: 1.1rem; flex-wrap: wrap; }
.actions input[type=file] { width: auto; border: 0; padding: 0; font-size: 0.82rem; color: var(--muted); }
button { border: 0; border-radius: 8px; padding: 0.5rem 0.9rem; cursor: pointer; font-size: 0.88rem;
  background: #24303a; color: var(--fg); }
button:hover:not(:disabled) { background: #2e3d49; }
button:disabled { opacity: 0.55; cursor: default; }
button.save { background: #14313f; color: var(--accent); border: 1px solid #1d4a5e; }
button.save:hover:not(:disabled) { background: #185066; }
button.danger { color: #ffb0b0; }
button.danger:hover:not(:disabled) { background: #4a2020; }
.ok { color: #4ad07a; font-size: 0.85rem; }
.err { color: var(--danger); font-size: 0.85rem; margin: 0.6rem 0 0; }
.flashline { color: var(--accent); font-size: 0.85rem; margin: 0.3rem 0 0.9rem; }
.muted { color: var(--muted); }
h4 { margin: 1.2rem 0 0.6rem; font-size: 0.92rem; }

.status { border-radius: 9px; padding: 0.6rem 0.8rem; margin-bottom: 0.9rem; font-size: 0.88rem;
  background: var(--bg); border: 1px solid var(--border); display: flex; flex-wrap: wrap; gap: 0.35rem; align-items: center; }
.status.good { border-color: #205a37; background: #10241a; }
.status.bad { border-color: #6a2020; background: #241010; }
.status .msg { flex-basis: 100%; color: var(--danger); font-size: 0.82rem; margin-top: 0.3rem; }

.cats { display: flex; flex-wrap: wrap; gap: 0.3rem 1rem; margin-bottom: 0.5rem; }
.tool { display: flex; align-items: center; justify-content: space-between; gap: 1rem;
  padding: 0.6rem 0; border-bottom: 1px solid var(--border); }
.disk { margin-bottom: 1rem; }
.disk-head { display: flex; justify-content: space-between; font-size: 0.86rem; margin-bottom: 0.35rem; }
.bar { height: 8px; background: var(--bg); border-radius: 5px; overflow: hidden; border: 1px solid var(--border); }
.bar div { height: 100%; background: var(--accent); }
.counts { display: flex; gap: 1.5rem; margin-top: 0.5rem; font-size: 0.9rem; color: var(--muted); }
.counts b { color: var(--fg); }
</style>
