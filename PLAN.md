# Tubelet — implementation plan (phases 2–5)

Working plan for continuing the build. DESIGN.md is the authority for *what*; this
file records *where we are* and *how to proceed*. Update the checkboxes as phases land.

## Current state (phase 1 done)

`backend/` solution with 3 projects, **34/34 tests green** (`cd backend && dotnet test`).

- `Tubelet` — ASP.NET Core minimal API, .NET 10.
  - `Db/schema.sql` (embedded) + `Db/Migrator.cs` (`PRAGMA user_version`, migration list append-only).
  - `Db/Database.cs` — connection factory (WAL etc. pragmas per-open), global `change_seq`
    (`Database.NextSeq/CurrentSeq`) stored in `settings`; every plugin-visible mutation stamps
    `changed_at` with `NextSeq()`. `/api/jf/v1/changes?since=` is a range scan over it.
  - `Db/Queries.cs` — `UpsertVideo` (row + FTS + seq), `DeleteVideo`, `EnqueueJob`
    (dedup vs videos/ignored/live jobs, revives failed), `ClaimNextJob`
    (atomic `UPDATE…RETURNING`, priority then added_at, respects `next_retry`),
    `ResetStuckJobs` (startup recovery), `QueueStats`.
  - `Db/FixtureSeeder.cs` — deterministic catalog when `TUBELET_FIXTURES=1` + empty db.
    Fixture ids referenced by tests: `dQw4w9WgXcQ` (segments+chapters), `fixture0001..3`,
    channels `UCuAXFkgsw1L7xaCfnd5JJOw`/`UC2C_jShtL725hvbm1arSV9w`, playlist `TL-0FIXTURE…`.
  - `Api/` — `JfEndpoints` (batch videos/channels, changes delta w/ 204 — no playback),
    `IntakeEndpoints` (+ `Pipeline/UrlClassifier`), `QueueEndpoints` (list/retry/pause/cancel/priority),
    `LibraryEndpoints` (FTS5 prefix search, paging, sort whitelist, delete + also_ignore),
    `SystemEndpoints` (system stats, settings sections as raw JSON under `settings` key `section:<name>`),
    `RepoEndpoints` (serves `{app}/repo` if baked, else stub manifest; plugin GUID
    `b7c0e5cc-2b6e-4f83-9c6e-3a1d47e05f10`), `Mapping.cs` (row→DTO, SB category→segment type
    applied server-side via `Sponsorblock/SbMapping`).
  - `Realtime/` — `EventsHub` (server→client only) at `/hub`, typed `Broadcaster`
    (`job.progress`, `job.state`, `video.added`, `channel.added`, `queue.stats`, `system.banner`).
  - Static: `/cache/*` immutable-cached, `/media/*` range requests. `/` is a placeholder page.
- `Tubelet.Contracts` — wire DTOs with **explicit `[JsonPropertyName]`** (never rely on policy).
  Plugin (phase 3) compiles against this project.
- `Tubelet.Tests` — `TubeletFactory` (WebApplicationFactory + temp dirs + fixtures; one db per
  test class), JF contract tests, frontend API tests, migrator/classifier/queue unit tests.

### Gotchas already learned (do not rediscover)

- Arch dotnet SDK: `backend/Directory.Build.props` sets `AllowMissingPrunePackageData` — keep it.
- `SQLitePCLRaw.bundle_e_sqlite3` pinned 3.0.3 in Tubelet.csproj (GHSA-2m69-gcr7-jv3q).
- snake_case must be set as `PropertyNamingPolicy` on `ConfigureHttpJsonOptions` in Program.cs;
  a policy inside the source-gen context gets overridden by runtime web defaults.
- Every new response/request DTO must be added to `Api/JsonContexts.cs` (`ApiJsonContext`).
- Dapper underscore mapping lives in a `[ModuleInitializer]` (`Db/Database.cs`), not Program.
- `bool` minimal-API query params don't bind `?flag=1` — parse from `HttpContext.Request.Query`.
- `yt-dlp` is NOT installed on this dev machine; ffmpeg is. The self-download path
  (`/cache/bin/yt-dlp`, DESIGN §9) doubles as the dev bootstrap. Gate real-network tests
  behind an env var or trait; CI/tests must pass offline.

