# SYNTHETIC 1.0.2 predecessor generator (AP07 / GP60)

Source-only, **not executed or accepted**. Intended production source is pinned to
`eec6c0169f8d52f7481ea0bf7febfd20981e97c3` (1.0.2 per Wave24's source review).
W26 did not reread that commit using Git: Git execution was prohibited. Current
working-copy APIs are corroborating context, **not proof of released schema**.
Root must compile this overlay against that exact exported source. Never patch
old product source or hand-author SQL/JSON state to make the fixture succeed.
This is synthetic provider input, not authentic historical user/release state.

## Preparation and provenance

`prepare.py` exports the locally available pinned tree using NUL-delimited
`git ls-tree` plus `git cat-file`, without checkout, tag resolution or fetch.
Lazy fetch/replacement objects are disabled. Ordinary and executable files retain
0644/0755 mode; symlink blobs become inert ordinary files; submodules/unsupported
names fail closed. It overlays the checked-in Program.cs and standalone csproj.
It rejects existing destinations and retains failed partial exports for diagnosis.
No build, restore, profile creation, network, Git mutation or current solution edit.

The source manifest records every exported path, length, SHA256, original Git
blob ID/mode, both overlay hashes and preparer hash. No clock/absolute destination
is embedded. Source traversal is capped at 20,000 files, 32 MiB per blob and
512 MiB total; each Git subprocess times out after 30 seconds. Tree listing is
read before those caps are checked; this is a trusted pinned local repository,
not a general hostile-repository sandbox. Root should impose a whole-command
wall-clock bound as shown below.

## Exact offline Root commands (NOT executed by this worker)

Use fresh paths, no personal profile. Commands assume Linux `timeout` and `flock`,
Python 3 standard library, locally available pinned Git objects and .NET 10.
Run serially with Root's usual build lock. The offline package directory must
already contain **all** needed packages/native dependencies; no download fallback.
A missing object/package or timeout is a failed attempt, not permission to fetch.

```sh
set -eu
cd /home/tomka/Desktop/oChan
SOURCE=/tmp/meowchan-w26-102-source-UNIQUE
PROFILE=/tmp/meowchan-w26-102-profile-UNIQUE
# Substitute an existing approved local NuGet feed; do not create/download it here.
OFFLINE_FEED=/absolute/path/to/approved/offline-nuget-feed

timeout 180s python3 tests/fixtures/upgrade-v1.0.2/prepare.py "$SOURCE"
python3 tests/fixtures/upgrade-v1.0.2/check.py --source "$SOURCE"
# Explicit empty network sources, only local feed; audit disabled.
cat > "$SOURCE/offline.nuget.config" <<'XML'
<configuration><packageSources><clear /></packageSources></configuration>
XML
PROJECT="$SOURCE/tests/Upgrade102FixtureGenerator/UpgradeFixtureGenerator.csproj"
flock /tmp/meowchan-ui-1.0-build.lock timeout 180s dotnet restore "$PROJECT" \
  --configfile "$SOURCE/offline.nuget.config" --source "$OFFLINE_FEED" \
  -p:NuGetAudit=false -p:Configuration=Debug
flock /tmp/meowchan-ui-1.0-build.lock timeout 180s dotnet build "$PROJECT" \
  --no-restore --configuration Debug \
  -p:SourceRevisionId=eec6c0169f8d52f7481ea0bf7febfd20981e97c3
# Do not use dotnet run with implicit restore/build.
env -u MEOWCHAN_PROFILE -u OCHAN_PROFILE MEOWCHAN_DISABLE_UPDATE_CHECK=1 \
  timeout 130s dotnet "$SOURCE/tests/Upgrade102FixtureGenerator/bin/Debug/net10.0/MeowChan.DesktopTests.dll" \
  "$PROFILE" "$SOURCE/UPGRADE-102-SOURCE.json"
python3 tests/fixtures/upgrade-v1.0.2/check.py --source "$SOURCE" --profile "$PROFILE"
```

The friend assembly name is intentional. Debug avoids adding a release-lock
project entry. If the pinned SDK changes the output location, stop and inspect;
do not substitute a current-product binary. The exact informational version
`1.0.2+<pinned SHA>` is required, not merely a prefix (which admitted 1.0.20).
The version attribute alone cannot attest compiled source; retain source manifest,
reviewed overlay, build provenance and output together. Run in Root's approved
network-denied environment if process-level network isolation is required; the
injected HTTP handler has no network fallback but is not an OS network sandbox.

## Output oracle

Successful preparation prints `SYNTHETIC SOURCE EXPORTED files=N bytes=B overlay=2`.
`check.py --source` prints exactly the prefix:

```
SOURCE OK synthetic=true revision=eec6c0169f8d52f7481ea0bf7febfd20981e97c3 files=N overlay=2
```

It verifies every listed source blob/hash/size and both reviewed overlays, preparer
hash and Version property. Build artifacts/other unlisted source-directory files
are not checked: use a fresh export with no product edits and the commands above.
The manifest is reproducibility evidence, not a signed/authenticated release.

After generation, `check.py --source ... --profile ...` also prints:

```
RECEIPT OK synthetic=true threads=2 posts=4 favorites=2 ratings=2 bookmarks=2 files=N
NOT AN AUTHENTIC USER PROFILE; NOT A MIGRATION/RESTART/RELEASE ACCEPTANCE
```

Counts N/B are observed, not fixed. Any mismatch exits nonzero (`CHECK FAILED:`),
including changed/missing/extra profile files, links, duplicate JSON keys, source
binding, identity/count/bytes/rating/bookmark/watch/settings/request mismatches.
This is a pre-migration check: do not use it to demand unchanged databases after
migration. Preserve the receipt independently before current-product opening.

## Fixture scope and determinism

* Two threads: 4chan and 8kun, board g, thread 123; posts 123/124; media tim 456,
  a fixed embedded PNG, MD5 derived from those bytes. Only six exact URLs and
  GET requests are accepted (maximum 256 requests).
* Released providers create manifests, work state and archives; two real HTML
  exports are recorded through released ExportHistoryStore. No DDL overlay.
* Two exact media favorites, thread favorites, ratings 5/2 and reply-124 bookmarks
  with a fixed multiline synthetic note. Store readback checks values/keys, not
  just counts. Posts and asset length/SHA256 are checked before receipt creation.
* Paused watches, volume 31, muted start, gallery context, disabled update checks,
  archive table layout/split width/window width and output directories are retained.
* A 120-second watchdog bounds async stalls including disposal. Timeout exits 124;
  partial output may remain. Never reuse/repair it into passing evidence.
* Post-shutdown traversal rejects links and caps entries/files/bytes (8192 entries,
  4096 files, 32 MiB/file, 128 MiB total). Streamed SHA256, ordinal-sorted slash paths
  and sorted request observations make manifest order stable. Receipt publication
  uses a new pending file followed by rename, bound to the source manifest SHA256.

Deterministic **inputs and identities** do not mean byte-identical profiles:
release-generated IDs, timestamps, generations, DB bytes and absolute paths vary.
The receipt retains those observations; sorting does not remove variability.
Do not modify release clocks/randomness or fabricate old releases for sameness.

No plugin-selection migration (see upgrade-v1x), collection/URL-text export,
installers, UI, live HTTP, downgrade, OS matrix, current migration or two-restart
proof is supplied. Root owns a separate current-side consumer and exact released
schema/build validation. Do not pass this receipt to the incompatible v1x
`Release102UpgradeTests`. No generation/build/test PASS is claimed.
