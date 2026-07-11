# Tubelet — fast, minimal YouTube archival for Jellyfin

A single-container YouTube archiver. Paste a URL, get a perfectly organized Jellyfin
library. Jellyfin integration is a **first-party plugin we ship ourselves** —
metadata, images, and SponsorBlock media segments in one assembly, talking to a
clean batch API. No Elasticsearch, no Redis, no Celery:
one .NET 10 process, one SQLite file, yt-dlp + ffmpeg as subprocesses.

```
Goals:     speed, usability, 1 container, first-party Jellyfin plugin
Non-goals: auth/multi-user, comment scraping, transcoding server, other sites,
           third-party plugin compatibility, any playback (Jellyfin plays &
           tracks watched state — Tubelet only archives & manages the library)
```

---

## 1. Jellyfin integration ("Tubelet for Jellyfin" plugin)

One plugin, one config field (server URL), everything included. We own both sides
of the wire, so the API is designed for exactly two things: **batch** and **delta**.

### 1.1 Filesystem layout

```
/youtube/                                   ← Jellyfin library root ("Shows" type)
  UC2C_jShtL725hvbm1arSV9w/                 ← folder = channel_id (series)
    dQw4w9WgXcQ.mp4                         ← filename stem = video_id (episode)
```

- IDs are derivable from paths alone (filename stem = video id, parent dir =
  channel id) — the plugin bootstraps identification from paths, then persists
  `ProviderIds["Tubelet"]` on items so later renames/moves can't break matching.
- Files are complete and playable before they ever appear here (§4).

### 1.2 Plugin assembly — one dll, three roles

```
Jellyfin.Plugin.Tubelet  (targets current stable Jellyfin ABI, 10.11)
├── Metadata   IRemoteMetadataProvider<Series>/<Episode> + image providers:
│              title, clean multi-line description, real premiere timestamp,
│              year seasons, tags, studios, chapter markers from YouTube chapters,
│              episode thumb / channel poster / banner / backdrop
├── Segments   IMediaSegmentProvider: SponsorBlock categories → Jellyfin
│              MediaSegmentType (sponsor→Commercial, intro→Intro, outro→Outro,
│              preview/filler→Preview, interaction/selfpromo→Recap; mapping
│              configurable server-side, delivered pre-mapped to the plugin)
└── Sync       one scheduled task: cursor-based delta sync — refresh changed
               video metadata (edited titles, freshly fetched SponsorBlock
               segments) and mirror Tubelet playlists → Jellyfin collections.
               No playback/watched sync (Jellyfin owns that). No library walks.
```

### 1.3 Plugin API (`/api/jf/v1`) — batch + delta, nothing else

```
GET  /api/jf/v1/videos?ids=a,b,c,…       → up to 500 full video docs per call
GET  /api/jf/v1/channels?ids=…           → batched channel docs
GET  /api/jf/v1/changes?since=<cursor>   → {videos, playlists, next_cursor}; 204 if none
GET  /cache/*                            → images (ETag + immutable cache headers)
```

Video doc (System.Text.Json source-gen, same DTO the frontend uses):

```jsonc
{
  "id": "dQw4w9WgXcQ",
  "channel_id": "UCuAXFkgsw1L7xaCfnd5JJOw",
  "title": "Never Gonna Give You Up",
  "description": "…",                        // raw, multi-line; plugin handles layout
  "published": "2009-10-25T06:57:33Z",       // full timestamp, not just a date
  "duration_s": 213,
  "tags": ["music"],
  "thumb": "/cache/videos/d/dQw4w9WgXcQ.jpg",
  "chapters": [ { "title": "Intro", "start_s": 0.0 } ],
  "segments": [                              // already filtered + mapped server-side
    { "type": "Commercial", "start_s": 12.34, "end_s": 56.78 }
  ]
}
```

