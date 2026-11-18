# Release checklist — MeowChan 1.1.0

**Date:** 2026-11-16  
**Release type:** stable feature release  
**Release owner:** ____________________  
**Candidate SHA:** ____________________  
**Candidate CI run:** ____________________

This is a go/no-go checklist, not a claim that the release is ready. Check every
required item against the **same immutable candidate SHA**. A green workflow job
is necessary but not sufficient when a step uses `continue-on-error`; inspect the
release-gate report and uploaded evidence as well.

## 1. Candidate and source identity

- [ ] `Directory.Build.props` reports version `1.1.0` in the candidate build.
- [ ] The candidate SHA is recorded in every package/source evidence file and is
      identical across source, packages, upgrade fixtures, and native artifacts.
- [ ] The branch is frozen before the final run; no merge, force-push, or Claude
      worktree changes are introduced after the run starts.
- [ ] The final candidate is tagged exactly `v1.1.0` (not a preview tag), and the
      tag points to the SHA validated by the final run.
- [ ] The GitHub release is created from `v1.1.0`, with the release notes reviewed
      and the release status set deliberately (draft/private first if needed).

## 2. Required final GitHub Actions run

Dispatch `.github/workflows/dotnet-desktop.yml` with:

```text
mode=stable-candidate
platform=all
scenario=all
```

This is the supported current-source `1.1.0` assembly path: `current-source-inventory`
uses `scripts/assemble-prerelease.py --channel stable` with the repository version.
It produces Actions artifacts, not a published release. Do **not** use or change
`upload-verified-release.yml` or `assemble-stable-release.py`; those intentionally
remain `0.9.0`-only.

Record the full candidate SHA, run URL/ID and attempt above; the dispatch ref must
resolve to that SHA. All required jobs and artifacts must come from that same
complete run/attempt, not earlier runs or focused retests, even at the same SHA.
The run must finish without cancellation, timeout, skipped required jobs, or
failed steps hidden by `continue-on-error`. The required matrix is:

- [ ] `validate (ubuntu-24.04, lane-a, true)` — full lane.
- [ ] `validate (ubuntu-24.04, lane-b, false)`.
- [ ] `validate (ubuntu-24.04, lane-c, false)`.
- [ ] `validate (ubuntu-24.04, lane-d, false)`.
- [ ] `validate (ubuntu-24.04-arm, lane-a, true)` — full lane.
- [ ] `validate (ubuntu-24.04-arm, lane-b, false)`.
- [ ] `validate (ubuntu-24.04-arm, lane-c, false)`.
- [ ] `validate (ubuntu-24.04-arm, lane-d, false)`.
- [ ] `validate (windows-2022, lane-a, true)` — full lane.
- [ ] `validate (windows-2022, lane-b, false)`.
- [ ] `validate (windows-2022, lane-c, false)`.
- [ ] `validate (windows-2022, lane-d, false)`.
- [ ] `validate` on `macos-15` (Apple Silicon): `native` (`full=true`,
      `mac_shard=1`) and `native-02` through `native-08` (`full=false`,
      `mac_shard=2` through `8`) all pass.
- [ ] `validate` on `macos-15-intel` (Intel): the same eight native lanes/shards
      all pass; a single `native` lane is not complete macOS coverage.
- [ ] `macos-offline-gate` passes exact source/inventory and executed TRX coverage
      for all eight shards on each macOS architecture.
- [ ] `desktop-offline-gate` passes exact source and four-lane coverage for both
      Ubuntu architectures and Windows, including retained project ownership.
- [ ] `library-package` is successful and its package-consumer evidence is
      present.
- [ ] `macos-package` is successful for both macOS runtimes.
- [ ] `package-media` is successful for every configured runtime and its packaged
      playback/workflow smokes pass.
- [ ] `linux-distribution` is successful for x64 and ARM64, including all three
      distribution gates and source archives.
- [ ] `macos-source-candidate` is successful and pairs the exact source overlay
      with both native DMGs.
- [ ] `current-source-inventory` is successful; its legal files, managed-identity
      and asset-inventory checks are retained.
- [ ] `ap04-upgrade` passes the full offline upgrade and write/readback gate
      against the same-run inventory-bound Linux x64 packages (see section 4).
