#!/usr/bin/env bash
# Run as a non-root PUID/PGID (matching Jellyfin's file access) so the media it writes to the
# shared /youtube library is owned correctly. Remaps the baked `tubelet` user to the requested
# ids, fixes ownership of the volumes, then drops privileges via gosu.
set -e

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"

groupmod -o -g "$PGID" tubelet 2>/dev/null || true
usermod  -o -u "$PUID" -g "$PGID" tubelet 2>/dev/null || true

mkdir -p /cache /youtube
# Best-effort: on a fresh volume this fixes ownership; on a huge existing library a deep chown is
# skipped (only the top level), so startup stays fast.
chown "$PUID:$PGID" /cache /youtube 2>/dev/null || true

exec gosu "$PUID:$PGID" /app/Tubelet "$@"
