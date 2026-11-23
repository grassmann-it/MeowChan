# Smoke testing and release packages

The shipping `MeowChan` assembly must contain neither acceptance harness types nor the private
MainWindow acceptance methods. `ReleaseAssemblyBoundaryTests` reads its actual PE metadata
(including generated/nested types) to enforce this in Release, with a positive Debug control.

## Choice

Use the Debug-only option, not a second project for the apphost harness. Its eight MainWindow
partials directly depend on private window/session/restore/gallery/shutdown state. C# partials
cannot extend a type across assembly boundaries. Moving those partials to a referencing project
would require replacing those accesses with reflection or exporting a large test-only product API.
Neither is an appropriate release-blocking infrastructure fix. Assertions and scenes are unchanged.

`MeowChan.csproj` excludes all apphost partials, the startup harness and the development entry
point in every non-Debug configuration; `#if DEBUG` is a second fence around their sources.
Seven standalone fixtures/diagnostics/assertion helpers now live under `tests/Shared/Apphost`. Debug apphost builds
link those fixtures; non-Debug DesktopSmoke builds link them into the runner, not the product.
Shared inventory/startup oracles are linked into the Release test executable as well, so product
integration tests keep all their existing assertions. Harness-specific unit tests run in Debug because their subject deliberately does not exist in
Release. Product tests and the new assembly-boundary test still build/run in Release.

## Startup

`Program.cs` has one optional partial-method injection: `RunDevelopmentHost(args, ref exitCode)`.
Without its Debug implementation, the compiler erases the method and call. Production does not
parse smoke arguments, select a harness error policy, suppress process exit, create a harness timer,
change lifetime arguments or postprocess its exit code. Normal error recovery is always installed
with `failHard: false`.

`Program.Development.cs` owns smoke parsing, profile restrictions, the deadline, fail-hard errors,
fixture factory / startup-window observation, and result completion. App startup knows only generic
factory and window-ready delegates, not smoke classes. Standalone process ownership defaults in
`ShutdownExit` from the entry assembly; embedding executables do not acquire permission to exit.

## Running

The separate `tests/MeowChan.DesktopSmoke` executable and `scripts/run-desktop-smoke.py --registry`
continue to work in **both Debug and Release** and reference the corresponding product build.
No registry entry is deleted or relaxed.

The in-app `--smoke-*` apphost acceptance switches are **Debug-only**. For those probes, publish
with `dotnet publish MeowChan.csproj -c Debug ...` to a separate, non-shipping acceptance directory.
Never enable DEBUG or re-include harness sources in a shipping Release build. A Debug apphost
acceptance result is not proof of executing the shipping Release archive. The packaging workflow
now builds separate same-source, same-RID Debug acceptance archives and keeps the shipping
Release archive's package/signature/layout checks separate. Neither half substitutes for the other.

### Final platform routing

| Job | Before | After |
| --- | --- | --- |
| `macos-package` (`osx-arm64`, `osx-x64`) | Startup, playback and workflow switches on the Release DMG-installed app | Release publish boundary, package, signature/closure, ZIP round-trip and mounted/copied DMG checks; all original startup/playback/workflow probes on a separately signed, packaged and mounted **Debug** app under `artifacts/acceptance-macos-*` |
| `package-media` (`win-x64`) | Playback/workflow switches on the extracted Release ZIP | Original Release distribution/native-video/plugin checks and ZIP round-trip retained; identical playback/workflow scenes use a separate **Debug** ZIP under `artifacts/acceptance-media-*` |
| `linux-distribution` (`linux-x64`, `linux-arm64`) | Playback/workflow/error switches on Release bundled-VLC, system-VLC and AppImage distributions | All four Release distribution layout scenarios run with `--layout-only`; all original positive scenes, missing-system-VLC and damaged-bundle negatives, AppRun and extract-and-run execute separate **Debug** distributions under `artifacts/acceptance-linux-*` |
| `macos-source-candidate` | One archive binding for both package inventory and UI execution | Independent Release DMG/ZIP/signature binding and Debug startup/playback/workflow archive binding; source overlay and promotable DMGs bind only to Release |
| `current-source-inventory` / `release-candidate-gate` | Native workflow hashes implicitly referred to shipping archives | Require split evidence at the same source/version/RID, verify executed Debug archive hashes, retain Release layout evidence and stage **only Release** desktop assets |

`scripts/apphost-acceptance-boundary.py` is a fail-closed configuration/capability preflight,
not a replacement for the PE metadata tests or any acceptance scene. Every in-app runner rejects
a Release input before launching a scene; `APPHOST --dry-run` checks routing without a UI or
acceptance claim. It inspects the actual managed payload in thin hosts, macOS bundles, uncompressed
single-file hosts and AppImages (extraction only). Release validation rejects the development hook
and any embedded `--smoke-` user string. Debug validation requires the Debug configuration,
development hook and the runner's switch. CI checks both publishes before packaging.

`MeowChanAcceptancePublish=true` only selects the reviewed Release RID lock graph for a **Debug**
acceptance restore/publish. It does not change `Configuration`, `DefineConstants` or the Release
source exclusion. Separate `--artifacts-path` directories avoid cross-configuration restore reuse;
macOS explicitly enables the same single-file analyzer dependency while keeping runtime libraries
visible for signing. Current-source assembly requires split evidence; retained v0.9 assembly inputs
keep their historical contract, not a fallback for new source candidates.

Reports identify `acceptanceConfiguration: Debug` and `shippingArtifactExecuted: false`;
Linux also distinguishes `configuration` and `layoutOnly`. Candidate inventories explicitly record
`acceptance_configuration: Debug` and `shipping_binary_execution_claim: false`. Debug archives
are retained solely as non-shipping evidence, never promoted as public release assets.

Focused validation (no solution build or full smoke suite):

```sh
dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Debug
xvfb-run -a dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Debug --no-build --filter 'FullyQualifiedName~Apphost|FullyQualifiedName~ReleaseAssemblyBoundaryTests'
dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Release
xvfb-run -a dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Release --no-build --filter FullyQualifiedName~ReleaseAssemblyBoundaryTests
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -c Debug
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -c Release
python3 scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll --registry --only no-tray --output artifacts/smoke-boundary
```