- [ ] Every validation cell's `artifacts/build-evidence/release-validation-gate.json`
      reports all steps required for that cell as passed; inspect underlying
      outcomes and evidence, not just job `success` after `continue-on-error`.
- [ ] `release-candidate-gate` is successful and its
      `artifacts/release-candidate-gate.json` records success for all ten required
      dependencies listed above, from `validate` through `ap04-upgrade`.
      An assembled inventory alone is not candidate admission.

Generic `package` may be skipped in `stable-candidate` by design; it is not a
`release-candidate-gate` dependency. Windows comes from `package-media`, Linux
from `linux-distribution`, and macOS from `macos-package`. Optional
`homebrew-macos` is not a substitute for these required native/package jobs.
Run it separately if Homebrew publication is part of 1.1.0.

## 3. Platform, package, and runtime evidence

For the exact candidate SHA, download and archive the same-run artifacts before
cleanup (candidate/gate artifacts can expire after seven days). Retain artifact
names/IDs, run URL/ID and attempt, source bindings, reports, logs and actual TRX
results—not screenshots of green jobs alone.

- [ ] Archive `validation-*`, `desktop-offline-*`, `macos-offline-*`, both
      `desktop-offline-coverage-<SHA>` and `macos-offline-coverage-<SHA>` reports,
      and `release-candidate-gate-<SHA>`; verify the reports and executed coverage.
- [ ] Archive `stable-candidate-<SHA>` and `candidate-binding-<SHA>` with
      `candidate-inventory.json` and `SHA256SUMS`; verify full SHA/run binding and
      independently check every assembled asset hash. Retain
      `current-source-inventory-evidence-<SHA>` and producer evidence:
      `library-package-candidate`, library/plugin package-consumer evidence,
      `native-media-candidate-win-x64`, `linux-distribution-candidate-linux-*`,
      `macos-native-candidate-osx-*`, and `macos-paired-candidate-<SHA>`.
      Missing evidence or mismatched source/run/checksums is NO-GO.

- [ ] Windows x64 ZIP exists, extracts successfully, has the expected apphost,
      bundled plugins and native video runtime, and passes the extracted-package
      checks.
- [ ] Linux x64 and ARM64 artifacts include the published app, AppImage and
      corresponding source archive; the package manifests bind them to the
      candidate SHA.
- [ ] Each Linux distribution passes the no-VLC, system-VLC and native-CPU
      distribution gates.
- [ ] macOS ARM64 and Intel ZIP/DMG artifacts exist, mount/copy successfully,
      contain the expected app structure, and pass apphost/workflow/playback
      checks where enabled. Keep shipping Release artifacts distinct from the
      separate non-shipping Debug acceptance builds used by native smokes; those
      results do not establish final Release native/manual acceptance.
- [ ] The library, Plugin SDK and Plugin Host `.nupkg` files are present and
      fresh-cache external consumers pass.
- [ ] Plugin delivery is verified in both desktop and graphical-harness output.
- [ ] SHA-256 files/manifests are retained and hashes are independently checked
      before uploading or publishing.
- [ ] Artifact names and versions are `1.1.0`; no preview suffix or artifact from
      another SHA is included in the public release.

## 4. Upgrade and compatibility gates

- [ ] The genuine `1.0.0` replay executes exactly once and passes.
- [ ] The genuine `1.0.1` replay executes exactly once and passes.
- [ ] The released `1.0.2` fixture is generated/validated from the pinned release
      source and profile, including receipt/source-binding checks.
- [ ] The current consumer build for the `1.0.2` fixture succeeds without using
      an untracked local project output.
- [ ] The `1.0.2` main upgrade test produces exactly one TRX result with outcome
      `Passed`; migrated-state checks also pass.
- [ ] `ap04-upgrade-<SHA>` retains passing `upstream-gate.json`, `gate.json`,
      `required-steps.json`, `ap04-stage.json`, full AP04 reports, cycle/write-readback
      evidence, logs and TRX results. Confirm the full replay passed (not a bounded
      attempt only) and its staged packages match the same-run inventory,
      `SHA256SUMS`, candidate SHA and run ID.