A full metadata refresh of a 10k-video library is ~20 batched round-trips plus
cached image fetches; steady state is one `changes` poll every few minutes,
answered with `204` when idle.

### 1.4 Distribution: the container is the plugin repo

Tubelet serves a Jellyfin plugin repository at `http://tubelet:8000/repo/manifest.json`
(static manifest + versioned zips baked into the image). Setup: add repo URL in
Jellyfin → install "Tubelet" → set server URL → done. Updates ride Jellyfin's own
plugin-update mechanism, and the manifest only offers plugin builds compatible with
the running server, so server/plugin version drift can't happen.

Compat policy: target current stable Jellyfin; CI also builds against the next-major
preview so ABI churn is caught by us, not by users.

---

## 2. Architecture — one process, one file DB

```
┌────────────────────────────── tubelet (single container) ───────────────────────────────┐
│                                                                                          │
│  ASP.NET Core (.NET 10, Kestrel)                                                         │
│  ├── /            Vue 3 SPA (static, embedded wwwroot)                                   │
│  ├── /api/v1/*    REST API (frontend CRUD)                                               │
│  ├── /api/jf/v1/* Jellyfin plugin API (batch + delta, §1.3)                              │
│  ├── /repo/*      Jellyfin plugin repository (manifest + zips)                           │
│  ├── /cache/*     Thumbnails/art (static files + ETag)                                   │
│  ├── /media/*     Video files (range requests, in-browser preview)                       │
│  └── /hub         SignalR (progress, queue, toasts)                                      │
│                                                                                          │
│  Hosted services (same process):                                                         │
│  ├── DownloadCoordinator   – pulls jobs from SQLite queue → yt-dlp subprocess            │
│  ├── PostProcessor         – ffmpeg remux/convert, thumbnail, SponsorBlock fetch, index  │
│  ├── Scheduler             – cron subscriptions, SB refresh, yt-dlp self-update          │
│  └── Janitor               – orphan .part cleanup, DB vacuum/analyze, log rotation       │
│                                                                                          │
│  SQLite (WAL) ── single db file: catalog + queue + settings + FTS5 search                │
│  Subprocesses: yt-dlp, ffmpeg/ffprobe (spawned per job, never long-lived)                │
└──────────────────────────────────────────────────────────────────────────────────────────┘
Volumes:  /youtube (media, = Jellyfin library)   /cache (db, thumbs, cookies, .part files)
```

Why this is fast:

- **No network hops.** Kestrel → SQLite (microseconds, same process, memory-mapped);
  no Django → Redis → Celery → Elasticsearch chain.
- **SQLite in WAL mode** with `synchronous=NORMAL`, prepared statements via Dapper.
  Reads never block writes; 100k videos ≈ a ~200 MB db — trivially fast.
- **FTS5** replaces a search server (title/description/channel/tags, prefix +
  porter stemming). Sub-millisecond at this scale.
- **SQLite replaces a broker** as the job queue: a `jobs` table + an in-process
  `Channel<long>` wake-up signal. Durable across restarts by construction.
- Static assets precompressed (brotli) at build; thumbnails immutable-cached;
  all JSON via System.Text.Json source generators (zero reflection).

### Tech choices

| Concern | Choice | Why |
|---|---|---|
| Web | ASP.NET Core minimal APIs, .NET 10 | fastest mainstream stack, AOT-friendly |
| DB access | `Microsoft.Data.Sqlite` + Dapper | no ORM overhead, explicit SQL, easy FTS5 |
| JSON | System.Text.Json source generators | zero-reflection, exact naming control |
| Realtime | SignalR (WebSockets, JSON protocol) | auto-reconnect for free |
| Scheduling | Cronos (cron parsing) + one timer loop | no Quartz/Hangfire dependency |
| Downloader | yt-dlp subprocess | JSON progress + infojson |
| Muxing | ffmpeg/ffprobe subprocess | remux/convert/verify |
| Frontend | Vue 3 + Vite + TS + Pinia + @microsoft/signalr | requested |
| JF plugin | net-current, Jellyfin 10.11 ABI | §1.2 |

