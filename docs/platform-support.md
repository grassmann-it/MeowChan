# Platform requirements and release evidence

This record separates upstream platform support, candidate oChan packages, and checks actually performed. A successful cross-publish does not establish that an application starts on the target operating system or CPU.

## Current 0.9.0 preparation

0.9.0 replaces the desktop UI with Simple, Advanced and Barebones views, appearance/language preferences, batch URL admission and details panels. The platform targets and .NET 10 baseline remain unchanged. Local source-built checks, five successful package round-trips and Linux x64 apphost startup/exit are recorded below, separately from historical 0.8.0 results. Full native workflow acceptance of the 0.9.0 extracted packages is still open.

Current acceptance must cover the integrated download/restore flow, all three views, Dark/Light, English/German, keyboard focus, small windows and native rendering at the intended display scales. The [release-notes draft](releases/v0.9.0.md) records user-visible behavior and remaining boundaries.

The subsequent compact-layout/menu-contrast correction passed a Release build without warnings or errors and 90 desktop tests. Final source-built Linux checks passed 5/5 scenarios at measured 100% and 150%, and 4/5 at 200%: `artifacts/ap13-layout-final-scale-{100,150,200}` (14/15 runs, 72 captures). All presentation checks passed, including six passive comparison rows, source/board and numeric sorting, minimum window sizes, and separately rendered Dark/Light context menus with asserted icon colors. The earlier `ap13-layout-correction-v3-scale-*` runs passed 15/15, but do not supersede the later failure.

