# SYNTHETIC 1.0.2 predecessor generator (AP04; historical AP07 / GP60)

Generator overlay for production source pinned to
`eec6c0169f8d52f7481ea0bf7febfd20981e97c3`. Root's first resume-1011 attempt
failed before the first settings write; after the ordering fix below, generation
and one three-process run succeeded, **bound to an older current revision only**
(see "Recorded attempts"). **No upgrade acceptance exists for the current HEAD.** Current
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
# Fresh, too: git status hides ignored bin/, obj/, *.dll ...; a reused build is never the candidate.
git status --porcelain=v1 --ignored=matching --untracked-files=all | grep -v '__pycache__/$' > "$ATTEMPT/current-ignored.txt" || true
test ! -s "$ATTEMPT/current-ignored.txt"
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
# The tree must still be clean and at the same commit after both current builds and the test.
test "$(git rev-parse HEAD)" = "$CURRENT_REVISION"
git status --porcelain=v1 > "$ATTEMPT/current-status-after.txt"
# Read-only: documented 1.1.0 upgrade outcomes, bound to the revision stamped in the cycles.
python3 tests/fixtures/upgrade-v1.0.2/check_migrated.py --profile "$PROFILE" \
  --results "$ATTEMPT/current-cycles" --expect-revision "$CURRENT_REVISION" \
  --status "$ATTEMPT/current-status.txt" --status "$ATTEMPT/current-status-after.txt" \
  > "$ATTEMPT/migrated-check.log" 2>&1
```

Only `MIGRATED OK ... revision=expected source=clean` counts for a candidate. Without
`--expect-revision`/`--status` the line says `revision=unbound`/`source=unverified`,
which is a diagnosis of old evidence, not acceptance. `SourceRevisionId` is passed by
hand, so the stamped revision alone does not exclude a dirty build; the status captures
are the bound (they cover tracked and untracked files, not ignored build outputs).

`check_migrated.py` asserts that all three cycles ran one stamped current revision,
that `archive.db` carries that revision's archive schema (released 1.0.2: 5) with no
journal file left behind, that `work-state.sqlite` carries that revision's journal
marker (1.0.2 has none), that originals/exports are unchanged and that opening created
no tag or Danbooru file: `private-tags.*` (including `private-tags.v1.bak`),
`booru-catalog.*` or their dot-prefixed `.tmp` files. Expected markers are read from local
Git objects of both revisions. Manifest versions, rewritten files, new files and a prune
backup are only reported (`OBSERVED`). It reads the SQLite header only, never opens a
database and writes nothing. Helper regressions:
`python3 -m unittest discover -s tests -p test_released102_migrated_check.py -v`.

### Final run on the frozen candidate: one command (no complete run yet)

`ap04_run.py` performs every step above plus the post-migration checks and writes one
commit-bound report. It must start in a **clean, fresh detached worktree of exactly the candidate
commit** and refuses to run otherwise; it also refuses to write `VERDICT=PASS` if, at the end,
the worktree is dirty, HEAD differs, a sealed build output or the package set changed (builds
included). Every report line starts with `AP04 candidate=<40-hex> bin=<16> pkg=<16>`: the
commit, the digest of the built consumer/test output directories and the digest of the Linux
package manifest whose `source_commit` must equal the candidate.

```sh
python3 tests/fixtures/upgrade-v1.0.2/ap04_run.py --dry-run --candidate-sha "$SHA" --out "$OUT"   # plan only
python3 tests/fixtures/upgrade-v1.0.2/ap04_run.py --candidate-sha "$SHA" \
  --out /home/tomka/.cache/claude-ssd-tmp/ap4/r1 \
  --linux-package-dir <run>/linux-distribution-candidate-linux-x64/linux-packages \
  --sha256sums <run>/stable-candidate-"$SHA"/SHA256SUMS
