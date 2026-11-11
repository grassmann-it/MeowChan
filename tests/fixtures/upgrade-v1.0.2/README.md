# SYNTHETIC 1.0.2 predecessor generator (AP04; historical AP07 / GP60)

Generator overlay for production source pinned to
`eec6c0169f8d52f7481ea0bf7febfd20981e97c3`. Root's resume-1011 attempt passed
offline source export/check and restore/build, but generation failed before the
first settings write. **No successful generation or upgrade acceptance yet.**
This ordering fix has not been built, tested or executed by its worker. Current
working-copy APIs are **not proof of released schema**: compile the overlay only
against the exact exported release. Never patch old product source or hand-author
SQL/JSON state to make the fixture succeed. This is synthetic provider input, not
authentic historical user/release state.

## AP04 overlay correction and preserved evidence

The failed generator created `profile/exports` before `new Config { ...
}.SaveConfig(profile)`. The exported released `Desktop/Config.cs:742` correctly
refused a nonempty destination with missing settings and no recovery decision.
Its `IsEmptyProfile` accepts an empty directory; ordinary `SaveConfig` is sufficient
here. The overlay now creates only the empty new profile, saves settings, **then**
creates exports and starts the fixture host. Merely assigning `DownloadPath` and
`LastExportDirectory` does not create those directories. No recovery bypass,
lease/admission override, pinned product change or oracle relaxation is involved.

Historical evidence stays untouched (paths relative to
`/home/tomka/mc-evidence/ux103-restpakete-20261111/`):

* Original user-supplied generator: `input/tests/fixtures/upgrade-v1.0.2/Program.cs`.
* Failed exported overlay and exact release: `resume-1011/released102-source/`.
* Failed profile: `resume-1011/released102-profile/`.
* Failure log: `resume-1011/ap04-generation.log` (the pre-save recovery refusal).

The original Program.cs, failed exported overlay and pre-fix versioned overlay all
had SHA256 `fabd202a944332e291479f4e5973deb761b55fb5d7ff92d3a9be7d1b319373eb`.
Only the current versioned overlay is patched. Do not update the old source
manifest, reuse the failed profile, delete its exports, or repair it into passing
evidence. Export again to fresh paths so the new manifest binds the changed
overlay; all identity/content assertions and receipt schemas remain unchanged.

`tests/test_released102_fixture_generation.py` is a small source-contract
regression for the new-path guard, empty pre-save setup and ordinary save before
export creation. It neither emulates released Config nor proves runtime admission.
Root's targeted command, **NOT RUN by this worker**:

```sh
python3 -m unittest discover -s tests -p test_released102_fixture_generation.py -v
```

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

## Exact offline Root commands from scratch (NOT executed by this worker)

Use fresh paths, no personal profile. Commands assume Linux `timeout` and `flock`,
Python 3 standard library, locally available pinned Git objects and .NET 10.
Use an approved isolated acceptance worktree after Root integrates and freezes
the overlay, current consumer and tests; never the live/root checkout or a sibling
worker's worktree. Run serially with Root's usual build lock. The offline package
directory must already contain **all** needed packages/native dependencies; no
download fallback.
A missing object/package or timeout is a failed attempt, not permission to fetch.

```sh
set -eu
cd /absolute/path/to/approved/isolated/acceptance-worktree
ATTEMPT=$(mktemp -d /tmp/meowchan-ap04-XXXXXX)
printf 'Retain this attempt, including failures: %s\n' "$ATTEMPT"
SOURCE="$ATTEMPT/released102-source"
PROFILE="$ATTEMPT/released102-profile"
CURRENT_REVISION=$(git rev-parse HEAD)
printf '%s\n' "$CURRENT_REVISION" > "$ATTEMPT/current-revision.txt"
git status --porcelain=v1 > "$ATTEMPT/current-status.txt"
test ! -s "$ATTEMPT/current-status.txt" # Freeze first; do not clean/reset a dirty tree.
# Substitute an existing approved local NuGet feed; do not create/download it here.
OFFLINE_FEED=/absolute/path/to/approved/offline-nuget-feed
test -d "$OFFLINE_FEED"
# --close prevents child compiler processes retaining the lock FD.
serial() { flock --close /tmp/meowchan-ui-1.0-build.lock nice -n 19 "$@"; }

nice -n 19 timeout 180s python3 tests/fixtures/upgrade-v1.0.2/prepare.py "$SOURCE" \
  > "$ATTEMPT/prepare.log" 2>&1
python3 tests/fixtures/upgrade-v1.0.2/check.py --source "$SOURCE" \
  > "$ATTEMPT/source-check.log" 2>&1
# Explicit empty network sources, only local feed; audit disabled.
cat > "$ATTEMPT/offline.nuget.config" <<'XML'
<configuration><packageSources><clear /></packageSources></configuration>
XML
PROJECT="$SOURCE/tests/Upgrade102FixtureGenerator/UpgradeFixtureGenerator.csproj"
serial timeout 180s dotnet restore "$PROJECT" \
  --configfile "$ATTEMPT/offline.nuget.config" --source "$OFFLINE_FEED" \
  -p:NuGetAudit=false -p:Configuration=Debug -nodeReuse:false \
  > "$ATTEMPT/released-restore.log" 2>&1
serial timeout 180s dotnet build "$PROJECT" \
  --no-restore --configuration Debug \
  -p:SourceRevisionId=eec6c0169f8d52f7481ea0bf7febfd20981e97c3 \
  -p:UseSharedCompilation=false -nodeReuse:false \
  > "$ATTEMPT/released-build.log" 2>&1
# Do not use dotnet run with implicit restore/build.
serial env -u MEOWCHAN_PROFILE -u OCHAN_PROFILE MEOWCHAN_DISABLE_UPDATE_CHECK=1 \
  timeout 130s dotnet "$SOURCE/tests/Upgrade102FixtureGenerator/bin/Debug/net10.0/MeowChan.DesktopTests.dll" \
  "$PROFILE" "$SOURCE/UPGRADE-102-SOURCE.json" \
  > "$ATTEMPT/generation.log" 2>&1
python3 tests/fixtures/upgrade-v1.0.2/check.py --source "$SOURCE" --profile "$PROFILE" \
  > "$ATTEMPT/receipt-check.log" 2>&1
# Preserve before any current writer opens the profile.
cp "$PROFILE/v102-receipt.json" "$ATTEMPT/released-receipt.json"
cp "$SOURCE/UPGRADE-102-SOURCE.json" "$ATTEMPT/released-source-manifest.json"
```