**Open Linux tray lifecycle defect:** The final 200% download-session check aborted during rapid restored-window shutdown, after download and media restoration had succeeded. The stack ends in `OrgKdeStatusNotifierItemHandler.EmitNewTitle` / `DBusTrayIconImpl.CreateTrayIcon`. Avalonia 11.3.22 can continue asynchronous DBus tray creation after disposal removes its handler; [upstream fix #21980](https://github.com/AvaloniaUI/Avalonia/pull/21980), included in 12.1.4, addresses this race. A reviewed framework upgrade/backport or an explicitly chosen Linux tray fallback is required before closing this gate; delays or rerunning until green do not fix it. The framework has not been changed in this layout correction. Native Windows/macOS tray appearance remains unverified. The packages listed below predate both this correction and the selected cat icon; they must be rebuilt before final release acceptance.

## Upstream requirements checked on 2026-10-10

The official [.NET 10 supported OS matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), updated 2026-09-28 when checked, lists:

- macOS 15, 26 and 27 on x64/Arm64. macOS 14 is listed out of support.
- Ubuntu 22.04, 24.04 and 26.04 on x64/Arm64, among other listed distributions. The portable x64/Arm64 glibc baseline is 2.27; this is a runtime compatibility floor, not a guarantee for every distribution or native graphics dependency.
- Maintained Windows 11 releases/editions and the specifically listed Windows 10 LTSC/Enterprise/IoT variants. It does not promise general support for every Windows 10 installation. The [Microsoft installation guidance](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions) explains the edition/lifecycle restriction.

The current Avalonia documentation also covers Avalonia 12, including a 12.1 Wayland package, and must not be used as though all of it describes the project's Avalonia 11 packages. The [official documentation snapshot from the Avalonia 11 era](https://github.com/AvaloniaUI/avalonia-docs/blob/4640a690ff98762519cd16ab369a0e8016aa3c59/docs/overview/supported-platforms.md) lists Windows 8.1/10/11, macOS 10.14–15, Debian 9+, Ubuntu 16.04+ and Fedora 30+. Those older operating-system allowances do not override .NET 10's stricter supported-platform matrix. The snapshot is historical upstream evidence, not a versioned warranty for every Avalonia 11 patch.

The current Linux desktop uses X11, including XWayland on a Wayland desktop. A working graphical session and native graphics/font libraries are required even in a self-contained package. Ubuntu requires the X11/font dependencies used by Avalonia, including `libx11-6`, `libice6`, `libsm6` and `libfontconfig1`. A `linux-x64` or `linux-arm64` package targets glibc; it is not a `linux-musl-*` Alpine package.

## Candidate package matrix

| Runtime identifier | Archive | Intended native validation baseline | Evidence still required |
| --- | --- | --- | --- |
| `win-x64` | `.zip` | Maintained Windows 11 x64 | Final 0.9.0 source-built and extracted-package workflows; the historical Server 2022 harness covers an earlier UI, a different OS and executable. |
| `linux-x64` | `.tar.gz` | Ubuntu 24.04 x64 | Full 0.9.0 workflows in the final extracted package; current Manjaro source-built checks and actual package startup/exit are recorded below. |
| `linux-arm64` | `.tar.gz` | Ubuntu 24.04 Arm64 | Native Arm64 package and desktop run; x64 cross-publish is insufficient. |
| `osx-x64` | `.tar.gz` | macOS 15 x64 | Native Intel Mac package and desktop run. |
| `osx-arm64` | `.tar.gz` | macOS 15 Arm64 | Final 0.9.0 source-built and extracted `.app` workflows plus Gatekeeper behavior; the passing Arm64 harness below used the earlier UI. |

These are candidate validation baselines, not completed platform certifications. Exact supported OS releases will be recorded with the tested artifacts. Microsoft/Avalonia support policies can change with OS end-of-life; consult the linked current policy before expanding the product matrix.

## Current distribution form

Windows candidates contain a self-contained executable directory in a zip archive; Linux candidates contain the equivalent directory in tar.gz. Extract every file and launch `oChan.exe` on Windows or `./oChan` on Linux. Keep the accompanying native libraries and notices beside the executable. macOS tar.gz archives contain `oChan.app`; open that bundle in Finder or with `open oChan.app`.

The macOS bundle is unsigned and is not notarized. Its structure contains the complete publish output under `Contents/MacOS`, the existing application icon under `Contents/Resources`, and `Info.plist` metadata generated from the product version. The bundle identifier is `org.ochan.desktop`, executable name `oChan`, and candidate deployment baseline macOS 15.0. Local structural checks do not establish native launch or Gatekeeper acceptance. The [official Avalonia macOS deployment guide](https://docs.avaloniaui.net/docs/deployment/macos) describes that structure. Its example `LSMinimumSystemVersion` is an example, not this application's supported minimum.

Packaging a `.app` can be prepared without credentials. Signing/notarization and public distribution require separate decisions and native Mac validation; no signing identity or publishing account is required for the local checks in this repository.

## What each check proves

| Check | Evidence provided | Remaining boundary |
| --- | --- | --- |
| Offline tests | Deterministic production behavior against local fixtures, including failures and cancellation. | No public provider availability or native UI proof. |
| Native source-built desktop harness | Avalonia startup/bindings/shutdown and selected profile/tray error paths on that runner. | The harness is a separate executable, even when it references the same application assembly. It does not execute the published `oChan` apphost. |
| Publish and archive round-trip | Target output can be created; full files, notices and Unix executable bits survive archiving/extraction. | No proof that the extracted app starts or downloads correctly. |
| Native extracted app run | Actual apphost, native dependencies, resources and normal app entrypoint run on the recorded OS/CPU. | Other OS/CPU combinations remain untested. |
| Local NuGet package consumer | A fresh restore can consume the candidate library with no project reference or desktop dependency. | No public NuGet publication is performed. |

CI runs the graphical harness on Windows/macOS runners and uses Xvfb with a private session bus on Linux. A runner without a working display must report the check as failed or unavailable; it must not silently count it as passed. A tray-absence simulation tests the application fallback contract and does not establish every desktop's tray behavior.

## Recorded validation

### Local 0.9.0 source and desktop checks — 2026-10-10

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

### Local 0.9.0 packages and Linux apphost — 2026-10-10

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

### Historical 0.8.0 checks

The three validation jobs in [GitHub Actions run 38055772655](https://github.com/tfgrass/oChan/actions/runs/38055772655) passed on 2026-10-10. The PR head was `49d994c75f7aebb75e157b3a7c4b28a61471ee62`; CI built merge checkout `2906d21da90efd579a867f7d6d2fa91bc6713a7f` with SDK 10.0.111.

| Native runner | Offline tests | Packaging fixtures | Desktop harness |
| --- | --- | --- | --- |
| Windows Server 2022 x64 | 367 core + 39 desktop passed | 22 passed | All four modes passed |
| Ubuntu 24.04 x64 | 367 core + 39 desktop passed | 22 passed | All four modes passed under Xvfb |
| macOS 15 Arm64 | 367 core + 39 desktop passed | 22 passed | All four modes passed |

The graphical modes are `normal`, `locked-profile`, `no-tray` and `tray-failure`. They exercise startup, bindings, state updates, settings, normal shutdown and profile/tray fallback paths in a source-built harness. These successful native UI checks do not execute the final extracted application archives or verify real provider HTTP, native clipboard/file-picker integration or every tray host.

The same successful run passed the isolated NuGet consumer and produced all five desktop archives with extraction, required-file and applicable executable-permission checks; the package jobs did not launch the extracted apphosts.

Separately, an earlier local `linux-x64` package from commit `10e3d7b5f0144ff98a2fd92fe38127545f798d4a` opened its main window and closed normally with exit code 0 on Manjaro x86_64 (kernel 6.18.49-1-MANJARO, glibc 2.44). The profile lock was released and extracted files were unchanged. Its archive SHA256 was `7f09c38934a08f4011ff86ab008be33a002238637010d90f1a0eb2e40cc7cb65`. This was an actual extracted apphost check with an isolated profile and different working directory, limited to startup and normal window close; no watch URLs or download/restore workflows were exercised. It is separate evidence for that earlier package.

## Final artifact acceptance record

For every platform promised as supported, record the source commit, SDK/runtime and resolved dependency graph, archive SHA256, actual OS/version/CPU, and native test result. Extract to a fresh directory and start from a different working directory using an isolated `OCHAN_PROFILE`. Verify:

1. The actual packaged application starts, loads icons/resources and reports the intended version.
2. Add, bounded download, cancellation, restart/restore and assigned download directories work.
3. Removing boards/threads has the documented behavior; settings persistence and an already-owned profile are handled visibly.
4. Clipboard, two-row context menus, folder picker and folder opening operate on the intended data.
5. Normal Exit works; unavailable/failing tray support leaves an accessible application.
6. Simple/Advanced/Barebones, Dark/Light and English/German remain usable in small windows and at the tested display scales; input, selection and active downloads survive presentation changes.
7. `LICENSE`, `README.md`, `CHANGELOG.md`, `THIRD-PARTY-NOTICES.md`, the release notes and language-pack instructions accompany the complete package; Unix executable permissions survive extraction.

The local 0.9.0 source-built checks, package round-trips, Linux apphost startup/exit and historical 0.8.0 runs establish only their stated coverage. Full native workflow acceptance of the 0.9.0 extracted package matrix remains open, including Windows 11, Linux Arm64 and both Mac architectures. Archive construction, a passing source-built harness and apphost startup alone do not complete that acceptance.

## Dependency restores and updates

`global.json` requires SDK 10.0.111 without patch roll-forward. For the five repository production/test projects, Release restores use reviewed `packages.release.lock.json` files and fail on dependency drift. Each published runtime has its own `packages.release.<RID>.lock.json` in the desktop and library projects. Switching a runtime therefore does not overwrite the portable or another runtime's lock. Default Debug development restores remain independent of this Release policy.

Use the same configuration and publish properties during restore and build:

```sh
dotnet restore oChan.sln -p:Configuration=Release --locked-mode
dotnet build oChan.sln -c Release --no-restore
dotnet test oChan.sln -c Release --no-build --no-restore
dotnet restore tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -p:Configuration=Release --locked-mode
```

For a self-contained package, replace `<RID>` with one of the five candidate identifiers:

```sh
dotnet restore oChan.csproj -p:RuntimeIdentifier=<RID> -p:Configuration=Release -p:SelfContained=true -p:PublishSingleFile=true --locked-mode
dotnet publish oChan.csproj -c Release --runtime <RID> --self-contained true -p:PublishSingleFile=true --no-restore --output artifacts/publish/<RID>
```

The restore deliberately sets the singular `RuntimeIdentifier` property. `dotnet restore --runtime` sets the plural `RuntimeIdentifiers`; with a self-contained executable, the SDK can then infer the current host as an additional runtime. Selecting the target explicitly keeps each reviewed runtime lock independent of the build host.

SDK and dependency upgrades deliberately regenerate locks. After changing the version, run the corresponding restore command with `-p:RestoreLockedMode=false --force-evaluate` instead of `--locked-mode`, first for the solution and smoke project, then for every supported publish RID with the same `SelfContained` and `PublishSingleFile` properties. Review the lock diffs, repeat all seven restores in locked mode, and run the relevant tests before committing the upgrade. CI also treats an unavailable vulnerability-data query (`NU1900`) as an error.

The package-only sample is outside this policy: it restores the just-built candidate into a fresh package cache and verifies its exact package hash. A fixed sample lock would otherwise reject intentional new contents of a local candidate at the same version. The SDK and package locks fix the selected inputs; they do not claim byte-for-byte identical archives. CI retains the resolved graph, SDK details and artifact hashes as evidence.