```

* `VERDICT=PASS` needs the 1.1.0 package set of the `stable-candidate` run for the same commit: the
  Linux package manifest (`source_commit` = candidate, version 1.1.0, all three assets re-hashed)
  **and** that run's `SHA256SUMS` listing the same checksums; the two options are only accepted
  together. `--allow-no-package` merely permits a source-only diagnosis (`pkg=none`): with every step
  PASS its verdict is `SOURCE-ONLY` (exit 1), never `PASS`.
* Fresh means more than an empty `git status`, which does not show ignored files: no ignored entry
  other than `__pycache__/` (`bin/`, `obj/`, `*.dll`, `*.csproj.user`, ...). MSBuild reuses `obj/` and
  the plugin copy does not prune `bin/`, so a reused tree could be sealed and executed as if it were
  built from the candidate. Preflight and current-build refuse it; use a new `git worktree add --detach`.
* Each restore/build/test waits at most `--lock-wait` seconds (default 3600) for `heavy.lock` on top
  of its own timeout. A lock that is not acquired fails as `heavy.lock not acquired` (flock exit 75,
  nothing ran), not as a command timeout.
* Only a fresh profile below `--out` is opened; HOME/TMPDIR/DOTNET_CLI_HOME point below it,
  `DISPLAY`/`WAYLAND_DISPLAY`/profile variables are dropped, builds are serialized under the
  shared `heavy.lock` with `-m:1`, the offline feed is the local NuGet cache, nothing is downloaded.
* Every current process (the three upgrade cycles via the test, the writes and the read-backs) runs
  in a private user+network namespace (`unshare -Urn`) in addition to the consumer's HTTP handler
  that records any request (`--no-netns` falls back to the handler only).
* The consumer (`current/Program.cs`) has modes: `observe` (the xunit test; read-only, now also reports
  the plugin state), `write-host` (settings and queue options, pause state re-asserted, thread tag:
  creates `private-tags.json` version 1), `write-local-tag` (**store level**: one real manifest asset
  is tagged as a loose file, because the synthetic profile has no loose file; this converts to version
  2 and writes `private-tags.v1.bak`; host revalidation of loose files is therefore not exercised) and
  `readback` (two more offline restarts). Unpausing is deliberately not done: it starts the watch producers.
* `check_writes.py` asserts: required plugins (`4chan`, `8kun`, `danbooru`) enabled and active with no
  problem and an identical plugin state in all seven starts; written settings/queue options persisted
  while other values survive; `saved_urls.json` unchanged; tag state readable after restart;
  `private-tags.v1.bak` byte-identical to the copy the runner took while the file was still version 1,
  unchanged after the read-backs, no temporary file left; no Danbooru file without a Danbooru write.
  The documented way back (restore the backup as `private-tags.json`) is checked on bytes only.
* Not covered: the packaged app (the package set is bound by checksum only), a window, a real
  published-asset start, Windows/macOS. `--release-artifact/--release-sums` only compare the checksum of
  an owner-supplied 1.0.2 download; the 1.0.2 code that writes the profile is the pinned Git object.
* Helper regressions (no dotnet): `python3 -m unittest discover -s tests -p test_ap04_run.py -v`.

### Recorded attempts (Root, resume-1011, 12.11.2026)

Evidence under `/home/tomka/mc-evidence/ux103-restpakete-20261111/resume-1011/`:

* Attempt 1 (`ap04-generation.log`, exit 134): pre-save recovery refusal, kept as is.
* Attempts 2-5 (`ap04-generation{2..5}.log`, `ap04-receipt{2..5}.log`): generation
  exit 0, `SOURCE OK ... files=1863 overlay=2`, `RECEIPT OK ... files=34`. The overlay
  and preparer hashes in `released102-src2/UPGRADE-102-SOURCE.json` equal the current
  `Program.cs`, `UpgradeFixtureGenerator.csproj` and `prepare.py`.
* Attempts 2-4 failed in the export-language comparison (later fixed in the test).
* Attempt 5 (`ap04-consumer5.log`, `ap04-consumer-trx5/`, `released102-cycles5/`):
  13/13 passed, the upgrade fact executed (not skipped), cycles 0/1/2 and
  `current-result.json` present. Current side stamped `1.0.2+e3569eae`, run from the
  dirty root checkout with the test change committed afterwards as `d959026b`, so
  it is not a frozen clean run. `check_migrated.py` on it: `archive=5->12
  journal=none->1 private-tags=absent booru-catalog=absent`, manifests `v4:unchanged`.
* Not carried forward: later commits changed archive/tag/journal/Danbooru/host code
  (Danbooru catalog, private-tag format 2 for loose files, journal pruning). A fresh
  attempt on the frozen candidate is required.
* First `ap04_run.py` attempt (13.11.2026, not part of resume-1011):
  `/home/tomka/.cache/claude-ssd-tmp/ap4/full-20261113-024722`, candidate `6f4dfe91`,
  `--allow-no-package`. Preflight and `SOURCE OK ... files=1863 overlay=2` passed; then
  `released-restore` was killed after 1000 s while still waiting for `heavy.lock` (its log
  holds only the command line, no output and no `EXIT`), everything later was skipped and
  the verdict was `FAIL`. Nothing was restored, built or opened. The lock wait is now bounded
  and reported on its own, and a source-only run can no longer end in `PASS`.

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
installers, UI, live HTTP, downgrade or OS matrix proof is supplied. Migration/
two-restart proof for the current HEAD remains pending execution of the existing
consumer and assertions above by Root under its serial lease. Do not pass this receipt to
the incompatible v1x `Release102UpgradeTests`. No PASS for the current HEAD is claimed;
the recorded attempts above are bound to their own revisions. The user chose **1.1.0** for the next release; this bounded generator
fix neither bumps versions nor closes remaining implementation/acceptance gates,
and authorizes no push, upload, tag or release.