---

## 3. Data model (SQLite)

```sql
PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;
PRAGMA busy_timeout=5000; PRAGMA cache_size=-64000; -- 64 MB page cache

CREATE TABLE channels (
  channel_id    TEXT PRIMARY KEY,          -- UC…
  name          TEXT NOT NULL,
  description   TEXT NOT NULL DEFAULT '',
  tags          TEXT NOT NULL DEFAULT '[]',-- JSON array
  thumb_path    TEXT, banner_path TEXT, tvart_path TEXT,
  last_refresh  INTEGER                    -- unixtime
);

CREATE TABLE videos (
  youtube_id    TEXT PRIMARY KEY,          -- 11 chars
  channel_id    TEXT NOT NULL REFERENCES channels,
  title         TEXT NOT NULL,
  description   TEXT NOT NULL DEFAULT '',
  published     TEXT NOT NULL,             -- ISO 8601 timestamp
  duration_s    INTEGER NOT NULL DEFAULT 0,
  tags          TEXT NOT NULL DEFAULT '[]',
  chapters      TEXT,                      -- JSON [{title,start_s}] from infojson
  media_path    TEXT NOT NULL,             -- youtube-root-relative: "<ch>/<id>.mp4"
  media_size    INTEGER, width INTEGER, height INTEGER,
  vcodec        TEXT, acodec TEXT,
  thumb_path    TEXT,
  segments      TEXT,                      -- JSON [{category,start_s,end_s}] or NULL
  sb_refreshed  INTEGER,
  downloaded_at INTEGER NOT NULL,
  changed_at    INTEGER NOT NULL,          -- monotonic cursor source for /changes
  info_json     TEXT                       -- gzip'd full yt-dlp infojson (re-index later)
);
CREATE INDEX ix_videos_channel ON videos(channel_id, published DESC);
CREATE INDEX ix_videos_changed ON videos(changed_at);

CREATE VIRTUAL TABLE videos_fts USING fts5(
  title, description, channel_name, tags,
  content='', tokenize='porter unicode61'
);  -- kept in sync by the indexer

CREATE TABLE playlists (
  playlist_id   TEXT PRIMARY KEY,          -- PL… or "TL-<ulid>" for custom
  name          TEXT NOT NULL,
  channel_id    TEXT, channel_name TEXT,
  description   TEXT NOT NULL DEFAULT '',
  type          TEXT NOT NULL DEFAULT 'regular',  -- regular|custom
  active        INTEGER NOT NULL DEFAULT 1,
  thumb_path    TEXT,
  changed_at    INTEGER NOT NULL
);
CREATE TABLE playlist_entries (
  playlist_id   TEXT NOT NULL REFERENCES playlists ON DELETE CASCADE,
  youtube_id    TEXT NOT NULL,
  idx           INTEGER NOT NULL,
  PRIMARY KEY (playlist_id, idx)
);

CREATE TABLE subscriptions (                -- things we watch for new content
  id            INTEGER PRIMARY KEY,
  kind          TEXT NOT NULL,              -- channel|playlist
  target_id     TEXT NOT NULL UNIQUE,       -- UC… / PL…
  cron          TEXT NOT NULL DEFAULT '0 */6 * * *',
  quality_prof  TEXT NOT NULL DEFAULT 'default',
  filter_json   TEXT,                       -- min duration, title regex, date floor, max items
  enabled       INTEGER NOT NULL DEFAULT 1,
  last_check    INTEGER, next_check INTEGER
);

CREATE TABLE jobs (                         -- durable download queue (no broker)
  id            INTEGER PRIMARY KEY,
  youtube_id    TEXT NOT NULL UNIQUE,
  channel_id    TEXT,
  title         TEXT,                       -- known pre-download (for UI)
  state         TEXT NOT NULL DEFAULT 'queued',
                -- queued|fetching_meta|downloading|converting|indexing|done|failed|paused
  priority      INTEGER NOT NULL DEFAULT 5, -- 1 = user paste (highest), 5 = subscription
  attempts      INTEGER NOT NULL DEFAULT 0,
  max_attempts  INTEGER NOT NULL DEFAULT 5,
  next_retry    INTEGER,                    -- unixtime; backoff gate
  last_error    TEXT, error_kind TEXT,      -- transient|permanent|throttled
  progress      REAL NOT NULL DEFAULT 0,    -- 0..1 (mirrored to SignalR, persisted coarsely)
  added_at      INTEGER NOT NULL, started_at INTEGER, finished_at INTEGER
);
CREATE INDEX ix_jobs_ready ON jobs(state, priority, next_retry, added_at);

CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
-- cookies state, rate limits, quality profiles, sb categories/mapping, yt-dlp channel…
```

