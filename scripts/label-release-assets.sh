#!/usr/bin/env bash
# Sets the readable display labels on the nine application downloads of a release (as on v1.0.2).
# Usage: scripts/label-release-assets.sh v1.1.1 [owner/repo]   (needs gh with release write access)
set -euo pipefail
tag="${1:?tag, e.g. v1.1.1}"; repo="${2:-grassmann-it/MeowChan}"
declare -A labels=(
  [win-x64.zip]='Windows x64 (.zip)' [osx-arm64.dmg]='macOS Apple Silicon (.dmg)' [osx-x64.dmg]='macOS Intel (.dmg)'
  [linux-x64.AppImage]='Linux x64 (.AppImage)' [linux-arm64.AppImage]='Linux ARM64 (.AppImage)'
  [linux-x64-bundled-vlc.tar.gz]='Linux x64 — bundled VLC (.tar.gz)' [linux-x64-system-vlc.tar.gz]='Linux x64 — system VLC (.tar.gz)'
  [linux-arm64-bundled-vlc.tar.gz]='Linux ARM64 — bundled VLC (.tar.gz)' [linux-arm64-system-vlc.tar.gz]='Linux ARM64 — system VLC (.tar.gz)')
gh api "repos/$repo/releases/tags/$tag" --paginate -q '.assets[]|[.id,.name]|@tsv' | while IFS=$'\t' read -r id name; do
  for suffix in "${!labels[@]}"; do
    if [[ "$name" == *-"$suffix" && "$name" != *video-sources* ]]; then
      gh api -X PATCH "repos/$repo/releases/assets/$id" -f label="${labels[$suffix]}" -q '.name+" -> "+.label'
    fi
  done
done