The friend assembly name is intentional. Debug avoids adding a release-lock
project entry. If the pinned SDK changes the output location, stop and inspect;
do not substitute a current-product binary. The exact informational version
`1.0.2+<pinned SHA>` is required, not merely a prefix (which admitted 1.0.20).
The version attribute alone cannot attest compiled source; retain source manifest,
reviewed overlay, build provenance and output together. Run in Root's approved
network-denied environment if process-level network isolation is required; the
injected HTTP handler has no network fallback but is not an OS network sandbox.

### Three current processes: first open plus two offline restarts (NOT RUN)

Continue in the **same shell**, only after the fresh receipt check succeeds. Do
not manually open the profile first. Both current projects below must come from
the same frozen current worktree/configuration, not from the exported 1.0.2 tree.
Use the same approved offline feed. No GUI is involved in this host-side recipe.

```sh
CONSUMER_PROJECT=tests/fixtures/upgrade-v1.0.2/current/UpgradeConsumer.csproj
TEST_PROJECT=tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj
for project in "$CONSUMER_PROJECT" "$TEST_PROJECT"; do
  name=$(basename "$project" .csproj)
  serial timeout 180s dotnet restore "$project" \
    --configfile "$ATTEMPT/offline.nuget.config" --source "$OFFLINE_FEED" \
    -p:NuGetAudit=false -p:Configuration=Debug -nodeReuse:false \
    > "$ATTEMPT/$name-restore.log" 2>&1
  serial timeout 180s dotnet build "$project" --no-restore --configuration Debug \
    -p:SourceRevisionId="$CURRENT_REVISION" -p:UseSharedCompilation=false -nodeReuse:false \
    > "$ATTEMPT/$name-build.log" 2>&1
done
CONSUMER_DLL="$PWD/tests/fixtures/upgrade-v1.0.2/current/bin/Debug/net10.0/MeowChan.DesktopTests.dll"
test -f "$CONSUMER_DLL"
# Test owns ALL three sequential opens of the SAME profile. Missing env means skip.
serial env -u MEOWCHAN_PROFILE -u OCHAN_PROFILE MEOWCHAN_DISABLE_UPDATE_CHECK=1 \
  MEOWCHAN_RELEASED102_PROFILE="$PROFILE" \
  MEOWCHAN_RELEASED102_SOURCE_MANIFEST="$SOURCE/UPGRADE-102-SOURCE.json" \
  MEOWCHAN_RELEASED102_CONSUMER_DLL="$CONSUMER_DLL" \
  MEOWCHAN_RELEASED102_RESULTS="$ATTEMPT/current-cycles" \
  timeout 900s dotnet test "$TEST_PROJECT" --no-build --no-restore --configuration Debug \
    --filter 'FullyQualifiedName~MeowChan.DesktopTests.Released102RestPackageUpgradeTests' \
    --results-directory "$ATTEMPT/current-test-results" \
    --logger 'trx;LogFileName=ap04.trx' -nodeReuse:false \
  > "$ATTEMPT/current-tests.log" 2>&1
test -f "$ATTEMPT/current-cycles/current-result.json"
```

The existing test invokes the current consumer once for cycle 0 (upgrade), once
for cycle 1, and once for cycle 2 (offline restarts), each a separate process with
normal disposal/lease reacquisition. It compares full post/attachment/bound
revision and asset identities, original bytes, exports, private inventory,
settings and watch state against the unchanged released receipt, and checks both
current assembly hashes against its own build. Equal-byte independent occurrences
remain distinct; neither counts nor hashes alone substitute for identity.

Inspect the TRX: the upgrade fact must execute, not skip. Require `cycle-0.json`,
`cycle-1.json`, `cycle-2.json`, their logs and the final `current-result.json`.
Any failure/timeout stops this attempt: retain everything and regenerate from
scratch, never rerun the fresh-profile test against a partially migrated profile.
Do not run the pre-migration file-digest check on a migrated database. This proves
only the synthetic released-provider → current-host chain if it passes, not native
UI, package/platform, private-consent or thread/bound/unbound tag acceptance.

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
installers, UI, live HTTP, downgrade or OS matrix proof is supplied. Current
migration/two-restart proof remains pending execution of the existing consumer
and assertions above by Root under its serial lease. Do not pass this receipt to
the incompatible v1x `Release102UpgradeTests`. No new generation/build/test PASS
is claimed. The user chose **1.1.0** for the next release; this bounded generator
fix neither bumps versions nor closes remaining implementation/acceptance gates,
and authorizes no push, upload, tag or release.
