# UX-LEDGER-21: smoke / release boundary

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
acceptance result is not proof of executing the shipping Release archive. Packaging workflows
which previously invoked these private switches against Release archives need a distinct Debug
acceptance artifact; keep the actual Release archive's package/signature checks separate.

Focused validation (no solution build or full smoke suite):

```sh
dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Debug
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Debug --no-build --filter 'FullyQualifiedName~Apphost|FullyQualifiedName~ReleaseAssemblyBoundaryTests'
dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Release
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Release --no-build --filter FullyQualifiedName~ReleaseAssemblyBoundaryTests
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -c Debug
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -c Release
python3 scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll --registry --only no-tray --output artifacts/ledger21
```
