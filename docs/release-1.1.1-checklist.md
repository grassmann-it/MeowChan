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
- [ ] Run **one full** [Desktop and library validation workflow](../.github/workflows/dotnet-desktop.yml) for the final SHA when it is available to Actions. Use `platform=all` and `scenario=all`. Choose one mode:
  - `mode=validate` for the full sweep with the current tooling. Verify all required Windows, Linux and macOS lanes, offline aggregate gates and applicable packaging jobs.
  - `mode=stable-candidate` for the full sweep and stable package admission once the AP04 target supports 1.1.1. This mode also requires same-run AP04 PASS, candidate inventory, source bindings, checksums and the release-candidate gate. See the [prior release checklist](releases/RELEASE-CHECKLIST-1.1.0-2026-11-16.md#2-required-final-github-actions-run).
- [ ] Inspect the validation-gate JSON and executed test reports for failures or skips of required checks.
- [ ] For the chosen mode, keep the run ID and attempt, tested SHA, gate reports, test results and applicable artifact checksums together. Earlier runs and focused retries are diagnostic evidence only.
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
- [ ] Check the Tagging header, progress and collapsed stamp, selection actions, **Without tags** and resume after restart. If available, use a copy of a dogfood v3 queue to check v2 writeback on the first edit.
- [ ] Check the gallery footer with mouse and keyboard. Hover the transport and navigation controls, leave them idle until they hide, and bring them back. Check that tooltips do not block clicks while viewing images and playing local video.
- [ ] Keep screenshots from both OSes of the Downloads row focus, Reader/Media toolbar switches, Booru tabs and gallery, Tagging header and visible gallery footer. Record motion and restart behavior separately in the acceptance notes.

## Known limitations and open items

- `validate` provides the full validation sweep but does not produce the inventory-bound stable candidate or its AP04 result. The AP04 stage and runner under `tests/fixtures/upgrade-v1.0.2/` still expect 1.1.0. An upgrade from 1.0.2 to 1.1.1 via that package gate remains open.
- Booru result restoration is bounded by the profile settings. Bulk download is bounded by the provider’s page limit. Live-provider behavior and native input are part of the pending acceptance.
- Current macOS packages are ad-hoc signed and require manual approval when Gatekeeper blocks first launch. The separate Homebrew formula pins a historical preview source and bundle version.
- The Windows application manifest still declares `MeowChan.Desktop` identity 1.0.0.0. About and package versions come from managed build metadata. Whether the manifest identity should follow future product releases is open. The plugin SDK compatibility contract remains 1.1.0.
- The public downloads, README and platform support documentation continue to refer to the published 1.1.0 assets during preparation. Any 1.1.1 publication metadata belongs to the later release step.

## Version audit

| Location | Result |
| --- | --- |
| `Directory.Build.props`, About, Windows ZIP, macOS bundle, Linux assets and NuGet packages | The product version is 1.1.1 in the shared build properties. About reads the assembly version metadata. The current packaging workflow takes the version from MSBuild. Verify the resulting filenames and embedded metadata during acceptance. |
| Desktop and test lockfiles | Updated internal project ranges from 1.0.2 or 1.1.0 to 1.1.1 in the six root Release locks and the affected test and probe locks. External package versions and hashes are unchanged. |
| Published help | Removed a stale statement that no 1.1.0 package was available. The German Danbooru guide links to the 1.1.1 changes. |