- [ ] Upgrade receipts and TRX files are retained in the final CI artifacts.
- [ ] No upgrade test is merely discovered, skipped, or reported green without an
      executed passing test result.

## 5. Documentation, legal files, and release communication

- [ ] Release notes cover the new 1.1.0 features, UX/workflow changes, known
      limitations, platform support, and upgrade notes from 1.0.2.
- [ ] The factual feature inventory has been reconciled with the final product;
      no planned, speculative, or unshipped feature is advertised as shipped.
- [ ] `Help.md` and in-app help/shortcut text match the final UI and shortcut
      defaults; language-review changes are included or explicitly deferred.
- [ ] Third-party notices, licenses, `CC0-1.0` fixture attribution, and bundled
      VLC/FFmpeg/native-runtime notices are present in the source and package
      where required.
- [ ] Privacy/update/network wording is reviewed for a public release; no test
      fixture, private path, token, local hostname, or internal review document is
      included in public assets.
- [ ] The release notes and changelog identify the exact version `1.1.0` and link
      to the public tag/release after publication.
- [ ] The final public asset list has been reviewed by a second person.

## 6. Signing, provenance, and artifact publication

- [ ] Windows artifact provenance/signing requirements are decided and met (or
      the release explicitly states that the artifact is unsigned); verify the
      actual published file, not only a source-build check.
- [ ] Linux archive/AppImage provenance and checksums are signed or otherwise
      published through the project's documented trust channel.
- [ ] macOS signing is checked on the final DMGs. The workflow's current
      `--sign-adhoc` path is **not** Developer ID signing, notarization, or
      Gatekeeper acceptance.
- [ ] Before calling macOS distribution release-ready, a separate owner records
      Developer ID signing, notarization, stapling, and Gatekeeper acceptance—or
      explicitly approves an ad-hoc/private distribution with that limitation in
      the release notes.
- [ ] Checksums are generated from the final uploaded artifacts and verified after
      upload/download.
- [ ] GitHub release assets are uploaded only after the candidate SHA, tag,
      checksums, package versions, and license inventory agree.
- [ ] Rollback plan is recorded: leave the release draft/private, remove assets,
      or delete/move the tag without publishing a mismatched build.

## 7. Current evidence and explicit unknowns (as of 2026-11-16)

**Current status: NO-GO until resolved.** The following are facts to verify again
rather than assumptions:

- The repository version is `1.1.0`, but no remote `v1.1.0` tag was present when
  this checklist was written.
- Current reported `main` is `ac97d76cc` (queue fix pushed), with UI changes at
  `4dd056e2c`. These short references are context, not final candidate evidence:
  record the full immutable SHA and final run above. Full suites and smokes have
  not yet been completed for this tip; no passing result is claimed here.
- Earlier candidate runs are not final evidence for the current source. A new
  complete same-SHA, same-run gate and artifact set is still required.
- Earlier runs contained native GUI, package-fixture, upgrade-replay, and macOS
  watchdog/lifecycle failures. Later fixes must be covered by one uncancelled
  stable-candidate run; old green sub-runs must not be mixed into its evidence.
- macOS native/package checks have historically been interrupted by cancellation
  or watchdog timeouts. A complete ARM64 and Intel result is still required.
- The workflow currently uses ad-hoc macOS signing and explicitly makes no
  Developer ID, notarization, or Gatekeeper claim. The distribution decision is
  unresolved until an owner signs off.
- The current public-release asset set, checksum/signature policy, and GitHub
  release/tag owner are not recorded here yet.
- Windows Authenticode and Linux artifact signing status are not established by
  the workflow alone.
- User-facing docs/help translations and the final feature inventory need a
  last comparison against the exact candidate, not merely against a prior wave.

### Final decision

- [ ] **GO** — all required boxes above are checked, the final candidate run is
      terminal and green, the evidence is archived, the signing/distribution
      decision is documented, and the tag/assets point to the same SHA.
- [ ] **NO-GO** — any required job, fixture, package, docs/legal, provenance, or
      signing check is missing, failed, cancelled, timed out, or tied to another
      SHA.

**Final approver:** ____________________  **Date/time:** ____________________
