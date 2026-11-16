#!/usr/bin/env bash
# Root-only bubblewrap adapter for AP04.
#
# Why this exists: hosted Ubuntu images refuse an unprivileged bwrap uid_map
# write, so the workflow starts bwrap through sudo.  A root-started bwrap is
# not setuid (real UID == effective UID == 0), and in that mode bwrap maps the
# sandbox UID onto the *caller's* UID, i.e. host root:
#
#   bwrap (uid 0)  -> userns "0 0 1"  -> nested userns "<runner> 0 1"
#
# The runner UID is then unmapped inside every namespace.  Capabilities held
# in a user namespace never apply to inodes whose owner is unmapped, so a
# runner-owned 0750 /home/runner cannot even be searched while bwrap builds
# the new root ("Can't mkdir parents for .../released102-profile: Permission
# denied"), and anything the replay wrote would be owned by host root.
#
# Fix: root only creates one outer user namespace whose root is the runner
# ("0 <runner-uid> 1", "0 <runner-gid> 1", setgroups denied, host
# supplementary groups cleared) and then executes the reviewed bwrap inside it
# as that namespace root.  bwrap's own mapping chain then resolves to the
# runner on the host:
#
#   outer userns 0 = host runner -> bwrap userns 0 -> nested "<runner> 0 1"
#
# Setup, bind sources and destinations are therefore resolved with exactly the
# runner's own host identity: no path is staged, aliased or chmod-ed, and no
# file access beyond the runner's own is gained.  The real bwrap command keeps
# the read-only root, --disable-userns and every namespace/capability option;
# this adapter never weakens or drops them, there is no host-namespace or
# root fallback, and any failure aborts before the replay starts.
set -euo pipefail

die() { echo "AP04 root launcher: $*" >&2; exit 125; }

real_bwrap="${MEOWCHAN_RELEASED102_REAL_BWRAP_PATH:-}"
if [[ ${1:-} == --real-bwrap ]]; then
  (($# >= 2)) || die '--real-bwrap needs an absolute executable'
  real_bwrap=$2
  shift 2
fi
[ -n "$real_bwrap" ] || die 'real bwrap path is required'
[[ "$real_bwrap" == /* && -x "$real_bwrap" ]] || die "real bwrap is not executable: $real_bwrap"

# Root is required only to create the runner-owned outer user namespace.
[[ "$(id -u)" == 0 ]] || die 'must be started through sudo as root'
runner_uid="${SUDO_UID:-}"
runner_gid="${SUDO_GID:-}"
[[ "$runner_uid" =~ ^[1-9][0-9]*$ && "$runner_gid" =~ ^[1-9][0-9]*$ ]] ||
  die 'SUDO_UID/SUDO_GID must name the non-root runner; refusing a root replay'

# Inspect only bwrap's own options. Arguments after `--` belong to the child
# and are opaque, so a child argument cannot change the identity contract.
sandbox_uid='' sandbox_gid='' unshare_user=0 disable_userns=0
args=("$@")
for ((index=0; index<${#args[@]}; index++)); do
  case ${args[index]} in
    --) break ;;
    --uid) sandbox_uid=${args[index+1]:-} ;;
    --gid) sandbox_gid=${args[index+1]:-} ;;
    --unshare-user) unshare_user=1 ;;
    --disable-userns) disable_userns=1 ;;
    --userns|--userns2|--userns-block-fd|--unshare-user-try|--args)
      die "${args[index]} is not allowed: the adapter owns the user-namespace mapping" ;;
  esac
done
((unshare_user && disable_userns)) || die 'bwrap must create and then disable user namespaces'
[[ "$sandbox_uid" == "$runner_uid" && "$sandbox_gid" == "$runner_gid" ]] ||
  die "--uid/--gid ($sandbox_uid:$sandbox_gid) must equal the sudo runner ($runner_uid:$runner_gid)"

python=/usr/bin/python3
[ -x "$python" ] || die "$python is required to create the runner user namespace"

# The helper forks; the child clears host supplementary groups, unshares a
# user namespace and waits until the root parent has written its maps; only
# then does it become that namespace's root (= host runner) and exec bwrap.
# `--cap-drop ALL` keeps only the explicitly requested --cap-add set for the
# nested setup; the replay still drops every capability through setpriv.
# shellcheck disable=SC2016
helper='
import ctypes, os, signal, sys
uid, gid, bwrap = sys.argv[1], sys.argv[2], sys.argv[3]
argv = [bwrap, "--cap-drop", "ALL", *sys.argv[4:]]
libc = ctypes.CDLL(None, use_errno=True)
CLONE_NEWUSER = 0x10000000
ready_r, ready_w = os.pipe()
go_r, go_w = os.pipe()
pid = os.fork()
if pid == 0:
    os.close(ready_r); os.close(go_w)
    try:
        os.setgroups([])
        if libc.unshare(CLONE_NEWUSER) != 0:
            raise OSError(ctypes.get_errno(), "unshare(CLONE_NEWUSER)")
        os.write(ready_w, b"u")
        if os.read(go_r, 1) != b"g":
            raise RuntimeError("user namespace maps were not written")
        os.setresgid(0, 0, 0)
        os.setresuid(0, 0, 0)
        if os.getgroups():
            raise RuntimeError("supplementary groups survived")
        os.execv(bwrap, argv)
    except BaseException as error:
        print(f"AP04 root launcher: runner user namespace failed: {error}", file=sys.stderr)
        os._exit(125)
os.close(ready_w); os.close(go_r)
def forward(signum, _frame):
    try:
        os.kill(pid, signum)
    except ProcessLookupError:
        pass
for signum in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
    signal.signal(signum, forward)
try:
    if os.read(ready_r, 1) != b"u":
        raise RuntimeError("child did not create its user namespace")
    for name, value in (("uid_map", f"0 {uid} 1\n"), ("setgroups", "deny\n"), ("gid_map", f"0 {gid} 1\n")):
        with open(f"/proc/{pid}/{name}", "w") as handle:
            handle.write(value)
    os.write(go_w, b"g")
except BaseException as error:
    print(f"AP04 root launcher: cannot map the runner user namespace: {error}", file=sys.stderr)
    os.kill(pid, signal.SIGKILL)
    os.waitpid(pid, 0)
    sys.exit(125)
os.close(go_w)
while True:
    try:
        _, status = os.waitpid(pid, 0)
        break
    except InterruptedError:
        continue
sys.exit(os.waitstatus_to_exitcode(status) if os.WIFEXITED(status) else 128 + os.WTERMSIG(status))
'
exec "$python" -I -S -c "$helper" "$runner_uid" "$runner_gid" "$real_bwrap" "$@"
