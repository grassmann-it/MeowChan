#!/usr/bin/env bash
# Upgrade run 1.0.2 -> 1.1.0 on a synthetic profile written by the pinned released code.
#
# Seven starts of the current build on one profile, a state capture before and after each,
# then the assertions of upgrade_state.py. Nothing here touches a real profile, the network
# or the working tree; everything is written below --out.
#
# Prerequisites (both produced offline, see README.md):
#   --source    the exported, built 1.0.2 tree (prepare.py + dotnet build of the overlay)
#   --consumer  the built current MeowChan.DesktopTests.dll of current/UpgradeConsumer.csproj
#
# Usage:
#   upgrade_102_run.sh --source DIR --consumer DLL --out DIR [--runner "flock ... nice -n 19"]
set -euo pipefail

SOURCE=""; CONSUMER=""; OUT=""; RUNNER=""
while [ $# -gt 0 ]; do
  case "$1" in
    --source) SOURCE=$2; shift 2 ;;
    --consumer) CONSUMER=$2; shift 2 ;;
    --out) OUT=$2; shift 2 ;;
    --runner) RUNNER=$2; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -n "$SOURCE" ] && [ -n "$CONSUMER" ] && [ -n "$OUT" ] || { echo "need --source, --consumer and --out" >&2; exit 2; }
[ -e "$OUT" ] && { echo "--out must not exist: $OUT" >&2; exit 2; }
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
GENERATOR="$SOURCE/tests/Upgrade102FixtureGenerator/bin/Debug/net10.0/MeowChan.DesktopTests.dll"
[ -f "$GENERATOR" ] || { echo "no built 1.0.2 generator: $GENERATOR" >&2; exit 2; }
[ -f "$CONSUMER" ] || { echo "no built current consumer: $CONSUMER" >&2; exit 2; }

mkdir -p "$OUT"
PROFILE="$OUT/profile"
STATE="python3 $HERE/upgrade_state.py"
# No profile environment variable may leak in, and no start may check for updates.
run() { env -u MEOWCHAN_PROFILE -u OCHAN_PROFILE MEOWCHAN_DISABLE_UPDATE_CHECK=1 $RUNNER "$@"; }

echo "== synthetic released 1.0.2 profile =="
run timeout 200s dotnet "$GENERATOR" "$PROFILE" "$SOURCE/UPGRADE-102-SOURCE.json" > "$OUT/generation.log" 2>&1
python3 "$HERE/check.py" --source "$SOURCE" --profile "$PROFILE" | tail -2
$STATE snapshot "$PROFILE" "$OUT/00-released-1.0.2.json"

# observe x2: the upgrade and the second start (idempotency); media-index: the cold index build;
# write-host: settings and a thread tag (private-tags.json version 1); write-local-tag: version 2
# with the permanent v1 backup; readback: the writes after a restart; observe: an already
# upgraded profile once more.
STEPS=(observe observe media-index write-host write-local-tag readback observe)
index=0
for step in "${STEPS[@]}"; do
  index=$((index + 1))
  name=$(printf '%02d-start%d-%s' "$index" "$index" "$step")
  echo "== start $index ($step) =="
  run timeout 400s dotnet "$CONSUMER" "$PROFILE" "$OUT/$name-result.json" "$step" > "$OUT/$name.log" 2>&1
  # The exact v1 bytes, secured before the start that converts the store to version 2.
  if [ "$step" = write-host ] && [ -f "$PROFILE/private-tags.json" ]; then
    cp "$PROFILE/private-tags.json" "$OUT/private-tags-v1-copy.json"
  fi
  $STATE snapshot "$PROFILE" "$OUT/$name.json"
done

echo "== assertions =="
mapfile -t STATES < <(ls "$OUT"/0*.json | grep -v -- '-result.json' | sort)
mapfile -t RESULTS < <(ls "$OUT"/*-result.json | sort)
# The first three states come from read-only starts; the later ones write on purpose.
python3 "$HERE/upgrade_state.py" check --read-only-states 3 \
  --states "${STATES[@]}" --results "${RESULTS[@]}" \
  --v1-tag-copy "$OUT/private-tags-v1-copy.json" | tee "$OUT/upgrade-check.txt"
