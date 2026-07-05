# Tubelet

Fast, minimal YouTube archival for Jellyfin. See [DESIGN.md](DESIGN.md) for the full design
and [PLAN.md](PLAN.md) for the implementation plan / current progress.

## Status

Build order (DESIGN.md §11):

- [x] Phase 1 — skeleton: SQLite schema + `user_version` migrator (WAL, FTS5, durable job queue),
      `/api/jf/v1` (batch + `/changes` delta), `/api/v1` frontend API, `/repo/manifest.json`,
      SignalR `/hub`, shared `Tubelet.Contracts` DTOs + contract tests
- [x] Phase 2 — download pipeline (yt-dlp → ffmpeg → index), SignalR progress, retries/resume, Home/Queue UI
- [x] Phase 3 — Jellyfin plugin: metadata + image providers, `IMediaSegmentProvider`, delta sync task, baked plugin repo
- [x] Phase 4 — subscriptions + scheduler, playlists ⇄ collections, FTS Library/Channels UI
- [x] Phase 5 — hardening & ship: Settings UI (Network/Cookies/Quality/SponsorBlock/Storage/Maintenance),
      cookies validation, SponsorBlock weekly refresh, yt-dlp self-update, Janitor (orphan cleanup /
      `PRAGMA optimize` / db backup), hwaccel transcode (VAAPI/QSV/NVENC + libx264 fallback),
      single-container Dockerfile + compose + CI

## Development

```sh
cd backend
dotnet test                                   # contract + unit + pipeline tests (offline)
TUBELET_FIXTURES=1 dotnet run --project Tubelet   # serve fixture catalog on :5000

cd ../frontend
npm install
npm run build      # → backend/Tubelet/wwwroot (dotnet run then serves the SPA at /)
npm run dev        # or: Vite dev server on :5173, proxying /api and /hub to :5000
```

The pipeline shells out to `yt-dlp` (resolved from `TUBELET_YTDLP`, `/cache/bin/yt-dlp`, or `PATH`)
and `ffmpeg`/`ffprobe`. Paste a URL on the home page and the video downloads, converts to a
faststart mp4, and lands in `<media>/<channel_id>/<video_id>.mp4`.

Data directories default to `backend/Tubelet/data/{cache,youtube}`; override with
`TUBELET_CACHE` / `TUBELET_MEDIA` (the container sets `/cache` and `/youtube`).
`TUBELET_FIXTURES=1` seeds a small deterministic catalog into an empty database —
useful for frontend/plugin development before the pipeline exists.

## Deployment (Docker)

The whole thing is one image: the .NET host serves the SPA, the API, media, and the Jellyfin
plugin repo; yt-dlp and ffmpeg are baked in.

```sh
docker compose up -d          # pulls ghcr.io/kekropp/tubelet and starts Tubelet on :8000
```

CI publishes the image to `ghcr.io/kekropp/tubelet` — `:latest` from `main`, plus `:vX.Y.Z` /
`:X.Y` on release tags. To build locally instead, swap `image:` for `build: .` in the compose file.

Edit `docker-compose.yml` first: point the `/youtube` bind mount at the directory you'll add to
Jellyfin, and set `PUID`/`PGID` to the user that owns your media. Open `http://<host>:8000` and
paste a URL — the video downloads, converts to a faststart mp4, and lands in
`/youtube/<channel_id>/<video_id>.mp4`.

**Hardware transcode (optional):** map a GPU (`devices: [/dev/dri:/dev/dri]` for VAAPI/QSV, or the
nvidia container runtime for NVENC) and pick it under **Settings → Quality**. Auto-detect uses a
mapped GPU when present and always falls back to libx264.

### Jellyfin setup

1. **Add the library.** In Jellyfin → *Dashboard → Libraries → Add*, create a **Shows** library
   pointing at the same path you mounted as `/youtube`. Disable its default metadata/image providers
   (Tubelet supplies them).
2. **Add the plugin repo.** *Dashboard → Plugins → Repositories → Add*. Either:
   - the running server (auto host-rewritten): `http://<tubelet-host>:8000/repo/manifest.json`, or
   - the static repo hosted from this git repo (no Tubelet server needed to install):
     `https://raw.githubusercontent.com/kekropp/tubelet/main/jellyfin-repo/manifest.json`.

   > The static repo lives in [`jellyfin-repo/`](jellyfin-repo/) (manifest + zip). Regenerate it after
   > a plugin change with
   > `jellyfin-plugin/pack-repo.sh --out jellyfin-repo --base-url https://raw.githubusercontent.com/kekropp/tubelet/main/jellyfin-repo`.
3. **Install Tubelet** from the catalog, then restart Jellyfin when prompted.
4. **Point the plugin at the server.** *Plugins → Tubelet → Settings* → set the server URL
   (`http://<tubelet-host>:8000`).

The manifest only offers plugin builds compatible with your running Jellyfin, so server/plugin
version drift can't happen. Metadata, images, chapters, and SponsorBlock media segments then flow
in; Jellyfin owns playback and watched state entirely (Tubelet never plays anything).

### Backups & maintenance

The nightly Janitor removes orphaned `.part` files, runs `PRAGMA optimize`, and snapshots the
database (`VACUUM INTO`) to `/cache/backup/` (keeps the last N). SponsorBlock re-fetches weekly for
videos < 30 days old, and yt-dlp self-updates on a weekly cron (and after repeated extractor
breakage) into `/cache/bin/yt-dlp`. All schedules and policies are editable under
**Settings → Maintenance**.
