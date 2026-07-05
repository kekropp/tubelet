# Tubelet — one image, one process (DESIGN §9). yt-dlp + ffmpeg are short-lived children of the
# .NET host; the Vue SPA and the Jellyfin plugin repo are baked in and served by Kestrel.

# ---- frontend -------------------------------------------------------------
FROM node:22-alpine AS fe
WORKDIR /src
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
# The repo's vite config writes into ../backend/Tubelet/wwwroot; here we redirect it to ./dist.
RUN npm run build -- --outDir dist --emptyOutDir

# ---- jellyfin plugin + repo ----------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS jf
RUN apt-get update && apt-get install -y --no-install-recommends zip && rm -rf /var/lib/apt/lists/*
WORKDIR /src
# The plugin references backend/Tubelet.Contracts, so both trees must be present.
COPY backend/Directory.Build.props backend/Directory.Build.props
COPY backend/Tubelet.Contracts backend/Tubelet.Contracts
COPY jellyfin-plugin jellyfin-plugin
RUN chmod +x jellyfin-plugin/pack-repo.sh \
 && jellyfin-plugin/pack-repo.sh --out /repo --version 1.0.0.0 --abi 10.11.0.0

# ---- backend --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS be
WORKDIR /src
COPY backend/ ./
# Self-contained single file (no runtime needed in the final image). Not trimmed: Dapper and the
# SQLite bundle use reflection paths that trimming can strip.
RUN dotnet publish Tubelet/Tubelet.csproj -c Release -r linux-x64 --self-contained true -o /out \
      /p:PublishSingleFile=true /p:PublishTrimmed=false /p:InvariantGlobalization=false

# ---- runtime --------------------------------------------------------------
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
      ffmpeg ca-certificates libicu72 gosu curl \
    && rm -rf /var/lib/apt/lists/*

# Baked yt-dlp (the fallback; the app self-updates a newer copy into /cache/bin and prefers it).
ADD https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux /usr/local/bin/yt-dlp
# 0755 (not just +x): yt-dlp_linux is a self-extracting PyInstaller binary that must *read* itself,
# so the non-root runtime user needs read as well as execute.
RUN chmod 0755 /usr/local/bin/yt-dlp

WORKDIR /app
COPY --from=be /out/ /app/
COPY --from=fe /src/dist/ /app/wwwroot/
COPY --from=jf /repo/ /app/repo/
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh \
 && groupadd -g 1000 tubelet \
 && useradd -u 1000 -g tubelet -d /app -s /usr/sbin/nologin tubelet

ENV TUBELET_MEDIA=/youtube TUBELET_CACHE=/cache ASPNETCORE_URLS=http://0.0.0.0:8000
VOLUME ["/youtube", "/cache"]
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://localhost:8000/healthz || exit 1
ENTRYPOINT ["/entrypoint.sh"]
