# Official source plugins — implementation report

Role: build_official, baseline `dc053f8`, isolated worktree
`/home/tomka/Desktop/oChan-1.0-workers/official`, 2026-10-22.

## Delivery

- `9febe52`: SDK-only `FourChanPlugin` / `EightKunPlugin`, standalone editable
  `.cs` sources and prebuild projects under `plugins/Official`.
- Plugin IDs `meowchan.official.4chan` / `meowchan.official.8kun`, source IDs
  `4chan` / `8kun`, assemblies `MeowChan.Plugin.FourChan.dll` /
  `MeowChan.Plugin.EightKun.dll`. Version `1.0.0`, SDK `1.0.0`, empty settings
  schema `1`, decimal post ordering.
- Parser code inside each complete plugin source is linked unchanged into
  libmeowchan with `MEOWCHAN_CORE`, excluding its SDK wrapper. No duplicated
  library parser, no SDK/worker/compiler dependency in libmeowchan.
- With build_adapter's explicit agreement, the four existing Board/Thread
  classes now consume these same parsers; unused `ProviderPostCapture.Parse`
  removed. Library lifecycle, HTTP policy, media paths, legacy paths, capture,
  generations and public provider registration remain host/library concerns.
- Each plugin uses only HostHttp for metadata. DTOs contain source identities,
  inert HTML with presence flags and original attachment metadata, never local
  paths or watch instructions. Media ID/extension/name/MD5/size and 8kun extra
  attachment positions are retained. 4chan archive includes final capture;
  8kun locked/archived flags stay active. Only existing thread-404 semantics
  become NotFound. No implicit retry, 304-empty snapshot, or catalog-omission
  termination.

## Required root integration

1. Add to `libmeowchan/libmeowchan.csproj`:
   `<Import Project="../plugins/Official/LibraryParsing.targets" />`.
2. Exclude `plugins/**/*.cs` from the Desktop recursive Compile glob.
3. Register both projects and the test project in the desired solution/build
   graph. Prebuild and package their DLLs plus their respective editable `.cs`.
4. Desktop and Worker integration belongs to build_adapter/build_runtime.
   These owners received entry/assembly/source IDs and the early commit.

Central project/solution files were intentionally not edited in this worker.
Local validation imports the same parser target using
`-p:CustomAfterMicrosoftCommonTargets=/home/tomka/Desktop/oChan-1.0-workers/official/plugins/Official/LibraryParsing.targets`.
No central lockfile was modified; new projects include their own lockfiles.

## Executed validation

All dotnet operations used the global lock and disabled node reuse:
`flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 ... -nodeReuse:false`.

- Release build of both official plugin projects, SDK, libmeowchan and own test
  project: PASS; zero warnings in the successful run.
- `dotnet test tests/MeowChan.OfficialPlugins.Tests/MeowChan.OfficialPlugins.Tests.csproj -c Release --no-restore ...`:
  **17 PASS, 0 failed, 0 skipped**. The suite compares actual library archive
  captures/catalogs against plugin results from the same synthetic fixtures;
  validates SDK DTOs/descriptions; covers aliases/canonical IDs, source-specific
  filename fallback, 304 baselines/validators, 404, nonterminal error statuses,
  4chan archive versus 8kun lock, extra attachments/deletion markers, HTML,
  missing/null/empty fields, deferred/failure forwarding, cancellation,
  malformed and oversized metadata, and SDK-only entry assemblies.
  Artifact: `test-results/official-fixtures.trx`.
- Existing core regression run on `tests/MeowChan.Tests/MeowChan.Tests.csproj`,
  filter `ProviderHttpIntegrationTests|ProviderDescriptorTests|ContentRuntimeTests|ContentRevisionTests`:
  **50 PASS, 0 failed, 0 skipped**. Includes real library HTTP-policy deadlines,
  cancellation, 304-before-snapshot rejection, media-404 pauses/retry, final archive
  drain, 8kun lock and original capture/media revision behavior. One existing
  unrelated xUnit2031 warning in `BoardAdmissionTests.cs:25`.
  Artifact: `test-results/core-source-parity.trx`.
- `git diff --check`: PASS.

Fixtures are synthetic extensions of existing repository tests, with provenance
in `tests/MeowChan.OfficialPlugins.Tests/Fixtures/README.md`. Every profile is an
explicit fresh temporary directory. No personal profiles or displays, no live
board crawl and no original-media request were used for the new parity checks.

## Boundaries

This proves standalone SDK plugin behavior and direct-library parser/capture
parity. It does not claim the full child-worker/Desktop/upgrade/release matrix;
that integration is owned by adjacent roles and Root. Official plugins do not
perform the library's optional `boards.json` display-name lookup, because the
SDK has no board-list/name operation; source catalog capability is unaffected.
Missing title remains absent in plugin DTOs so the host can preserve known
metadata and use `Thread {id}` solely as a display fallback.
