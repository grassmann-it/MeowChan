# Platform requirements and release evidence

This record separates upstream platform support, candidate MeowChan packages, and checks actually performed. A successful cross-publish does not establish that an application starts on the target operating system or CPU.

## Final 0.9.0 platform packages

The accepted platform packages have explicit, independently verified source
boundaries:

- **Windows x64 and macOS Apple Silicon/Intel:** the [native package run at
  `b0888df`](https://github.com/tfgrass/MeowChan/actions/runs/39696624077) passed.
  Windows and the apps mounted and copied out of both ad-hoc-signed macOS DMGs
  passed startup, real gallery playback and the packaged
  download/restore/export/desktop workflow. These three packages are retained
  unchanged from that run.
- **Linux x64 and ARM64:** the final [Linux-only run at
  `e441308`](https://github.com/tfgrass/MeowChan/actions/runs/39701797723) passed.
  Each CPU passed the bundled-VLC archive, system-VLC archive,
  missing-system-VLC case and AppImage, including extraction and
  extract-and-run, in isolated offline desktop containers. These packages
  include the final Linux browser-link correction; the release tag points to
  this source. The earlier five-platform run remains historical Linux package
  evidence and is not substituted for these final Linux checks.

The final Linux launcher uses the selected known browser's new-window option
for web links, retaining the desktop handler for unknown/unavailable browsers
and other targets. Fourteen focused launcher tests and the isolated About-button
GUI scenario passed, and the Linux user confirmed the new-window behavior after
restarting. Window-manager focus policy still controls foreground activation.

The Linux user also completed a personal click-through acceptance on 22 October
2026. Personal Windows/macOS testing is deferred until before 1.0.0; the native
runner checks do not establish every physical device, audible hardware output,
Gatekeeper approval or desktop environment. macOS remains ad-hoc signed without
Developer ID signing or notarization. The final artifact assembly additionally
checks source identity, package hashes, legal-source pairing and the complete
13-file binary/source inventory plus `SHA256SUMS` before publication.

The functional Linux checkpoint also passed four focused isolated GUI modes
(normal, reader, download session and selection), 14 gallery tests and 47 focused
export/recovery tests. Private KWin image, VP8 and VP9/Opus checks exercised
maximized → fullscreen → maximized restoration and real caption-close shutdown.
These are complementary source-built checks, not additional package platforms.

## Earlier 0.9.0 candidate evidence (historical)

Product checkpoint `161a8b0` contains fullscreen lifecycle corrections:
explicit app resize requests are separate from native resize reports, normal
geometry survives a maximized fullscreen round-trip, and the native resize probe
reserves actual desktop space. The Release build and repeated isolated Linux
selection scenarios pass. The current Linux source passed 644 core and 269
desktop tests, with one expected native-media skip. Earlier native reader/gallery
scenarios passed on Windows, both Macs and both Linux CPUs. Later targeted native
checks and their source boundaries are recorded below.

The [complete package/media matrix at `6e750ec`](https://github.com/tfgrass/MeowChan/actions/runs/39667959802)
passed Windows x64, both Mac DMGs, all three Linux formats on both CPUs, and the
corresponding-source collector. Each DMG was mounted, signature-checked and its
app copied out for startup/playback/workflow tests. These packages predate the
last window corrections and do not attest to the changed product.

The [newer matrix run](https://github.com/tfgrass/MeowChan/actions/runs/39670053190)
was deliberately canceled to stop repeated all-platform builds. The Linux
functional checkpoint is complete: all eight isolated graphical scenarios passed
at `161a8b0`, alongside the offline suites above. The targeted
[Windows run at `6d6c3fb`](https://github.com/tfgrass/MeowChan/actions/runs/39681449053)
and [Intel Mac run at `b76e2f1`](https://github.com/tfgrass/MeowChan/actions/runs/39682508602)
passed their respective native source checks after the window corrections.
The subsequent focused Apple Silicon runs
[`d3c741f`](https://github.com/tfgrass/MeowChan/actions/runs/39684198944) and
[`b1dc9f8`](https://github.com/tfgrass/MeowChan/actions/runs/39684831339) failed.
The latter passed final normal-window restoration but then overwrote later
intended resizes. `e4374dd` restricts that repair to recorded stale native frames;
its isolated Linux selection check passed, but native macOS confirmation remains
open. No successful ARM selection run at `6ae6008` is claimed. These are not
final package results.
Public stable publication is authorized once technical checks and final package
validation pass. No final native acceptance or published stable release is claimed yet.

The [screenshot provenance](screenshots/README.md) identifies the captured UI
checkpoint. A fresh local NuGet consumer and bounded 4chan/8kun
download/search/export/restore checks passed at `3a47804`; subsequent changes
have not modified the library or providers.

The next public release is planned as **stable 0.9.0** after completion and native package checks. No further intermediate release is planned. All desktop variants are self-contained for .NET. Windows x64 retains its ZIP. macOS Intel/Apple Silicon use separate DMGs with `MeowChan.app` and an Applications shortcut; Linux x64 and ARM64 each have three planned choices:

| Planned Linux artifact | Native VLC delivery |
| --- | --- |
| `MeowChan-0.9.0-linux-x64-bundled-vlc.tar.gz` | Private bundled VLC |
| `MeowChan-0.9.0-linux-x64-system-vlc.tar.gz` | System LibVLC 3, version 3.0.20 or newer |
| `MeowChan-0.9.0-linux-x64.AppImage` | Private bundled VLC |
| `MeowChan-0.9.0-linux-arm64-bundled-vlc.tar.gz` | Private bundled VLC |
| `MeowChan-0.9.0-linux-arm64-system-vlc.tar.gz` | System LibVLC 3, version 3.0.20 or newer |
| `MeowChan-0.9.0-linux-arm64.AppImage` | Private bundled VLC |

This is the agreed delivery matrix, not a claim that these artifacts are already built, published or accepted. Bundled tar archives and AppImages avoid a separate VLC installation; system-VLC tar archives are smaller. All still require a compatible native Linux desktop. The tar root contains `MeowChan`; AppImages launch through AppRun. See [startup and AppImage instructions](Help.md#linux-packages-and-startup).

Linux preflight runs **before session startup**: load the selected backend, require VLC major version 3 at least 3.0.20, and initialize an engine. The minimum includes the distro-maintained Ubuntu 24.04 runtime; no API used by MeowChan requires 3.0.21. Native packaged playback remains a separate acceptance gate. Missing, unsupported or unusable system VLC produces a dismissible installation hint, then exit code **2**. Bundled runtime integrity/metadata or load failures require re-extracting the complete package; they never trigger a system-VLC fallback. This check does not guarantee that every media file decodes: unsupported/corrupt files remain per-file preview/playback errors. Linux accepts the runtime ABI `libvlc.so.5`, without development symlinks.

Source `20bc022` limits application archives to the app, runtime, icon and required legal notices/native-source information. README, changelog, user/developer help, screenshots, SDK/test sample documentation, PDBs and XML documentation stay out of desktop payloads; all GitHub documentation remains available. Native dependency trees and legal files must remain intact when extracting. The 16 [interface screenshots](screenshots/README.md) include the final Barebones/Advanced comparison and refreshed gallery, Settings and Rules at functional checkpoint `be2f807`, alongside 11 earlier `b8e1291` captures. They remain source-built UI evidence, separate from package acceptance.

**Evidence boundary:** [package-media run 39630125624](https://github.com/tfgrass/MeowChan/actions/runs/39630125624) passed all five native OS/CPU package jobs at source `6354f94`, including the macOS collector. Libraryconsumer and bounded live checks also passed for that earlier candidate. It predates the slim payload and new Linux delivery/preflight contract and does **not** validate those changes. Local startup-fix Debug and Release-analyzer builds completed with zero warnings/errors and 44 focused tests passed. The corresponding new native delivery results remain open.

The separate [validate run 39630120998](https://github.com/tfgrass/MeowChan/actions/runs/39630120998) was not fully green: its Windows fixture finding is addressed by `c6a9fb4`; the macOS ARM combined test invocation hung. A dedicated diagnostic run is recorded below. Neither the earlier package pass nor source tests establish full current release readiness, physical audio/hardware, or user acceptance.

## macOS ARM offline-test invocation diagnosis

[Diagnostic run 39640686897](https://github.com/tfgrass/MeowChan/actions/runs/39640686897) passed on native macOS ARM at source `1c34e25`: all 633 core tests and 232 desktop tests passed, with one expected opt-in native-poster test skipped. The two complete suites ran independently using `scripts/macos-test-watchdog.py`, without test filters, and completed in 15.59 and 2.96 seconds. Their logs, TRX and VSTest diagnostics are retained in artifact `12364832695`.

The earlier solution-level invocation with the blame collector hung; this comparison does not distinguish solution-level concurrency from collector behavior. macOS validation now uses the independently bounded project invocations and retains their diagnostics. This is source-test evidence, separate from final graphical/package acceptance.

## Integrated local video playback

Gallery WebM playback uses LibVLCSharp.Avalonia 3.10.2. The video surface and
transport controls occupy separate rows. Windows/Linux use the native video
window; macOS renders LibVLC software frames through an Avalonia surface so
playback does not depend on an accelerated CGL context.
Left/right navigate media; a focused timeline or volume slider retains its own
arrow keys. Playback reads the same retained stream verified by the local media
manifest, stops when changing media or leaving the gallery, and releases that file
before disposal completes. This does not download external media or install VLC.

| Target | Native video runtime |
| --- | --- |
| Linux x64 / Arm64 | Private bundled VLC in bundled tar/AppImage variants, or system LibVLC **3**, version **3.0.20 or newer**, with Matroska/WebM, VP8/VP9, Vorbis/Opus and audio/video output plugins in system variants |
| Windows x64 | Official VideoLAN.LibVLC.Windows 3.0.24, selected `libvlc/win-x64` tree |
| macOS x64 / Arm64 | Official VideoLAN.LibVLC.Mac 3.1.3.2, selected `libvlc/<RID>/lib` tree |

On Debian-derived systems the relevant package families include `libvlc5`,
`libvlccore9`, `vlc-plugin-base` and `vlc-plugin-video-output`; package versions and
names vary by distribution. The Linux loader accepts the runtime ABI name
`libvlc.so.5`; development-package symlinks are not required. Use supported
distribution packages for the system-VLC variant. A required Linux backend failure
is reported before session startup; after startup, individual missing codecs or
unreadable/corrupt files produce per-file gallery/preview errors. No native library
is automatically installed.

All delivery variants bundle the .NET runtime. Only the Linux system-VLC variant
requires a separately installed VLC runtime; the bundled variants still need the
usual native desktop dependencies. Windows/macOS publishes keep
the complete selected VLC plugin/data hierarchy external to the .NET single file.
`scripts/check-video-native.py` verifies architecture and essential plugin presence
in publishes or extracted packages; it does not prove playback on that OS.

The ignored Linux spike exercised VP8/Vorbis, VP9/Opus and silent VP8 through a
verified retained stream, including native frame delivery, pause, seek, stop,
dispose and file release. It used system VLC on Manjaro x64, isolated Xvfb and a
dummy audio sink; it establishes neither real audio/GPU behavior nor Windows,
macOS or Linux Arm64 acceptance. A subsequent private-Xvfb harness compiled the actual production
`GalleryVideoPlayer.cs` and passed all three clips with native displayed frames,
12 real pointer clicks, pause, timeline seek, stop/replay, 18 superseding
stop/show cycles, token cancellation, corrupt-media diagnosis and exclusive file
reopening after cleanup. A separate process blocked `libvlc` loads via a local
`dlopen` test shim and verified the production missing-backend status. Evidence:
`artifacts/r09-video-controller/production-playback-result.json` and
`missing-backend-result.json`; screenshot `screenshots/frame-1.png` visibly shows
native video and the sibling toolbar. The harness injected an engine configured
for dummy audio/software video; real audio/GPU and extracted-package playback
still require their own evidence. Windows redistribution uses the
[companion-source and notices mechanism](native-video-sources.md), with the
source asset published alongside the binary ZIP. macOS source/signing acceptance
remains separate, as recorded in [third-party notices](../THIRD-PARTY-NOTICES.md).

The macOS 0.9.0 standard is separate self-contained ARM/Intel DMGs with
ad-hoc signatures without Apple Developer ID or notarization; certificate acquisition is not a
release prerequisite. The [macOS package guidance](macos-signing.md) explains
manual approval in Privacy & Security and keeps Developer ID/plugin signing,
hardened runtime and notarization as an optional later distribution route.
Historical Mac package/startup checks passed at `672b45d` below; the later
`6354f94` five-target package matrix also passed. Those results retain their
source/package boundaries and do not validate the new delivery/preflight contract.

## Historical preparation checkpoints

**Earlier integration checks:** the delivery contract and evidence boundary
are recorded above. Window, Linux runtime-resolution, macOS software-video and
selection corrections have been integrated; the preceding `6354f94` native package
matrix passed. New Linux variants, mandatory startup preflight and slim payloads
need their own native checks. This checkpoint predates the separately passing macOS ARM diagnostic recorded above. One exact tested Windows package comes before the next
public release, stable 0.9.0.

[Windows x64 Preview 3](https://github.com/tfgrass/MeowChan/releases/tag/v0.9.0-preview.3)
remains the existing historical compatibility-test download, with .NET 10 and VLC.
It does not contain current source changes. The current interface screenshots
are documented in the [screenshot provenance](screenshots/README.md); they are
separate from package acceptance.

**Published Preview 3:** release source/tag `a98759e819f233bee0de806cb4f0b9fbd535d2cd`, prerelease, not latest stable. ZIP 128546741 bytes, SHA-256 `c8b099897adb0041f5f7bd3914c0f355ea8e7db0e64474864a991aa4f104c66c`. Its required native-video source companion and checksums are included in the release.

| Windows source-built gate | Verified result |
| --- | --- |
| [Run 39511988229, job 118935015628](https://github.com/tfgrass/MeowChan/actions/runs/39511988229/job/118935015628) | Job success; Core 628/628, Desktop 146/146 passed |
| Native GUI | Normal, locked-profile, no-tray, tray-failure, download-session and separate tray-lifecycle passed; startup frame fits 1024×728 work area |
| Source relation | Artifact source `d17840bd38e1a50f2ce990dcfc86a1187768f97c`; reviewed product-source/build-input equivalence to ZIP source `a98759e` |

Evidence: `artifacts/r09-windows-preview-3/windows-ci-progress-followup/` and
`ci-product-source-equivalence-d17840b.json`. The workflow as a whole is not green: the Linux job encountered a test-fixture
race; the Mac temporary-path fix was verified separately in the later native
offline run recorded below. The Windows source-built gate does not establish
extracted-ZIP VLC playback, real hardware/audio or full package acceptance.
macOS ZIP and native extracted-app startup subsequently passed at `672b45d`
(below); playback, audio, real hardware and Gatekeeper acceptance remain open.

**Historical Preview 2 finding:** Windows startup could create a window larger than the available screen work area. Published Preview 3 includes the startup-geometry fix and passed the Windows source-built test gate described below. No native video or hardware acceptance follows from this window-sizing check.

**Windows profile handle lifecycle:** The earlier Preview 1 exposed this issue. Default SQLite connection pooling in `WorkStateStore.Open` can retain profile database handles after host disposal and release of the profile lease, preventing profile-directory deletion in the same process. Windows CI reported 41 temporary-directory cleanup failures from this issue. Preview 3 retains the fix that disables pooling for this store and adds a handle-release regression check. The focused source checks passed 83 tests; this establishes their source-level coverage, not native Windows package or hardware acceptance. The cleanup failures do not establish data loss. Only Preview 3 is listed as the current preview; the earlier preview names here identify historical findings, not currently available releases.

**Earlier profile-handle fix-source checkpoint — `f8f769d23e8450b5779732780d27adf2d9fcd848`:** focused tests **83/83**, full Core **628/628** and Desktop **141/141** passed (769 full-suite tests), with no warnings. Logs: `artifacts/r09-windows-preview-1/ci-lifetime-{focused,core,desktop}.log`. These local source results cover the handle-lifecycle fix; the screenshot UI is unchanged since `8d31f2c`. The later Windows source-built CI result is recorded below; this earlier checkpoint alone does not establish it.

**Historical pre-fix product-source checkpoint — `8d31f2c`, 2026-10-20:** locked Release restore passed; the Release solution build completed with **0 warnings and 0 errors**; **627/627 Core and 141/141 Desktop tests passed, none skipped** (768 total). The default Debug build also completed with **0 warnings and 0 errors**. These are source checks, not native Windows package acceptance.

The package names, hashes and results in the historical checkpoint sections below describe their respective earlier source revisions. They do not validate the current preview or final five-platform package matrix. Published builds are on the [MeowChan releases page](https://github.com/tfgrass/MeowChan/releases).

0.9.0 adds Simple, Advanced and Barebones views, local archive/search, a shared gallery, durable missing-media follow-up and compact repeat HTML export alongside appearance/language preferences and batch URL admission. The platform targets and .NET 10 baseline remain unchanged. Local source-built checks, five successful package round-trips and Linux x64 apphost startup/exit are recorded below, separately from historical 0.8.0 results. Full native workflow acceptance of the 0.9.0 extracted packages is still open.

Current acceptance must cover gallery navigation/zoom/context, all-result search sequences, repeat-export update/backup/open-folder behavior, durable 404/terminal recovery and the integrated download/restore flow, all three views, Dark/Light, English/German, keyboard focus, small windows and native rendering at the intended display scales. The [release-notes draft](releases/v0.9.0.md) records user-visible behavior and remaining boundaries.

The subsequent compact-layout/menu-contrast correction passed a Release build without warnings or errors and 90 desktop tests. Final source-built Linux checks passed 5/5 scenarios at measured 100% and 150%, and 4/5 at 200%: `artifacts/ap13-layout-final-scale-{100,150,200}` (14/15 runs, 72 captures). All presentation checks passed, including six passive comparison rows, source/board and numeric sorting, minimum window sizes, and separately rendered Dark/Light context menus with asserted icon colors. The earlier `ap13-layout-correction-v3-scale-*` runs passed 15/15, but do not supersede the later failure.

**Linux tray lifecycle correction:** The 14/15 layout checkpoint above is historical. Avalonia 11.3.22 could continue asynchronous DBus tray creation after disposal removed its handler. Commit `53162b9` upgrades to Avalonia 12.1.4, which includes [upstream fix #21980](https://github.com/AvaloniaUI/Avalonia/pull/21980), with Actipro 26.2.0, DataGrid 12.1.2 and the transitive Tmds.DBus.Protocol 0.94.1. Icons now attach to the application's collection as required by Avalonia 12, and each window removes its entry on shutdown. The previous direct DBus pin is removed; locks and licensing notices are refreshed.

**Historical pages and local source evidence — `53162b9`:** `0bbbfd5` extracts the shared settings editor, and `ae1b3f9` adds retained Downloads/Settings pages in one shell. Save/Cancel remain on Settings; footer actions preserve unrelated draft fields. The final source in `53162b9` passed 457 .NET tests and the 22 macOS-packaging fixtures. All six native Linux scenarios passed at measured 100%, 150% and 200%: **18/18**, retained in `artifacts/ap13-pages-final-scale-{100,150,200}`. The new tray-lifecycle scenario checks 16 rapid native windows per scale, confirms actual native tray attachment, checks collection cleanup and reacquires each profile lease. Normal mode also covers invalid draft navigation, footer failures, accepted save/picker shutdown, language refresh and small Settings pages; the download scenario completes media while Settings is visible and retains both page states.

The local host logged unavailable AT-SPI/global-menu services as warnings; native accessibility/global-menu integration is not established by that evidence. The Xvfb attempt at that checkpoint was unavailable because the tools were not installed. They have since been provided as verified, locally extracted Manjaro packages; the new isolated evidence is recorded separately below.

**Workstation freeze and test isolation:** After the framework checkpoint, the user reported a desktop freeze during subsequent style checks. The last native run (`artifacts/ap13-styles-final-scale-100`) passed only 2/6 scenarios, with window-size/minimization timeouts. Its relationship to the freeze is unproven. Do not infer resolution from the earlier 18/18 pass or use the active personal desktop for automatic GUI suites.

Commit `d355152` isolates Linux runs in Xvfb/Openbox with a private DBus session that cannot activate host services, private XDG directories and cleared inherited desktop endpoints. Standard scenarios stop at the first failure; stress requires a separate explicit invocation. Windows/macOS and Linux native-session runs require explicit opt-in on a disposable test desktop. Nine window-free runner fixtures verify the selection, fail-fast, timeout, process cleanup and evidence behavior. Isolation is process/display ownership, not a security sandbox; the [harness instructions](../tests/MeowChan.DesktopSmoke/README.md) explain the limits.

Native Windows/macOS, Linux Arm64 and full extracted-package workflows remain open. The earlier archives listed below predate the icon, pages, framework and style changes. Cross-publish alone cannot replace their native acceptance.

### Bounded live-provider check — 2026-10-10

Source checkpoint `c7ef581` was additionally exercised against actual public
4chan and 8kun endpoints through the built-in adapters, production HTTP policy,
registry, download queue, media store and archive runtime. No synthetic response
handler supplied the content. The check used two workers, an eight-reservation
pending limit, JPG/PNG selection and a 200,000-byte metadata size limit in isolated
profiles. Public catalog/thread metadata was inspected first to select small
threads; only two original media files were downloaded in total.

| Source and live thread | Captured posts | Verified files / bytes | Recheck | Fresh-process restore |
| --- | --- | --- | --- | --- |
| 4chan `/g/110029602` | 2 | 1 / 28,512 | Actual conditional HTTP 304 | 1 saved medium, zero media HTTP requests |
| 8kun `/tech/1087224` | 3 | 1 / 96,313 | Actual HTTP 200 | 1 saved medium, zero media HTTP requests |

The first media responses were HTTP 200 from `i.4cdn.org` and
`nerv.8kun.top`. The stored files were verified through the independent manifest
reader. Manual checks completed, pause rejected a manual request, local archive
search found each thread, and both real captures exported with their saved medium
and zero attachment omissions. New providers/queues/stores/sessions reused the
files; an additional, separately launched .NET process restored both sessions and
again issued no media requests.

Local request/status evidence and results are retained under
`artifacts/live-provider-check-20261010/`, including `4chan-result.json`,
`8kun-result.json`, `fresh-process-restore.json` and `results.json`. At the user's
request the two verified watches and their captured posts were merged into the
normal desktop profile, preserving its previous watch and settings. The media
remain in their assigned live-test folders; a normal `dotnet run` exposes them in
Downloads and Archive. Backups of the previous profile files are retained with the
evidence. The personal desktop was not opened or automated.

This is a small live-source smoke check, not a 32-worker stress or long-running
source test. These particular captures contain no parsed quote anchors, so live
quote/backlink navigation is not established by this run; its existing acceptance
uses controlled GUI/catalog fixtures. Native release-package acceptance and real
NAS-disconnection behavior remain separate.

### Inline archive images — 2026-10-10

Archived posts now show viewport-loaded, bounded local thumbnails with at most two concurrent verification/decoding operations. Matching uses captured media identity, variant, size and MD5 when supplied; the manifest reader verifies the selected file's SHA-256 before decoding. The Debug smoke build and Release desktop build passed without warnings/errors; all 103 desktop tests passed. Isolated Linux download-session GUI runs at measured 100% and 200% passed, including inline-image loading and click-to-preview, compact Barebones/Simple/Advanced views in English/German, delayed media discovery and disposal on detachment. Evidence is retained in `artifacts/archive-inline-accepted-{100,200}`. These checks use controlled downloaded-image fixtures; they do not establish native Windows/macOS behavior, real NAS disconnection or a sustained live-source load.

### Isolated style checkpoint — `1884ff5`

The final style source passed the Release solution build without warnings/errors and all **457 .NET tests** (367 core, 90 desktop). The smoke build, 22 macOS-packaging fixtures and nine window-free runner fixtures also passed. Linux GUI checks used Xvfb/Openbox, software rendering, a private non-activating DBus session and temporary profile/download directories; no personal desktop windows or tray stress were started.

| Scope | Observed scale | Result | Evidence directory |
| --- | --- | --- | --- |
| Visual comparison plus five standard scenarios | 1.0 | 6/6 passed | `artifacts/ap13-styles-isolated-final-100` |
| Normal presentation/settings after harness synchronization fixes | 1.0 | Passed | `artifacts/ap13-styles-isolated-final-100-normal-v2` |
| Normal presentation/settings | 1.5 | Passed | `artifacts/ap13-styles-isolated-final-150-v3` |
| Normal presentation/settings | 2.0 | Passed | `artifacts/ap13-styles-isolated-final-200` |

The first 150% run retained a stale pre-resize restore target; the next reached a right-click check that used stale row coordinates after sorting. Both failed runs are retained in `artifacts/ap13-styles-isolated-final-150` and `-150-v2`. The harness now waits for the measured native size before saving its restore target and for the actual hit-tested row before sending a click. No timeout extension or product-behavior workaround was used. Normal mode was then verified at all three scales.

The visual scene uses six passive snapshot rows at the mock's 1440×920 size and 880×680 for compact Barebones. Downloads/Settings, empty/disabled states, Advanced details, Dark/Light and keyboard focus were inspected; primary-action label contrast measured 7.55:1 and 6.46:1. Small EN/DE pages retain reachable actions. The README screenshots come from this isolated run. Visual user acceptance is still open.

These results do not reproduce or establish the cause of the reported native desktop freeze. Xvfb has no native tray host, GPU rendering, host accessibility or desktop portals, and does not prove native Windows/macOS or extracted-package behavior.

### Historical local style candidate — source `203d7ed` (built as oChan)

All five desktop archives and `libochan.0.9.0.nupkg` were rebuilt from source commit `203d7ed392edb5d99427a3f7b5d24c1dc448e1c5`. This includes the `1884ff5` style checkpoint, the retained pages, cat icon, Avalonia 12 and updated documentation/screenshots. All five publishes completed without warnings/errors. Complete extracted file hashes match their source trees; required documents, all three screenshots, native libraries and Unix executable permissions were checked. macOS bundle metadata and icons passed structural checks.

The library passed its consumer in a fresh package cache with an exact package-hash match and no desktop dependencies. Packages and `SHA256SUMS` are in `artifacts/ap13-style-candidate-203d7ed/packages`; source/runtime graphs, logs, SDK details and results are in its `evidence` directory. Portable solution/smoke dependency graphs were restored in locked mode afterward. These local artifacts have not been published. No extracted GUI apphost was launched for this checkpoint; full native workflows, Gatekeeper behavior and user visual acceptance remain open.

| Artifact | SHA256 |
| --- | --- |
| `oChan-0.9.0-linux-x64.tar.gz` | `5af813235367b84d2ca07a255cd039652a7954f442bdf196e56cc8d3c02fb2fd` |
| `oChan-0.9.0-win-x64.zip` | `bd4a56f6ffa05e09a7e69994996081677bc95377409d250a0760c425e2f8d449` |
| `oChan-0.9.0-linux-arm64.tar.gz` | `729db44b87c86bca8eda4a381f43dd84dc6242da3efde0c13d27aeb64b3727b3` |
| `oChan-0.9.0-osx-x64.tar.gz` | `4a4470f4e880edcc931c346d1242d46446ddbc4cdece16713409ddc32a6a7958` |
| `oChan-0.9.0-osx-arm64.tar.gz` | `4d4e5e53dea0007bb0f2c1f1a2ecaaf5bf6f55a9291fe31d6b8fb8be19d03ec7` |
| `libochan.0.9.0.nupkg` | `340877a15c06ef7ad6d9aa69f388b66e41db4fdf7d6f70fbf38efa21bfbed54a` |

The older package/apphost evidence below identifies different archives and does not establish native execution of these refreshed candidates. A later evidence-only documentation commit records these hashes; it does not change their source commit.

## Upstream requirements checked on 2026-10-10

The official [.NET 10 supported OS matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), updated 2026-09-28 when checked, lists:

- macOS 15, 26 and 27 on x64/Arm64. macOS 14 is listed out of support.
- Ubuntu 22.04, 24.04 and 26.04 on x64/Arm64, among other listed distributions. The portable x64/Arm64 glibc baseline is 2.27; this is a runtime compatibility floor, not a guarantee for every distribution or native graphics dependency.
- Maintained Windows 11 releases/editions and the specifically listed Windows 10 LTSC/Enterprise/IoT variants. It does not promise general support for every Windows 10 installation. The [Microsoft installation guidance](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions) explains the edition/lifecycle restriction.

The desktop now uses Avalonia 12.1.4. Consult the [current Avalonia supported-platform guidance](https://docs.avaloniaui.net/docs/overview/supported-platforms) together with .NET 10's stricter OS matrix above. Avalonia 12 offers a separate Wayland backend, but this application does not enable or package it. Historical Avalonia 11 OS allowances do not describe the current dependency graph or override .NET's lifecycle requirements.

The current Linux desktop uses X11, including XWayland on a Wayland desktop. A working graphical session and native graphics/font libraries are required even in a self-contained package. Ubuntu requires the X11/font dependencies used by Avalonia, including `libx11-6`, `libice6`, `libsm6`, `libfontconfig1` and `libdbus-1-3`. A `linux-x64` or `linux-arm64` package targets glibc; it is not a `linux-musl-*` Alpine package.

## Native macOS package and startup checkpoint — `672b45d`

[Run 39515151002](https://github.com/tfgrass/MeowChan/actions/runs/39515151002)
completed successfully on both native targets, exact source
`672b45db89b469a04904335f17b6ae3d9ec83492`:

| Check | ARM (`macos-15`) | Intel (`macos-15-intel`) |
| --- | --- | --- |
| Native packaging/signing fixtures | 33/33 passed, including strict CFBundle identity/icon runtime lookups and direct/whole-app alias cases | Same 33/33 fixture checks passed |
| Actual product package | Signatures, native dependency closure and ZIP round-trip passed | Same checks passed |
| Actual extracted .NET app | Startup and graceful shutdown passed | Startup and graceful shutdown passed |

Strict runtime CFBundle identity/icon lookups were exercised by the native
fixtures. The actual product has package metadata/icon validation and a separate
extracted .NET startup/shutdown result; runtime identity/icon introspection inside
the actual .NET process is not claimed. Final evidence:
`artifacts/r09-macos-adhoc-native/39515151002/RESULT.json` and `RESULT.txt`.

This is an evidence-only package/startup checkpoint. It does not establish VLC
playback, real audio/hardware, quarantine/Gatekeeper acceptance, corresponding-source
closure or a public Mac release. Those remain separate requirements.

## Native macOS offline source checkpoint — `5a7ca8d`

[Run 39511554103](https://github.com/tfgrass/MeowChan/actions/runs/39511554103)
passed on **macOS 15.7.9 ARM64**, SDK 10.0.111, source
`5a7ca8d2c06df650751f6e46b513f1ebc7b7b2c0`:

| Check | Observed result |
| --- | --- |
| Native source build | 0 warnings / 0 errors |
| Core / Desktop tests | 631/631 / 146/146 passed |
| Export coverage | All 43 existing single/collection cases and 3 path-safety cases passed |
| Native temporary-path diagnosis | `/var/folders/.../T` resolves to `/private/var/folders/.../T`; owned physical temporary fixtures succeed while caller-supplied linked/dangling paths remain rejected |

Evidence: `artifacts/r09-macos-export-path-fix/native-followup/RESULT.txt`,
`summary.json` and `path-safety-diagnostics.txt`. This is an offline native
source-test result, not GUI, extracted app ZIP, VLC playback or Gatekeeper
acceptance, and it does not establish native Intel execution.

## Candidate package matrix

| Runtime identifier | Delivery | Native evidence boundary |
| --- | --- | --- |
| `win-x64` | Self-contained ZIP | Targeted native source checks passed at `6d6c3fb`; final extracted ZIP remains separate. |
| `linux-x64` | Bundled/system-VLC tar and bundled AppImage | Eight isolated source-built GUI scenarios passed at `161a8b0`; final extracted formats remain separate. |
| `linux-arm64` | Bundled/system-VLC tar and bundled AppImage | Earlier package matrix passed at `6e750ec`; final extracted formats remain separate. |
| `osx-x64` | Ad-hoc-signed DMG | Targeted Intel source checks passed at `b76e2f1`; final DMG remains separate. |
| `osx-arm64` | Ad-hoc-signed DMG | Focused ARM check failed at `b1dc9f8`; correction `e4374dd` passed local Linux selection, with native confirmation and final DMG still open. |

These are candidate evidence boundaries, not completed platform certifications. Actual OS versions, source commits and archive hashes belong to the final package records.

## Planned MeowChan distribution form

Windows candidates contain a self-contained executable directory in a ZIP; Linux has bundled/system-VLC tar archives and bundled AppImages for each CPU. Extract the complete Windows ZIP or Linux tar archive and launch `MeowChan.exe` or `./MeowChan`, respectively. Keep the accompanying native libraries and notices beside the executable. The standard macOS downloads are `MeowChan-<version>-osx-arm64.dmg` and `MeowChan-<version>-osx-x64.dmg`, each containing a self-contained ad-hoc-signed `MeowChan.app` and an Applications shortcut. Drag the app to Applications before launching.

The planned macOS bundle has an ad-hoc signature and is not notarized. Its revised signed ZIP layout puts the physical .NET main executable at `Contents/MacOS/MeowChan` with the four CoreCLR entitlements. All other publish entries reside in `Contents/Resources/application`, with relative top-level symlinks from `Contents/MacOS`; `Resources/application/MeowChan` links back to the physical main. No C launcher is used. All Mach-O code is explicitly signed and validated; native-fixture CFBundle identity/icon lookups and separate actual extracted .NET startup/shutdown passed at `672b45d`, while full media/desktop acceptance remains open. The icon remains under `Contents/Resources`, with generated `Info.plist` metadata. Legacy unsigned/Homebrew bundles retain the earlier `Contents/MacOS` payload layout. The bundle identifier is `org.meowchan.desktop`, executable name `MeowChan`, and candidate deployment baseline macOS 15.0. Local structural checks do not establish native launch or Gatekeeper acceptance. The [official Avalonia macOS deployment guide](https://docs.avaloniaui.net/docs/deployment/macos) describes that structure. Its example `LSMinimumSystemVersion` is an example, not this application's supported minimum.

Standard 0.9.0 macOS distribution uses complete ad-hoc-signed app DMGs without Apple Developer ID or notarization; obtaining a certificate is not a release gate. Copied-app startup, bundled-plugin playback and desktop workflows passed at `6e750ec` on both CPUs, before the final window corrections. Targeted Intel source checks passed at `b76e2f1`; optimized ARM and final DMG checks remain open. For a trusted app blocked as an unidentified developer, use System Settings → Privacy & Security → Open Anyway (older macOS: System Preferences → Security & Privacy → General). Keep Gatekeeper and quarantine metadata intact. See [manual approval and optional future signing](macos-signing.md).

Native packaging targets `macos-15` (ARM) and `macos-15-intel` (Intel), with
inside-out Mach-O/bundle signing, main-apphost CoreCLR entitlements, and ZIP
extraction/signature/architecture/dependency revalidation. Package/signature and extracted-startup checks passed at `672b45d`; this is
limited native evidence, not full media/desktop or Gatekeeper acceptance. Homebrew
source builds remain optional. See [the packaging contract](macos-signing.md#ad-hoc-packaging-contract).

## What each check proves

| Check | Evidence provided | Remaining boundary |
| --- | --- | --- |
| Offline tests | Deterministic production behavior against local fixtures, including failures and cancellation. | No public provider availability or native UI proof. |
| Source-built desktop harness | Avalonia startup/bindings/shutdown and selected profile/tray error paths on that runner. | The harness is a separate executable, even when it references the same application assembly. It does not execute the published `MeowChan` apphost. |
| Publish and archive round-trip | Target output can be created; full files, notices and Unix executable bits survive archiving/extraction. | No proof that the extracted app starts or downloads correctly. |
| Native extracted app run | Actual apphost, native dependencies, resources and normal app entrypoint run on the recorded OS/CPU. | Other OS/CPU combinations remain untested. |
| Local NuGet package consumer | A fresh restore can consume the candidate library with no project reference or desktop dependency. | No public NuGet publication is performed. |

CI runs the graphical harness with explicit native opt-in on disposable Windows/macOS runners and uses Xvfb/Openbox with a private session bus on Linux. Standard checks and tray stress run in separate steps. A runner without a working display must report the check as failed or unavailable; it must not silently count it as passed. A tray-absence simulation tests the application fallback contract and does not establish every desktop's tray behavior.

## Recorded validation

### Earlier 0.9.0 source and native desktop checks — 2026-10-10

The integrated 0.9.0 source working tree passed 457 .NET tests: 367 core and 90 desktop tests. All 22 Python macOS-packaging fixture tests also passed. These were local source checks using the repository's pinned SDK 10.0.111 on Manjaro Linux x86_64 (kernel 6.18.49-1-MANJARO). The packaging fixtures check packaging code against controlled inputs; they do not establish that a 0.9.0 release archive was built or launched.

The source-built Avalonia harness ran in the existing Linux graphical session with native rendering. All five scenarios passed at each requested scale, with exit code 0 and no timeout. The harness measured the native windows' actual `RenderScaling`; the figures below are observed values, not just environment settings.

| Requested scale | Measured `RenderScaling` in all five scenarios | Result | Local evidence directory |
| --- | --- | --- | --- |
| 100% | `1.0` | 5/5 passed | `artifacts/ap13-v090-accepted-scale-100` |
| 150% | `1.5` | 5/5 passed | `artifacts/ap13-v090-accepted-scale-150` |
| 200% | `2.0` | 5/5 passed | `artifacts/ap13-v090-accepted-scale-200` |

Each directory contains `results.json`, scenario logs and window captures. The scenarios are `normal`, `locked-profile`, `no-tray`, `tray-failure` and `download-session`, each run in a separate process. These local evidence files are not part of the desktop distribution. Normal-mode checks exercise presentation/settings, all three views, Dark/Light, English/German, small-window details and retained input/selection. Profile and tray scenarios cover their respective startup/shutdown and fallback paths.

The download-session scenario uses the real FourChan provider, registry, shared queue and file storage with a controlled in-process `HttpMessageHandler`. It checks UI admission and alias deduplication, appearance changes during work, a verified saved media file, native window shutdown, and restoration through a fresh host/window using the same isolated profile. Restoration reuses the saved media; there is only one media request across the initial session and restart. Both window lifetimes release their profile lease.

The integrated UI and final layout/harness fixes are recorded in commits `2e03d95` and `afa873c`. These checks used the 0.9.0 version metadata being prepared alongside the fixes; final restore/build/test logs are retained locally in `artifacts/ap13-v090-accepted-checks`. They do not identify a final release archive. No public imageboard HTTP requests were made. Clipboard and file-picker checks use controlled services; native clipboard/file-picker integration and every desktop's tray host remain outside this evidence. Window captures demonstrate application rendering at the measured scale, not complete desktop screenshots or validation of all monitor arrangements. Full workflows in the extracted packages, including Windows, macOS and Linux Arm64, still require their own native evidence.

### Historical 0.9.0 packages and Linux apphost — 2026-10-10 (built as oChan)

All five desktop packages were produced from source commit `2deaf1bcd15e730b1413b5899952a104cb6f4347`, archived and extracted successfully. All five publish logs were free of warning and error lines. Required documents, language-pack instructions, screenshots and native dependencies were present; applicable Unix executable permissions survived extraction. The macOS outputs use the unsigned app-bundle structure described above. These checks used SDK 10.0.111 and .NET host 10.0.11 on the Linux development machine.

The local record is `artifacts/ap13-release-0.9.0/evidence/results.json`. The same evidence directory retains SDK information, source status, per-runtime desktop/library assets files, restore/publish/bundle logs and library-consumer output. Archives are under `artifacts/ap13-release-0.9.0/packages`. The hashes below identify those existing artifacts; subsequent documentation commits do not rebuild or change their source commit.

| Artifact | SHA256 |
| --- | --- |
| `oChan-0.9.0-linux-x64.tar.gz` | `f553c00ec321a843623f627bb572beec759b22321c7e41f47db73254c3f83fcc` |
| `oChan-0.9.0-win-x64.zip` | `3a89fa9d500e255f0c74c5e05d474dbdb2fbb6954b6011edda41971ac062d217` |
| `oChan-0.9.0-linux-arm64.tar.gz` | `398acf9b09d8a1be816c36040484d4898d42f721ec221bd3e72ea0ce16462de6` |
| `oChan-0.9.0-osx-x64.tar.gz` | `e0e16f3fe946d65bd330de5045a3e167b009666083bd5512300904592132fb71` |
| `oChan-0.9.0-osx-arm64.tar.gz` | `752e4306640481f817d81a9323e7e9f597201f0644bfa271cd081fcecf4d2c8c` |
| `libochan.0.9.0.nupkg` | `b96156d1c11e254d39c9684fee8e2c56fccd1ec0b5e13ee47a542598f3038442` |

The 0.9.0 library package passed the isolated consumer using a fresh package cache and an exact package-hash match, without desktop dependencies. The consumer verified a download, manifest reuse without another media request, and clean shutdown; its exit code was 0. This is local package-consumption evidence, not public NuGet publication.

The actual extracted `linux-x64` apphost also opened its native main window and closed normally on Manjaro x86_64 (kernel 6.18.49-1-MANJARO, glibc 2.44). Its inspected screenshot shows the new Simple/Dark interface and version `v0.9.0`. It started from a different working directory with an isolated profile. A normal close request to its native main window returned exit code 0 without forced termination, and all extracted files remained unchanged. The apphost retained mode `0755`. The result and native screenshot are recorded in `artifacts/ap13-v090-apphost-linux-x64/result.json` and `main-window.png` in that directory; the archive hash matches the Linux x64 row above.

This apphost check supplied no watch URLs and performed no downloads. It proves package startup and normal close only. The five archive checks do not establish native execution on Windows, macOS or Linux Arm64, and neither they nor this Linux startup check close the full download/removal/restore, clipboard/folder, tray and Gatekeeper acceptance matrix.

### Historical 0.8.0 checks (released as oChan)

The three validation jobs in [GitHub Actions run 38055772655](https://github.com/tfgrass/MeowChan/actions/runs/38055772655) passed on 2026-10-10. The PR head was `49d994c75f7aebb75e157b3a7c4b28a61471ee62`; CI built merge checkout `2906d21da90efd579a867f7d6d2fa91bc6713a7f` with SDK 10.0.111.

| Native runner | Offline tests | Packaging fixtures | Desktop harness |
| --- | --- | --- | --- |
| Windows Server 2022 x64 | 367 core + 39 desktop passed | 22 passed | All four modes passed |
| Ubuntu 24.04 x64 | 367 core + 39 desktop passed | 22 passed | All four modes passed under Xvfb |
| macOS 15 Arm64 | 367 core + 39 desktop passed | 22 passed | All four modes passed |

The graphical modes are `normal`, `locked-profile`, `no-tray` and `tray-failure`. They exercise startup, bindings, state updates, settings, normal shutdown and profile/tray fallback paths in a source-built harness. These successful native UI checks do not execute the final extracted application archives or verify real provider HTTP, native clipboard/file-picker integration or every tray host.

The same successful run passed the isolated NuGet consumer and produced all five desktop archives with extraction, required-file and applicable executable-permission checks; the package jobs did not launch the extracted apphosts.

Separately, an earlier local `linux-x64` package from commit `10e3d7b5f0144ff98a2fd92fe38127545f798d4a` opened its main window and closed normally with exit code 0 on Manjaro x86_64 (kernel 6.18.49-1-MANJARO, glibc 2.44). The profile lock was released and extracted files were unchanged. Its archive SHA256 was `7f09c38934a08f4011ff86ab008be33a002238637010d90f1a0eb2e40cc7cb65`. This was an actual extracted apphost check with an isolated profile and different working directory, limited to startup and normal window close; no watch URLs or download/restore workflows were exercised. It is separate evidence for that earlier package.

## Content pages branch checks (2026-10-10)

The content/rules foundation is committed as `965bb53`; the desktop integration is
`a6b2e72`, following diagnostics `e65aded`. On the local Linux x64 host, 464 core
and 95 desktop contract tests passed. Default Debug and Release builds completed
without warnings. Isolated Xvfb/Openbox/private-DBus harness runs passed at actual
100% and 200% render scaling, using synthetic HTTP and temporary profiles:

- Rules: actual global/board saves, inherited field origins, disabled overrides,
  retained invalid drafts, failed saves, and visible selection/text after scope
  and language changes. Final 100% evidence: `artifacts/rules-selection-fix-100-v1`.
- Archive: capture through the bundled provider, restart, watch removal, retained
  plain-text posts, verified PNG preview, and local reading while media discovery
  is delayed. 100% evidence: `artifacts/content-pages-final-100/download-session`.
- Both workflows plus existing desktop regressions at 200%:
  `artifacts/content-pages-final-200`. Screenshots cover compact EN/DE layouts;
  the personal desktop was not used.

Earlier runs and failures remain in their own artifact directories. Visual review
caught stale/empty rule selections that earlier model-only assertions missed;
final assertions cover the visible controls as well. The small archive reader
now reserves useful post space by reducing secondary chrome.

The real local NuGet consumer used a fresh cache, excluded desktop dependencies,
verified media reuse and read a SQLite catalog after disposing its writer/lease.
The five exact CI publish restores passed with locked inputs and their native
SQLite assets (`artifacts/content-runtime-publish-restore-ci`). A Linux x64
self-contained publish includes `libe_sqlite3.so`. These are source/dependency
checks, not final Windows/macOS/Arm64 runtime acceptance or a release of this branch.

## Final artifact acceptance record

For every platform promised as supported, record the source commit, SDK/runtime and resolved dependency graph, archive SHA256, actual OS/version/CPU, and native test result. Extract to a fresh directory and start from a different working directory using an isolated `MEOWCHAN_PROFILE`. Verify:

1. The actual packaged application starts, loads icons/resources and reports the intended version.
2. Add, bounded download, cancellation, restart/restore and assigned download directories work.
3. Removing boards/threads has the documented behavior; settings persistence and an already-owned profile are handled visibly.
4. Clipboard, two-row context menus, folder picker and folder opening operate on the intended data.
5. Normal Exit works; unavailable/failing tray support leaves an accessible application.
6. Simple/Advanced/Barebones, Dark/Light and English/German remain usable in small windows and at the tested display scales; input, selection and active downloads survive presentation changes.
7. The complete package retains its native dependency tree, `LICENSE`, `THIRD-PARTY-NOTICES.md` and required native-component notices/source information; Unix executable permissions survive extraction. README, changelog, release notes, screenshots and developer documentation remain on GitHub rather than inside desktop downloads.

The local 0.9.0 source-built checks, package round-trips, Linux apphost startup/exit and historical 0.8.0 runs establish only their stated coverage. Full native workflow acceptance of the 0.9.0 extracted package matrix remains open, including Windows 11, Linux Arm64 and both Mac architectures. Archive construction, a passing source-built harness and apphost startup alone do not complete that acceptance.

## Dependency restores and updates

`global.json` requires SDK 10.0.111 without patch roll-forward. For the five repository production/test projects, Release restores use reviewed `packages.release.lock.json` files and fail on dependency drift. Each published runtime has its own `packages.release.<RID>.lock.json` in the desktop and library projects. Switching a runtime therefore does not overwrite the portable or another runtime's lock. Default Debug development restores remain independent of this Release policy.

Use the same configuration and publish properties during restore and build:

```sh
dotnet restore MeowChan.sln -p:Configuration=Release --locked-mode
dotnet build MeowChan.sln -c Release --no-restore
dotnet test MeowChan.sln -c Release --no-build --no-restore
dotnet restore tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -p:Configuration=Release --locked-mode
```

For a self-contained package, replace `<RID>` with one of the five candidate identifiers:

```sh
dotnet restore MeowChan.csproj -p:RuntimeIdentifier=<RID> -p:Configuration=Release -p:SelfContained=true -p:PublishSingleFile=true --locked-mode
dotnet publish MeowChan.csproj -c Release --runtime <RID> --self-contained true -p:PublishSingleFile=true --no-restore --output artifacts/publish/<RID>
```

The restore deliberately sets the singular `RuntimeIdentifier` property. `dotnet restore --runtime` sets the plural `RuntimeIdentifiers`; with a self-contained executable, the SDK can then infer the current host as an additional runtime. Selecting the target explicitly keeps each reviewed runtime lock independent of the build host.

SDK and dependency upgrades deliberately regenerate locks. After changing the version, run the corresponding restore command with `-p:RestoreLockedMode=false --force-evaluate` instead of `--locked-mode`, first for the solution and smoke project, then for every supported publish RID with the same `SelfContained` and `PublishSingleFile` properties. Review the lock diffs, repeat all seven restores in locked mode, and run the relevant tests before committing the upgrade. CI also treats an unavailable vulnerability-data query (`NU1900`) as an error.

The package-only sample is outside this policy: it restores the just-built candidate into a fresh package cache and verifies its exact package hash. A fixed sample lock would otherwise reject intentional new contents of a local candidate at the same version. The SDK and package locks fix the selected inputs; they do not claim byte-for-byte identical archives. CI retains the resolved graph, SDK details and artifact hashes as evidence.

### Tray preference correction — 2026-10-21 source checkpoint

Close to tray and Minimize to tray are now separate settings. New profiles default
both off; profiles without the new CloseToTray field migrate the old
MinimizeToTray value to CloseToTray and start the new minimize preference off.
Linux hiding requires a registered StatusNotifier host and this process's
registered native icon. While hidden, bounded non-overlapping host checks restore
the window if the host or watcher becomes unavailable. Tray failure preserves
taskbar access and logs the fallback once per requested action.

The private DBus/Xvfb/Openbox `tray-host`, `no-tray` and `tray-failure` scenarios
passed at measured scale 1 in `artifacts/tray-split-freeze-20261021`. The host fixture
uses Avalonia's actual native DBus registration and activation, and checks
independent settings Save/Cancel, first minimize/close, another process's icon
being insufficient, host loss, watcher restart and watcher loss. All 75 targeted
configuration/host tests and 11 runner safety tests passed. Default Release and
five RID lock graphs restored with their CI flags; the package versions did not
change. This source evidence does not claim native shell presentation or final
extracted-package acceptance on every supported platform.