Notes:

- `changed_at` on videos/playlists (bumped on any mutation — re-index, metadata
  edit, SponsorBlock refresh, playlist change) is what makes the plugin's
  `/changes?since=` cursor a single indexed range scan.
- `info_json` (gzip'd) means the catalog can be rebuilt/extended later without
  touching YouTube again.
- The queue is *the* jobs table; workers claim rows with
  `UPDATE … SET state='downloading' WHERE id = (SELECT … LIMIT 1) RETURNING *` —
  atomic in SQLite, no lock service needed.

---

## 4. Download pipeline

```
paste URL ──▶ classify ──▶ enqueue ──▶ [worker] meta ──▶ download ──▶ convert ──▶ index ──▶ done
                 │                                                                    │
                 └─ channel/playlist? expand to N video jobs (flat extraction)        └─ SignalR "video.added"
```

### 4.1 URL intake (usability core)

One omnibox. Paste anything: watch URL, short URL, shorts, live, playlist, channel
(`/@handle`, `/channel/UC…`, `/c/…`), or bare video ID. Classification is local
regex first (instant UI feedback: "Video", "Channel — subscribe or fetch-all?"),
then `yt-dlp --flat-playlist -J` resolves channels/playlists to concrete video IDs
(fast: no per-video pages). Already-archived IDs are skipped and reported.
User pastes get `priority=1` and jump the queue.

### 4.2 Worker execution (per job)

Concurrency: `N` download workers (default **2**) pulling from the queue, plus one
postprocess lane — download of job B overlaps convert/index of job A.

1. **fetching_meta** — `yt-dlp -J <id>` (single call, also writes `info_json`).
   Upserts channel + video rows early so the UI shows real title/thumb immediately.
2. **downloading** — one yt-dlp invocation per video:

   ```
   yt-dlp -f "bestvideo[vcodec^=avc1]+bestaudio[ext=m4a]/bestvideo*+bestaudio/best"
     --merge-output-format mp4
     --continue --part                      # RESUME: .part files survive restarts
     --concurrent-fragments 4               # SPEED: parallel fragment download
     --retries 10 --fragment-retries 10 --retry-sleep exp=1:120
     --limit-rate {cfg} --sleep-requests {cfg} --sleep-interval {cfg}
     --cookies /cache/cookies/cookies.txt   # if configured
     --progress-template "%(progress)j" --newline --no-colors
     -o "/cache/incomplete/%(id)s.%(ext)s" <id>
   ```

   Progress JSON lines are parsed off stdout → throttled to 4 Hz → SignalR
   `job.progress {id, pct, speed, eta}`. stderr is ring-buffered for error display.

   The `-f` selector above is the **directplay** preset (the default). Settings →
   Quality exposes a `format_preset` chooser — `directplay` | `best` (any codec,
   up to UHD) | `720p` (space saver) | `custom` (raw `-f` string in
   `custom_format`). A subscription's `quality_prof` overrides the global choice
   per channel/playlist (`default` = follow global, a preset key, or
   `custom:<-f string>`); it is stamped onto `jobs.format` at enqueue time and
   resolved at download time, so global changes apply to already-queued jobs
   that don't carry an override.
3. **converting** — see §4.4.
4. **indexing** — atomic move `incomplete/<id>.mp4` → `/youtube/<channel_id>/<id>.mp4`
   (same filesystem = rename, instant), thumbnail → `/cache/videos/<c>/<id>.jpg`,
   channel art if new channel, SponsorBlock fetch (§5), FTS row, `videos` upsert
   (bumps `changed_at` → plugin picks it up on next delta poll), job → `done`,
   broadcast.

Files land in the Jellyfin library **only after** they are complete and converted —
Jellyfin never sees a partial file.

### 4.3 Retries, resume, failure taxonomy

- **Resume within a download:** `--continue --part` + fragment retries; a restarted
  job reuses the `.part` file in `/cache/incomplete` and continues where it stopped.
- **Resume across restarts:** the queue is SQLite. On startup, any job stuck in
  `downloading|converting` (crash/kill) is reset to `queued` with attempts intact;
  its `.part` data is still there, so almost no work is lost.
- **Retry policy:** on failure classify from yt-dlp exit code + stderr:
  - `transient` (network, 5xx, timeout) → backoff `min(2^attempts, 60) min ± jitter`,
    up to `max_attempts`.
  - `throttled` (HTTP 429, "Sign in to confirm you're not a bot") → pause the
    **whole queue** for a cooldown (default 30 min), raise a UI banner suggesting
    cookies/PO-token, don't burn attempts.
  - `permanent` (private/deleted/geo-blocked/members-only) → `failed` immediately,
    no retries, reason shown in UI.
- **Dead jobs UI:** failed jobs stay listed with the last 30 lines of stderr,
  one-click *Retry* (resets attempts) / *Ignore*. Nothing fails silently.
- **Janitor:** `.part` files with no matching queued/failed job are deleted after 7 days.

### 4.4 Media conversion (Jellyfin-first policy)

Format selector already prefers **avc1 + m4a → mp4**, which direct-plays everywhere,
so the common case is *merge-only* (no transcode, seconds). Then a policy pass,
per quality-profile setting:

| Downloaded streams | `compat` profile (default) | `quality` profile |
|---|---|---|
| h264 + aac (mp4) | keep (ffprobe verify only) | keep |
| vp9/av1 + opus (webm; only option at >1080p or vp9-only uploads) | **transcode** → h264 (NVENC/QSV/VAAPI if a GPU is mapped, else libx264) + aac → mp4 | **remux** streams into mp4 (`-c copy`), rely on modern client direct-play |
| anything already mp4-compatible | `ffmpeg -c copy` remux | same |

Rules either way: output is always `<video_id>.mp4` (§1.1), `+faststart` moov atom,
ffprobe sanity check (duration within 2 s of metadata, decodable first frame)
before the file is moved into the library. Conversion runs in the postprocess lane
so it never blocks the download slots; hardware acceleration is auto-detected
(`/dev/dri` present → VAAPI/QSV, nvidia runtime → NVENC).

Optional extras (off by default): embed subtitles (`--embed-subs --sub-langs`),
embed thumbnail, write `.en.srt` sidecars for Jellyfin subtitle pickup.

---

## 5. Metadata & SponsorBlock

- **Source of truth** is the yt-dlp infojson → mapped once into `videos`/`channels`
  rows. The plugin API and the frontend API share the same source-gen DTOs — one
  serializer, no drift. No comment scraping — we archive video, metadata, and art;
  comments are out of scope by design.
- **Channel art:** on first video of a channel, pull avatar/banner from the infojson
  thumbnails; generate `_thumb` (square), `_banner`, `_tvart` (16:9) via ffmpeg
  scale/crop into `/cache/channels/`. Video thumbs go to
  `/cache/videos/<first-char>/<id>.jpg` (keeps directories small).
- **Chapters:** YouTube chapters from the infojson are stored and delivered to the
  plugin as Jellyfin chapter markers.
- **SponsorBlock:** at index time, `GET https://sponsor.ajay.app/api/skipSegments/{sha256(id)[0..4]}`
  (k-anonymity endpoint — we never send raw IDs), filter to configured categories
  (default: `sponsor, selfpromo, interaction, intro, outro, preview, music_offtopic`),
  validate (`0 ≤ start < end ≤ duration`), store `[{category,start_s,end_s}]`.
  The category→MediaSegmentType mapping (§1.2) is applied server-side when serving
  `/api/jf/v1/videos`, so the plugin stays dumb and the mapping is editable in one
  place (Settings → SponsorBlock).
- **SB refresh:** scheduler re-fetches segments for videos newer than 30 days on a
  weekly cron (segments accrete after upload), bumping `sb_refreshed` and
  `changed_at` — the plugin's next delta poll triggers Jellyfin's segment update.
- **No playback sync:** Tubelet neither plays nor tracks watched state — Jellyfin
  owns that end-to-end. The `changes` cursor carries only changed-video ids (→ plugin
  queues a Jellyfin metadata refresh) and playlists (→ collections).

---

## 6. Scheduling, rate limiting, cookies

### Scheduler
One hosted service, one loop: `SELECT … FROM subscriptions WHERE next_check <= now`.
Per subscription: `yt-dlp --flat-playlist --playlist-end {max_new} -J` on the
channel's uploads (cheap, 1–2 HTTP requests), diff against `videos` + `jobs`,
enqueue misses at priority 5, apply filters (duration/regex/date floor), recompute
`next_check` from cron (Cronos). Also on cron: SB refresh, yt-dlp self-update
check, nightly `PRAGMA optimize` + db backup to `/cache/backup/`.

### Rate limiting (be a polite bot, stay unbanned)
- Global **token bucket** in front of every yt-dlp spawn (metadata calls count too),
  default ~30 YouTube operations/hour for subscriptions; user pastes bypass the
  bucket but not the sleep flags.
- Per-invocation: `--sleep-requests 1 --sleep-interval 3 --max-sleep-interval 8`,
  `--limit-rate` configurable (default off).
- Download concurrency default 2; subscription scans run serially.
- 429/bot-check trips the queue-wide cooldown (§4.3) with exponential escalation.
- All knobs live in Settings → Network, changes apply live (no restart).

### Cookies & PO tokens
- Settings page: upload/paste `cookies.txt` (Netscape format) → stored at
  `/cache/cookies/cookies.txt` (0600). "Validate" button runs a metadata-only
  yt-dlp call and reports logged-in identity. Age + last-success shown; a failing
  cookie jar raises a persistent UI banner.
- Optional **PO token provider**: ships `bgutil-ytdlp-pot-provider`'s yt-dlp plugin;
  if enabled in settings the extractor args are passed through. Off by default.

---

## 7. API & realtime surface

### Frontend REST (`/api/v1`)

```
POST /api/v1/intake                {url}            → classification + enqueued job ids
GET  /api/v1/queue                                  → active + recent jobs
POST /api/v1/queue/{id}/retry|pause|cancel|priority
GET  /api/v1/videos?query=&channel=&sort=&page=     → FTS5-backed browse/search
GET  /api/v1/videos/{id}                            → full video doc
DELETE /api/v1/videos/{id}         ?also_ignore=1   → delete media + row
GET/POST/PATCH/DELETE /api/v1/subscriptions…
GET/POST/PATCH/DELETE /api/v1/playlists…            → custom playlists (→ JF collections)
GET/PUT  /api/v1/settings/{section}
POST /api/v1/cookies               (multipart)      → store + validate
GET  /api/v1/system                                 → disk free, versions, queue stats, cooldown state
```

### Jellyfin plugin API (`/api/jf/v1`) — see §1.3.

### SignalR hub (`/hub`)
Server→client events: `job.progress` (≤4 Hz/job), `job.state`, `video.added`,
`channel.added`, `queue.stats`, `system.banner` (cookie expired, cooldown active),
`scan.progress`. Client→server: none (all mutations via REST — keeps the hub
stateless and reconnects trivial). On reconnect the client re-GETs `/queue` and
resubscribes; missing a transient event is never fatal.

No auth anywhere: the server binds for LAN use; the Jellyfin plugin needs only the
base URL. (Anything sensitive — cookies.txt — is write-only via the API and never
served back.)

---

## 8. Frontend (Vue 3)

Design center: **the omnibox**. The home screen is a paste box + the live queue.

- **Home/Queue:** paste → instant classification chip → Enter enqueues. Queue cards
  show thumb, title, progress bar, speed/ETA (SignalR), inline retry/cancel.
  Failed section collapsed below with stderr expander.
- **Library:** virtualized thumbnail grid (`vue-virtual-scroller`), FTS
  search-as-you-type (debounced 150 ms, server-side FTS5 prefix query), channel
  filter rail, sort by published/added/duration. Click → detail with in-browser
  preview (`/media/…` range requests), segment timeline visualization, per-video
  re-download.
- **Channels:** grid of subscribed channels, per-channel cron/quality/filters,
  "fetch entire backlog" button with count preview before confirm.
- **Settings:** Network (rate limits), Cookies, Quality profiles, SponsorBlock
  categories + mapping, Storage, Maintenance (yt-dlp version + update button,
  db backup).
- Stack: Vue 3 `<script setup>` TS, Pinia stores fed by SignalR, plain CSS with
  dark default. No component framework — a handful of hand-rolled components keeps
  the bundle < 150 kB gz and first paint instant on a LAN.

Usability details that matter: every long operation is cancellable; every error is
visible and actionable; empty states explain the next step; the tab title shows
queue progress (`⬇ 3 · 42%`); everything works keyboard-only.

---

## 9. One container, minimal

Single image, single process (the .NET host; yt-dlp/ffmpeg are short-lived children).
No supervisor, no nginx (Kestrel serves the SPA + static media fine on a LAN),
no python runtime (yt-dlp standalone binary). The Jellyfin plugin zips are baked
into the image and served from `/repo`.

```dockerfile
# ---- frontend ----
FROM node:22-alpine AS fe
WORKDIR /src
COPY frontend/package*.json .
RUN npm ci
COPY frontend .
RUN npm run build                                   # → dist/, pre-brotli'd

# ---- jellyfin plugin ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS jf
WORKDIR /src
COPY jellyfin-plugin .
RUN dotnet publish -c Release -o /out && ./pack-repo.sh /out /repo   # zip + manifest.json

# ---- backend ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS be
WORKDIR /src
COPY backend .
RUN dotnet publish Tubelet -c Release -r linux-x64 -o /out \
      /p:PublishSingleFile=true /p:PublishTrimmed=true /p:InvariantGlobalization=false
# self-contained single file ≈ 40 MB; no dotnet runtime needed in final image

# ---- runtime ----
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
      ffmpeg ca-certificates \
    && rm -rf /var/lib/apt/lists/*
ADD https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux /usr/local/bin/yt-dlp
RUN chmod +x /usr/local/bin/yt-dlp
COPY --from=be /out/Tubelet /app/Tubelet
COPY --from=fe /src/dist /app/wwwroot
COPY --from=jf /repo /app/repo
ENV TUBELET_MEDIA=/youtube TUBELET_CACHE=/cache ASPNETCORE_URLS=http://0.0.0.0:8000
VOLUME ["/youtube", "/cache"]
EXPOSE 8000
ENTRYPOINT ["/app/Tubelet"]
```

- Image ≈ **350 MB** (ffmpeg dominates; debian's ffmpeg build is safer for hwaccel
  than alpine's). Everything the pipeline needs is inside.
- **yt-dlp self-update:** the baked binary is the fallback; on a weekly cron (and
  on repeated "unable to extract" errors) the app downloads the latest release
  binary to `/cache/bin/yt-dlp`, verifies it with `--version`, and prefers it.
  YouTube breakage is fixed without a new image.
- DB at `/cache/tubelet.db`. `/cache/incomplete` → `/youtube` rename is only atomic
  within one filesystem — if the volumes are on different mounts, detect it and
  fall back to copy + fsync + rename.
- Runs as non-root `PUID/PGID` (env), matching Jellyfin's file access.

```yaml
# docker-compose.yml — the whole deployment
services:
  tubelet:
    image: ghcr.io/you/tubelet
    ports: ["8000:8000"]
    environment: { PUID: 1000, PGID: 1000 }
    volumes:
      - /srv/media/youtube:/youtube        # ← add this same path to Jellyfin as a Shows library
      - tubelet-cache:/cache
```

Jellyfin setup (README): add `/youtube` as a **Shows** library, disable its default
metadata providers, add plugin repo `http://tubelet:8000/repo/manifest.json`,
install **Tubelet**, set the server URL. One plugin, one URL, done.

---

## 10. Project layout

```
tubelet/
├── backend/
│   ├── Tubelet/                       # single project — it's one app, keep it flat
│   │   ├── Program.cs                 # DI, endpoint groups, hosted services
│   │   ├── Db/          (schema.sql, Migrator.cs, Queries.cs)
│   │   ├── Domain/      (Video.cs, Channel.cs, Job.cs, QualityProfile.cs)
│   │   ├── Api/         (IntakeEndpoints.cs, QueueEndpoints.cs, LibraryEndpoints.cs,
│   │   │                 JfEndpoints.cs, JsonContexts.cs)
│   │   ├── Pipeline/    (DownloadCoordinator.cs, YtDlp.cs, Ffmpeg.cs, PostProcessor.cs,
│   │   │                 RetryPolicy.cs, RateGate.cs)
│   │   ├── Scheduling/  (Scheduler.cs, SubscriptionScanner.cs, Janitor.cs)
│   │   ├── Sponsorblock/(SbClient.cs, SbRefresher.cs)
│   │   └── Realtime/    (EventsHub.cs, Broadcaster.cs)
│   └── Tubelet.Tests/                 # pipeline, queue, and /api/jf contract tests
├── jellyfin-plugin/                   # Jellyfin.Plugin.Tubelet (§1) + pack-repo.sh
├── frontend/                          # Vue 3 + Vite + TS + Pinia
│   └── src/ (views: Home, Library, Channels, Settings; stores: queue, library, system)
├── Dockerfile
└── docker-compose.yml
```

The `/api/jf/v1` contract tests live in `Tubelet.Tests` and run the **actual plugin
DTOs** against the server (shared via a small `Tubelet.Contracts` source package) —
both sides compile against the same types, so the wire contract cannot drift.

## 11. Build order

1. **Skeleton:** schema, migrator, `/api/v1` + `/api/jf/v1` serving seeded fixture
   data, contract tests green, plugin repo endpoint serving a stub manifest.
2. **Pipeline:** intake → queue → yt-dlp worker → convert → index; SignalR progress;
   retries/resume; Home/Queue UI. *(Usable product.)*
3. **Jellyfin plugin:** metadata + segments providers against the fixture server,
   then end-to-end against real downloads; delta sync (metadata refresh + playlists).
4. **Library & subscriptions:** FTS search, library UI, channel subscriptions +
   scheduler, playlists ⇄ collections.
5. **Hardening:** cookies UI, rate-limit cooldowns, SB refresh, janitor, yt-dlp
   self-update, hwaccel transcode, backups.
