#!/usr/bin/env bash
# Root-only bubblewrap adapter for AP04.
#
# sudo bwrap can be unable to resolve a runner-owned workspace path while it
# assembles its mount namespace.  Make every writable bind source reachable
# through /var/tmp first.  The bind mount is an alias of the original tree,
# not a copy: writes still land in the runner-owned profile/results/worktree.
# The real bwrap command remains responsible for the read-only root and all
# namespace/capability options; this adapter never weakens those options.
set -euo pipefail

real_bwrap="${MEOWCHAN_RELEASED102_REAL_BWRAP_PATH:-}"
if [[ ${1:-} == --real-bwrap ]]; then
  (($# >= 2)) || { echo 'AP04 root launcher: --real-bwrap needs an absolute executable' >&2; exit 125; }
  real_bwrap=$2
  shift 2
fi
[ -n "$real_bwrap" ] || { echo 'AP04 root launcher: real bwrap path is required' >&2; exit 125; }
[[ "$real_bwrap" == /* && -x "$real_bwrap" ]] || {
  echo "AP04 root launcher: real bwrap is not executable: $real_bwrap" >&2
  exit 125
}


stage=$(mktemp -d /var/tmp/meowchan-ap04-bind.XXXXXX)
chmod 755 "$stage"
declare -a mounts=()
declare -a rewritten=()
cleanup() {
  local index
  for ((index=${#mounts[@]}-1; index>=0; index--)); do
    /usr/bin/umount "${mounts[index]}" 2>/dev/null || true
  done
  rmdir "$stage" 2>/dev/null || true
}
trap cleanup EXIT HUP INT TERM

# Rewrite only bwrap's pre-command writable --bind options. Arguments after
# `--` belong to the child and are opaque, so a child cannot smuggle a mount.
while (($#)); do
  if [[ $1 == -- ]]; then
    rewritten+=("$@")
    break
  fi
  if [[ $1 == --bind ]]; then
    (($# >= 3)) || { echo 'AP04 root launcher: --bind needs source and destination' >&2; exit 125; }
    source=$2
    destination=$3
    [ -d "$source" ] || { echo "AP04 root launcher: bind source is not an accessible directory: $source" >&2; exit 125; }
    alias="$stage/source-${#mounts[@]}"
    mkdir "$alias"
    /usr/bin/mount --bind "$source" "$alias"
    mounts+=("$alias")
    rewritten+=(--bind "$alias" "$destination")
    shift 3
    continue
  fi
  rewritten+=("$1")
  shift
done

# No --bind is silently dropped. If a bind source cannot be staged, this
# process fails before bwrap starts; there is no host-namespace fallback.
set +e
"$real_bwrap" "${rewritten[@]}"
status=$?
cleanup
trap - EXIT HUP INT TERM
exit "$status"
