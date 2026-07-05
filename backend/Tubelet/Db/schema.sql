-- Tubelet schema v1. Applied once by Migrator when user_version = 0.

CREATE TABLE channels (
  channel_id    TEXT PRIMARY KEY,          -- UC…
  name          TEXT NOT NULL,
  description   TEXT NOT NULL DEFAULT '',
  tags          TEXT NOT NULL DEFAULT '[]',-- JSON array
  thumb_path    TEXT,
  banner_path   TEXT,
  tvart_path    TEXT,
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
  media_size    INTEGER,
  width         INTEGER,
  height        INTEGER,
  vcodec        TEXT,
  acodec        TEXT,
  thumb_path    TEXT,                      -- cache-root-relative
  segments      TEXT,                      -- JSON [{category,start_s,end_s}] or NULL
  sb_refreshed  INTEGER,
  downloaded_at INTEGER NOT NULL,
  changed_at    INTEGER NOT NULL,          -- monotonic change sequence (cursor source for /changes)
  info_json     BLOB                       -- gzip'd full yt-dlp infojson (re-index later)
);
CREATE INDEX ix_videos_channel ON videos(channel_id, published DESC);
CREATE INDEX ix_videos_changed ON videos(changed_at);

CREATE VIRTUAL TABLE videos_fts USING fts5(
  title, description, channel_name, tags,
  content='', contentless_delete=1, tokenize='porter unicode61'
);  -- rowid = videos.rowid, kept in sync by the indexer

CREATE TABLE playlists (
  playlist_id   TEXT PRIMARY KEY,          -- PL… or "TL-<ulid>" for custom
  name          TEXT NOT NULL,
  channel_id    TEXT,
  channel_name  TEXT,
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
  last_check    INTEGER,
  next_check    INTEGER
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
  last_error    TEXT,
  error_kind    TEXT,                       -- transient|permanent|throttled
  progress      REAL NOT NULL DEFAULT 0,    -- 0..1 (mirrored to SignalR, persisted coarsely)
  added_at      INTEGER NOT NULL,
  started_at    INTEGER,
  finished_at   INTEGER
);
CREATE INDEX ix_jobs_ready ON jobs(state, priority, next_retry, added_at);

CREATE TABLE ignored (                      -- ids intake/scanner must skip (user said "don't re-download")
  youtube_id    TEXT PRIMARY KEY
);

CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
-- cookies state, rate limits, quality profiles, sb categories/mapping, yt-dlp channel…

INSERT INTO settings(key, value) VALUES ('change_seq', '0');
