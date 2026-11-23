# Release checklist — MeowChan 1.1.1

Preparation for [MeowChan 1.1.1](releases/v1.1.1.md). The source notes cover `v1.1.0..1.1.1`. The prior release tag resolves to `8806763ef`; the preparation branch started at `e16fc474c`.

| Evidence | Record |
| --- | --- |
| Final candidate SHA | Pending |
| GitHub Actions run URL, mode and attempt | Pending |
| Tom’s Windows and macOS acceptance, with screenshots | Pending |
| Dogfooding results | Pending |

## Gates

- [ ] Run the combined local filter below once on the final candidate. Record the SHA, SDK version, test totals and log. The preparation run passed 849 tests with SDK 10.0.112 from the local `global.json` override. CI uses the committed pin, 10.0.111.
- [ ] Once everything intended for the dogfood build is merged, push the candidate and start **one full** [Desktop and library validation workflow](../.github/workflows/dotnet-desktop.yml) in the background with `platform=all` and `scenario=all`, mode `validate`. Tom dogfoods while it runs. The run does **not block** the release (Tom's decision on 22 November, as for 1.1.0, where validation mostly found fragile tests). A red job holds the release only when it shows a real functional failure; a flaky or load-dependent test, or a platform quirk of a test, is recorded and fixed afterwards. `mode=stable-candidate` stays out of scope until the AP04 target supports 1.1.1.
- [ ] Inspect the red jobs of that run once: classify each as functional failure or fragile test, and record the run ID, the tested SHA and the classification. Earlier runs and focused retries are diagnostic evidence only.
- [ ] Tom signs off Windows and macOS on the same candidate SHA and records any differences by OS, architecture, display scale and package.
- [ ] Tom dogfoods that candidate with a copy of a representative 1.1.0 profile. Use the usual download → Reader → gallery → tag → resume flow and a Booru search across restarts. Record the profile origin, observed results, regressions and disposition before sign-off.

```sh
dotnet test tests/MeowChan.Tests --filter "FullyQualifiedName~Release|FullyQualifiedName~Changelog|FullyQualifiedName~Version"
```

## Tom’s Windows and macOS acceptance

- [ ] Extract the Windows x64 ZIP and open the packaged app. Mount the macOS DMG for the machine’s architecture, copy the app to Applications and open it using the [first-launch guidance](macos-signing.md) if needed. Record the macOS architecture actually exercised. Confirm **About** shows 1.1.1 and record the package filename and checksum.
- [ ] Check Downloads tabs and toolbar alignment, keyboard focus across cells, and the row context menu in wide and narrow windows.
- [ ] Check the Reader and Media sidebar switches with mouse and keyboard in wide and narrow windows. Open and close Find and Tags, follow a later-line link or post reference, then return from the gallery and check attachment previews and scroll position.
- [ ] In Booru, open and duplicate a search tab, try both search destinations, close Watchers, switch List/Grid/Gallery and navigate to both ends of a result page. Restart and verify search and result restoration. Try one original download and inspect the outcome.
- [ ] Check the Tagging page: the header with the primary Continue action and progress, the collapsed stamp summary with its popover editor, the two-action selection bar with its More menu, the compact filter row, **Without tags** and resume after restart. If available, use a copy of a dogfood v3 queue to check v2 writeback on the first edit.
- [ ] Check the gallery footer with mouse and keyboard. Hover the transport and navigation controls, leave them idle until they hide, and bring them back. Check that tooltips do not block clicks while viewing images and playing local video.
- [ ] Keep screenshots from both OSes of the Downloads row focus, Reader/Media toolbar switches, Booru tabs and gallery, Tagging header and visible gallery footer. Record motion and restart behavior separately in the acceptance notes.

## Known limitations and open items

- `validate` provides the full validation sweep but does not produce the inventory-bound stable candidate or its AP04 result. The AP04 stage and runner under `tests/fixtures/upgrade-v1.0.2/` still expect 1.1.0. An upgrade from 1.0.2 to 1.1.1 via that package gate remains open.
- Booru result restoration is bounded by the profile settings. Bulk download is bounded by the provider’s page limit. Live-provider behavior and native input are part of the pending acceptance.
- Current macOS packages are ad-hoc signed and require manual approval when Gatekeeper blocks first launch. The separate Homebrew formula pins a historical preview source and bundle version.
- The Windows application manifest still declares `MeowChan.Desktop` identity 1.0.0.0. About and package versions come from managed build metadata. Whether the manifest identity should follow future product releases is open. The plugin SDK compatibility contract remains 1.1.0.
- The public downloads, README and platform support documentation continue to refer to the published 1.1.0 assets during preparation. Any 1.1.1 publication metadata belongs to the later release step.

## Release assets and notes (Tom, 22 Nov)

The 1.1.1 release page follows the clearer 1.0.2 layout rather than the 1.1.0 one:

- The 1.0.2 page shows readable **asset labels** ("Windows x64 (.zip)", "macOS Apple Silicon (.dmg)", ...) in place of file names; the 1.1.0 assets have none and show raw file names. `upload-verified-release.yml` now uploads with these labels, and `scripts/label-release-assets.sh <tag>` sets them on an existing release (run it after publication if a label is missing).
- Application downloads keep the `01-`..`09-` order and readable names (`01-MeowChan-1.1.1-win-x64.zip`, `02-...-osx-arm64.dmg`, ...). Nothing else sorts before them.
- The release text carries a **Downloads** table with a plain platform label per package (Windows x64 .zip, macOS Apple Silicon .dmg, macOS Intel .dmg, Linux x64/ARM64 AppImage, Linux bundled-VLC and system-VLC .tar.gz) and a direct link to each file, followed by the `SHA256SUMS` link and a one-line note on system VLC requirements.
- Supporting files (`candidate-inventory.json`, the NuGet packages, the corresponding video-source archives) are explained in one sentence so no asset name is cryptic. The 1.1.0 page lacked the table and these explanations.
- Check the final asset list and every link against the uploaded names before publishing; Tom publishes.

## Handling red tests before the release (23 Nov)

- A red test is **not chased by default.** If it passes in isolation and the cause is interference between tests (shared shortcut, dispatcher or footer state), record the cause in the validation note; it does not block.
- A red test is **fixed** when it shows a real product defect, a layout failure in a GUI smoke, or when the test fix is small.
- Follow-ups for 1.2.0: derive the localization audit counts from the built catalog instead of literals; replace tests that read source text of a smoke with checks on the product.

## Version audit

| Location | Result |
| --- | --- |
| `Directory.Build.props`, About, Windows ZIP, macOS bundle, Linux assets and NuGet packages | The product version is 1.1.1 in the shared build properties. About reads the assembly version metadata. The current packaging workflow takes the version from MSBuild. Verify the resulting filenames and embedded metadata during acceptance. |
| Desktop and test lockfiles | Updated internal project ranges from 1.0.2 or 1.1.0 to 1.1.1 in the six root Release locks and the affected test and probe locks. External package versions and hashes are unchanged. |
| Published help | Removed a stale statement that no 1.1.0 package was available. The German Danbooru guide links to the 1.1.1 changes. |