---

## Phase 2 — download pipeline + Home/Queue UI  → *usable product*

Backend (`Tubelet/Pipeline/`):

1. **`YtDlpLocator`** — resolve binary: `TUBELET_YTDLP` env → `/cache/bin/yt-dlp` → `yt-dlp` on PATH;
   verify with `--version` (cache result for `/api/v1/system.ytdlp_version`). Add "download latest
   release binary to `/cache/bin`" routine (used by dev bootstrap now, self-update cron in phase 5).
2. **`YtDlp.cs`** — subprocess wrapper:
   - `FetchMetadataAsync(id)` → `yt-dlp -J <id>` → parse infojson (title, channel, published
     full timestamp, duration, tags, chapters, thumbnails); return typed record + raw JSON
     (gzip → `videos.info_json`).
   - `DownloadAsync(id, profile, progress, ct)` → exact flags from DESIGN §4.2 (`--continue --part`,
     `--concurrent-fragments 4`, retry/sleep flags, `--progress-template "%(progress)j" --newline
     --no-colors`, output to `{cache}/incomplete/%(id)s.%(ext)s`). Parse progress JSON lines from
     stdout (throttle to 4 Hz before broadcasting), ring-buffer last ~50 stderr lines for error UI.
   - Kill-on-cancel (process tree). Exit code + stderr → `RetryPolicy.Classify`.
