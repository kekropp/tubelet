#!/usr/bin/env bash
# Build the Tubelet Jellyfin plugin and pack it into a repository directory the
# Tubelet server serves at /repo. Produces:
#   <out>/tubelet_<version>.zip   — the plugin package (dll + Tubelet.Contracts.dll + meta.json)
#   <out>/versions.json           — host-independent version metadata; the server turns this
#                                   into /repo/manifest.json with absolute, per-request sourceUrls.
#
# Usage: ./pack-repo.sh [--version X.Y.Z.W] [--abi 10.11.0.0] [--out DIR] [--changelog TEXT]
#                       [--base-url URL]
#
# With --base-url, also writes a fully static <out>/manifest.json whose sourceUrl is
# <base-url>/<zip> — point Jellyfin's "custom repository" at that manifest.json to install without
# running the Tubelet server (e.g. the committed jellyfin-repo/ served from raw.githubusercontent.com).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$here/Jellyfin.Plugin.Tubelet/Jellyfin.Plugin.Tubelet.csproj"

version="1.0.0.0"
target_abi="10.11.0.0"
framework="net9.0"
out="$here/repo"
changelog="Initial release."
guid="b7c0e5cc-2b6e-4f83-9c6e-3a1d47e05f10"
description="Metadata, images and SponsorBlock media segments for a Tubelet library."
base_url=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)   version="$2"; shift 2 ;;
    --abi)       target_abi="$2"; shift 2 ;;
    --out)       out="$2"; shift 2 ;;
    --changelog) changelog="$2"; shift 2 ;;
    --base-url)  base_url="$2"; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 1 ;;
  esac
done

# Make --out absolute now: the zip step cd's into a temp dir, so a relative out path would resolve
# against the wrong directory.
mkdir -p "$out"
out="$(cd "$out" && pwd)"

timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
zip_name="tubelet_${version}.zip"
build_dir="$(mktemp -d)"
stage_dir="$(mktemp -d)"
trap 'rm -rf "$build_dir" "$stage_dir"' EXIT

echo ">> building plugin ($framework, ABI $target_abi) v$version"
dotnet publish "$project" -c Release -f "$framework" -o "$build_dir" --nologo -v q \
  "-p:Version=$version" "-p:AssemblyVersion=$version" "-p:FileVersion=$version"

# The plugin package ships exactly the plugin assembly and its zero-dep contract; the
# Jellyfin host provides everything else. meta.json lets Jellyfin identify the plugin.
cp "$build_dir/Jellyfin.Plugin.Tubelet.dll" "$stage_dir/"
cp "$build_dir/Tubelet.Contracts.dll" "$stage_dir/"

cat > "$stage_dir/meta.json" <<JSON
{
  "category": "Metadata",
  "guid": "$guid",
  "name": "Tubelet",
  "description": "Metadata, images and SponsorBlock media segments for a Tubelet library.",
  "overview": "Tubelet for Jellyfin",
  "owner": "tubelet",
  "framework": "$framework",
  "targetAbi": "$target_abi",
  "version": "$version",
  "changelog": "$changelog",
  "timestamp": "$timestamp"
}
JSON

mkdir -p "$out"
rm -f "$out/$zip_name"
( cd "$stage_dir" && zip -q -r -X "$out/$zip_name" . )

checksum="$(md5sum "$out/$zip_name" | awk '{print $1}')"
echo ">> packed $out/$zip_name (md5 $checksum)"

# Host-independent version list; the server builds absolute sourceUrls at request time.
cat > "$out/versions.json" <<JSON
[
  {
    "version": "$version",
    "changelog": "$changelog",
    "targetAbi": "$target_abi",
    "zip": "$zip_name",
    "checksum": "$checksum",
    "timestamp": "$timestamp"
  }
]
JSON

echo ">> wrote $out/versions.json"

# Optional static manifest.json with an absolute sourceUrl — installable from a plain file host.
if [[ -n "$base_url" ]]; then
  source_url="${base_url%/}/$zip_name"
  cat > "$out/manifest.json" <<JSON
[
  {
    "guid": "$guid",
    "name": "Tubelet",
    "description": "$description",
    "overview": "Tubelet for Jellyfin",
    "owner": "tubelet",
    "category": "Metadata",
    "versions": [
      {
        "version": "$version",
        "changelog": "$changelog",
        "targetAbi": "$target_abi",
        "sourceUrl": "$source_url",
        "checksum": "$checksum",
        "timestamp": "$timestamp"
      }
    ]
  }
]
JSON
  echo ">> wrote $out/manifest.json (sourceUrl $source_url)"
fi

echo ">> done. Point Jellyfin at http://<tubelet-host>/repo/manifest.json (or the static manifest.json)"