3. **`RetryPolicy.cs`** — taxonomy per DESIGN §4.3: `transient` (backoff `min(2^attempts,60)min ± jitter`
   → `next_retry`, requeue until `max_attempts`), `throttled` (429/"confirm you're not a bot" →
   set `settings.cooldown_until`, pause whole queue, `system.banner`, don't burn attempts),
   `permanent` (private/deleted/geo/members-only → fail immediately). Unit-test classification
   against canned stderr samples.
4. **`RateGate.cs`** — global token bucket ahead of every yt-dlp spawn (default ~30 ops/h for
   subscription work; user pastes bypass bucket). Live-reload from settings section `network`.
5. **`Ffmpeg.cs`** — ffprobe streams → policy table §4.4 (`compat` default: h264+aac keep/remux,
   webm → transcode; `quality`: remux `-c copy`); always `<id>.mp4` + `+faststart`; ffprobe sanity
   check (duration within 2 s, first frame decodable) before indexing. hwaccel detect (`/dev/dri`,
   nvidia) but keep libx264 fallback; hw path can be a stub until phase 5.
6. **`DownloadCoordinator.cs`** (BackgroundService) — N download workers (default 2, settings) +
   1 postprocess lane. Wake via in-process `Channel<long>` signal (intake/scheduler write → signal)
   + periodic poll for `next_retry`. Job flow: claim → `fetching_meta` (upsert channel+video rows
   early so UI shows title/thumb; download video thumb + channel art on first sight of channel) →
   `downloading` → hand to postprocess queue → `converting` → `indexing`: atomic rename
   `incomplete/<id>.mp4` → `{media}/<channel_id>/<id>.mp4` (cross-device fallback: copy+fsync+rename),
   SponsorBlock fetch (`Sponsorblock/SbClient` — k-anonymity `sha256(id)[..4]` endpoint, filter
   configured categories, validate bounds), `Queries.UpsertVideo`, job `done`, broadcast
   `video.added` + `queue.stats`.
7. **Intake expansion** — channel/playlist paste: `yt-dlp --flat-playlist -J` (RateGate'd) →
   enqueue N video jobs (skip archived/ignored/queued, report counts), create/refresh `playlists`
   rows + entries for playlist pastes. Wire `POST /api/v1/intake` statuses `enqueued|expanded`.
   Run expansion as a background task; report via SignalR `scan.progress`.
8. **Broadcaster** — wire `job.progress`/`job.state` from coordinator; keep REST as source of truth.

Frontend (`frontend/`, Vue 3 + Vite + TS + Pinia + @microsoft/signalr, plain dark CSS,
no component framework):

9. Scaffold vite app; dev proxy → `:5000`; build output consumed by backend
   (`app.UseStaticFiles` + SPA fallback replacing the placeholder `/`).
10. **Home/Queue view** (the product): omnibox with instant local classification chip (mirror
    `UrlClassifier` regexes in TS) → POST intake; queue cards (thumb, title, progress bar,
    speed/ETA via SignalR, retry/cancel/priority inline); failed section collapsed with stderr
    expander; empty states; tab title `⬇ N · pct%`; keyboard-only usable.
11. Pinia `queue` store fed by SignalR with re-GET `/api/v1/queue` on (re)connect.

Tests: YtDlp progress-line parser, RetryPolicy classification, coordinator happy-path with a
**fake yt-dlp script** (shell stub emitting progress JSON + writing a file — no network), ffmpeg
policy decision table (ffprobe against generated test media via `ffmpeg -f lavfi`), SbClient
against canned responses, intake expansion with fake flat-playlist JSON.

- [x] Phase 2 complete: paste real URL → file lands in `/youtube/<ch>/<id>.mp4` (verified E2E with
      `jNQXAC9IVRw`: h264+aac faststart mp4, thumb generated, incomplete cleaned), Home/Queue UI
      shows live progress over SignalR, restart mid-download resumes (`--continue --part` +
      `ResetStuckJobs`), `dotnet test` green offline (68 tests; live coordinator pinned to a
      fast-failing yt-dlp stub in tests). Pipeline lives in `Tubelet/Pipeline/*` +
      `Sponsorblock/SbClient.cs`; frontend in `frontend/` (Vue 3, builds to `Tubelet/wwwroot`,
      ~46 kB gz). hwaccel transcode still uses libx264 only (VAAPI/QSV/NVENC deferred to phase 5).

## Phase 3 — Jellyfin plugin

1. `jellyfin-plugin/Jellyfin.Plugin.Tubelet/` — separate csproj targeting the Jellyfin 10.11 ABI
   (`Jellyfin.Controller` NuGet, **net9.0** — 10.11 targets net9, not net8); references
   `Tubelet.Contracts` (multi-targeted `net9.0;net10.0`, still zero external deps).
2. Plugin skeleton: `Plugin.cs` (GUID **must equal** `RepoEndpoints.PluginGuid`), config page with
   one field (server URL), typed `TubeletClient` (batch + delta + `/cache` image fetch; no playback).
3. **Metadata**: `IRemoteMetadataProvider<Series>` (channel) + `<Episode>` (video) — identify from
   path (`{media}/<channel_id>/<video_id>.mp4`), then persist `ProviderIds["Tubelet"]`; year
   seasons; premiere timestamp. Image providers: episode thumb, channel poster/banner/backdrop
   from `/cache/*`. (Chapters aren't settable via `IRemoteMetadataProvider` — deferred; better
   embedded in the mp4 server-side at download time.)
4. **Segments**: `IMediaSegmentProvider` mapping `SegmentDoc.Type` string → `MediaSegmentType`
   (`Jellyfin.Database.Implementations.Enums`; values Commercial/Preview/Recap/Outro/Intro).
5. **Sync** (no playback — Jellyfin owns watched state): scheduled task polling `/changes` with a
   persisted cursor (plugin config) → metadata refresh for changed video ids + playlists ⇄
   collections.
6. `pack-repo.sh` — zip (plugin dll + `Tubelet.Contracts.dll` + `meta.json`) + host-independent
   `versions.json`; backend generates `/repo/manifest.json` per-request with absolute `sourceUrl`s
   + md5 checksum + targetAbi. Version gating per running-server compat.
7. Plugin-side round-trip test (`PluginRoundTripTests`) deserializes real server responses through
   the plugin's exact serializer config + shared Contracts DTOs.

- [x] Phase 3 code complete (offline): plugin builds against the real Jellyfin.Controller 10.11.11
      ABI (net9.0) → `Jellyfin.Plugin.Tubelet.dll` + `Tubelet.Contracts.dll` only (host provides the
      rest). Providers (Series/Episode metadata + images), `IMediaSegmentProvider`, and the delta
      sync task are wired; `pack-repo.sh` + dynamic `/repo/manifest.json` verified end-to-end (zip
      served, checksum + host-rewritten sourceUrl correct); `dotnet test` green (71 tests). **Not yet
      run inside a live Jellyfin** — install/library-populate/segments-visible needs a manual pass on
      a real 10.11 server.

## Phase 4 — library & subscriptions

1. **Scheduler.cs** (BackgroundService, Cronos): loop over `subscriptions WHERE next_check <= now`;
   per sub `yt-dlp --flat-playlist --playlist-end {max_new} -J` (serial, RateGate'd), diff vs
   videos+jobs+ignored, apply `filter_json` (min duration, title regex, date floor, max items),
   enqueue at priority 5, recompute `next_check` from cron.
2. Subscriptions CRUD endpoints (`/api/v1/subscriptions`) — DTOs already sketched in
   `Api/FrontendDtos.cs`; wire + tests. "Fetch entire backlog" = one-shot expansion with count
   preview endpoint first.
3. Playlists CRUD (`/api/v1/playlists`, custom `TL-<ulid>` ids) — bump `changed_at` on every
   mutation so collections sync rides the existing cursor.
4. **Library UI**: virtualized grid (`vue-virtual-scroller`), 150 ms debounced FTS
   search-as-you-type, channel rail, sort; detail view with `/media/` preview + segment timeline +
   re-download. **Channels UI**: subscription grid, per-channel cron/quality/filters.

- [x] Phase 4 complete: `Scheduler` (Cronos, one loop) polls due `subscriptions` and enqueues new
      uploads at priority 5 via `SubscriptionScanner` (shares flat-playlist + `EnqueueJob` dedup;
      `SubscriptionFilter` = min/max duration, title regex, date floor, max_items, best-effort on
      extractor-dependent flat fields). Subscriptions + playlists CRUD wired
      (`/api/v1/subscriptions` GET/POST/PATCH/DELETE + `/scan` + `/backlog`; `/api/v1/playlists`
      custom `TL-<ulid>`, every mutation bumps `changed_at` so collections ride the `/changes`
      cursor — verified new custom playlist surfaces past an earlier cursor). Frontend: hash router
      + Library view (channel rail, 150 ms debounced FTS search, sort, infinite-scroll paging with
      `content-visibility` + a video-detail overlay: `/media` preview, segment timeline, chapters,
      re-download/delete) and Channels view (subscribe form, per-sub cron/quality/filter editor,
      enable toggle, scan/backlog). `dotnet test` green (103 tests, offline); frontend builds to
      `wwwroot` (~52 kB gz). Perf smoke test at 10,005 rows (`TUBELET_FIXTURES_BULK=10000`): FTS
      3–12 ms, deepest page 25 ms, channel filter 3 ms. Virtualization is hand-rolled (paged +
      `content-visibility`) rather than `vue-virtual-scroller` — zero new deps, stays offline.

## Phase 5 — hardening & ship

1. Settings UI (Network/Cookies/Quality/SponsorBlock/Storage/Maintenance) against the existing
   settings sections; live-apply (RateGate re-read, worker count).
2. Cookies: multipart upload → `/cache/cookies/cookies.txt` (0600), "Validate" runs metadata-only
   yt-dlp call, identity + age surfaced; failing jar → persistent banner. Write-only (never served
   back). Optional PO-token provider passthrough (off by default).
3. SB weekly refresh cron (videos < 30 days, bump `sb_refreshed` + `changed_at`); yt-dlp
   self-update cron (+ on repeated "unable to extract"); Janitor (orphan `.part` > 7 days, nightly
   `PRAGMA optimize`, db backup to `/cache/backup/`, log rotation).
4. hwaccel transcode paths (VAAPI/QSV/NVENC autodetect) with libx264 fallback; optional subs/
   thumbnail embedding flags.
5. **Dockerfile** per DESIGN §9 (4-stage; self-contained single-file publish, non-root PUID/PGID,
   brotli precompressed SPA, plugin repo baked in) + docker-compose.yml + README Jellyfin setup
   walkthrough. CI: dotnet test + plugin build against Jellyfin next-major preview.

- [x] Phase 5 complete: Settings UI (Network/Cookies/Quality/SponsorBlock/Storage/Maintenance,
      hash route `#/settings`) drives live-applied settings sections. Cookies write-only to
      `/cache/cookies/cookies.txt` (0600) with a metadata-only yt-dlp `Validate` + persistent banner
      on rejection (`Api/CookiesEndpoints.cs`). Maintenance lanes hang off the `Scheduler`
      (`CronGate` persists next-run in settings): weekly `SbRefresher` (videos < 30 d, bumps
      `changed_at` only when segments actually change), yt-dlp self-update (cron + `ytdlp_update_requested`
      flag the coordinator sets after 3 consecutive "unable to extract"), and a `Janitor` hosted
      service (orphan `.part` > TTL w/ no live job, `PRAGMA optimize`, `VACUUM INTO` backup keeping N,
      log rotation). hwaccel transcode auto-detects VAAPI (`/dev/dri`) / NVENC (nvidia dev) / QSV with
      a **libx264 fallback on any hw failure** (`Ffmpeg.ResolveHwaccel` + `TranscodeCmd`); optional
      `.srt` sidecars + `--embed-thumbnail` + PO-token passthrough (`TUBELET_POT_EXTRACTOR_ARGS`).
      4-stage `Dockerfile` (self-contained single-file, non-root PUID/PGID via gosu, SPA + baked plugin
      repo) verified end-to-end: image builds, container boots, `/api/v1/system` + `/repo/manifest.json`
      (host-rewritten sourceUrl) + SPA all serve, yt-dlp runs as the non-root user (needs `chmod 0755`,
      not just `+x` — it self-extracts and must read itself). `docker-compose.yml` + README Jellyfin
      walkthrough + GitHub Actions CI (backend tests w/ ffmpeg, frontend build, plugin build stable +
      next-major-preview ABI [`-p:JellyfinControllerVersion`, continue-on-error], docker build).
      `dotnet test` green (126 tests, offline); frontend ~56 kB gz.
      Follow-ups closed: yt-dlp `--embed-chapters` on download (remux `-c copy` preserves container
      chapters → they reach Jellyfin natively, closing the phase-3 chapters deferral); `/healthz`
      liveness endpoint + Docker `HEALTHCHECK` (curl), verified container reports `healthy`.

## Conventions to keep

- One serializer path: contracts DTOs pinned names; everything else snake_case via http options.
- Any plugin-visible mutation MUST stamp `changed_at = Database.NextSeq(conn, tx)` in the same tx.
- New settings live in a `section:<name>` JSON blob; no schema migration for settings.
- Schema changes = append a migration to `Migrator.Migrations`, never edit schema.sql's v1.
- Jobs are the only queue; workers claim via `Queries.ClaimNextJob`; no in-memory job state that
  can't be rebuilt from the db on restart.
- Tests must pass offline; anything network-touching gets a fake subprocess/HTTP fixture.
- Cronos 0.13: the `GetNextOccurrence(DateTimeOffset, TimeZoneInfo)` overload doesn't resolve
  cleanly here — pass `from.UtcDateTime` to the UTC `DateTime` overload (`CronSchedule` does this).
  All schedule math is UTC so restarts are deterministic.
- Custom playlist DELETE is a hard delete with no tombstone (the `/changes` feed only carries
  `active = 1` playlists). The plugin is expected to reconcile its collection set on a full sync;
  revisit if we ever need immediate collection removal (phase 5).
- Subscription filters apply to `--flat-playlist` entries whose `duration`/`upload_date` are
  extractor-dependent — when a field is absent the filter lets the entry through (never silently
  drops a wanted video). `max_items` also caps enqueues per scan; `--playlist-end` bounds the fetch.
