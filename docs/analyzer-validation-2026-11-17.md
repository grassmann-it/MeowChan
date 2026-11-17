# UX-LEDGER-22 validation — 2026-11-17

Base: `e2ee324ef` (`origin/goal/gesamtprogramm-profile-a` when fetched). SDK: **10.0.111**.
All commands below target projects, not the solution. No GUI smoke suite was run.

## Before / enforcement / after

| Check | Warnings | Errors | Outcome |
| --- | ---: | ---: | --- |
| Original `dotnet build MeowChan.csproj` including dependencies | 13 | 0 | Succeeded |
| Same graph, curated rules enforced, baseline disabled | 156 | 67 | Expected failure in libmeowchan; graph stops there |
| Desktop graph, curated rules and dated baseline | 465 | 0 | Succeeded |
| Final desktop-only build (`--no-restore -p:BuildProjectReferences=false`) | 271 | 0 | Succeeded |
| New real SDK analyzer probe, baseline enabled | 0 | 4 | Expected failure: exactly CA2000, CA2213, CA1001, CA2016 |
| DesktopSmoke compile-only, no baseline, four rules warning-only for capture | 405 | 0 | Captured 98 pre-existing diagnostics |
| DesktopSmoke compile-only, normal enforcement and final baseline | 307 | 0 | Succeeded; no GUI execution |
| Release desktop-only retry, normal enforcement and final baseline | 271 | 0 | Succeeded |

The fresh original build had 13 warnings on this fetched base, rather than the audit's incremental
one-warning build. Existing C#/XML/Avalonia warnings were not hidden or fixed. The other Minimum
analyzer warnings remain warnings, not errors. Counts vary with which dependencies are recompiled.

Representative enforced errors before applying the baseline:

```text
<repo>/libmeowchan/Booru/DanbooruWatcherScheduler.cs(713,19): error CA2000: Use recommended dispose pattern to ensure that object created by 'CancellationTokenSource.CreateLinkedTokenSource(stop)' is disposed on all paths. If possible, wrap the creation within a 'using' statement or a 'using' declaration. Otherwise, use a try-finally pattern, with a dedicated local variable declared before the try region and an unconditional Dispose invocation on non-null value in the 'finally' region, say 'x?.Dispose()'. If the object is explicitly disposed within the try region or the dispose ownership is transfered to another object or method, assign 'null' to the local variable just after such an operation to prevent double dispose in 'finally'. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2000) [<repo>/libmeowchan/libmeowchan.csproj]
<repo>/libmeowchan/Http/JsonResponseReader.cs(145,33): error CA2213: 'LimitedReadStream' contains field '_inner' that is of IDisposable type 'Stream', but it is never disposed. Change the Dispose method on 'LimitedReadStream' to call Close or Dispose on this field. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2213) [<repo>/libmeowchan/libmeowchan.csproj]
<repo>/libmeowchan/Persistence/WorkStateAccess.cs(280,26): error CA1001: Type 'Entry' owns disposable field(s) 'Gate' but is not disposable (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1001) [<repo>/libmeowchan/libmeowchan.csproj]
<repo>/libmeowchan/Gallery/MediaManifestCache.cs(73,16): error CA2016: Forward the 'cancellationToken' parameter to the 'GetAsync' method or pass in 'CancellationToken.None' explicitly to indicate intentionally not propagating the token (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2016) [<repo>/libmeowchan/libmeowchan.csproj]
```

## Affected project build checks

Every test/fixture/helper project listed in `MeowChan.sln` was built individually, sequentially,
with the baseline enabled and normal warnings-as-errors policy (no overrides). Their production
project-reference graphs also build. No full solution build was run.

| Project | Warnings | Errors | Exit |
| --- | ---: | ---: | ---: |
| `tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj` | 1798 | 0 | 0 |
| `tests/MeowChan.ExamplePlugins.Tests/MeowChan.ExamplePlugins.Tests.csproj` | 62 | 0 | 0 |
| `tests/MeowChan.ImageHeader.Tests/MeowChan.ImageHeader.Tests.csproj` | 0 | 0 | 0 |
| `tests/MeowChan.MetadataFilters.Tests/MeowChan.MetadataFilters.Tests.csproj` | 2 | 0 | 0 |
| `tests/MeowChan.OfficialPlugins.Tests/MeowChan.OfficialPlugins.Tests.csproj` | 163 | 0 | 0 |
| `tests/MeowChan.OriginalMediaHttp.Tests/MeowChan.OriginalMediaHttp.Tests.csproj` | 3 | 0 | 0 |
| `tests/MeowChan.PluginCompiler.Tests/MeowChan.PluginCompiler.Tests.csproj` | 27 | 0 | 0 |
| `tests/MeowChan.PluginFilters.Fixture/MeowChan.PluginFilters.Fixture.csproj` | 0 | 0 | 0 |
| `tests/MeowChan.PluginFilters.Helper/MeowChan.PluginFilters.Helper.csproj` | 0 | 0 | 0 |
| `tests/MeowChan.PluginFilters.Tests/MeowChan.PluginFilters.Tests.csproj` | 7 | 0 | 0 |
| `tests/MeowChan.PluginHost.Configuration.Tests/MeowChan.PluginHost.Configuration.Tests.csproj` | 16 | 0 | 0 |
| `tests/MeowChan.PluginRuntime.Fixture/MeowChan.PluginRuntime.Fixture.csproj` | 2 | 0 | 0 |
| `tests/MeowChan.PluginRuntime.Tests/MeowChan.PluginRuntime.Tests.csproj` | 13 | 0 | 0 |
| `tests/MeowChan.PluginSdk.Tests/MeowChan.PluginSdk.Tests.csproj` | 4 | 0 | 0 |
| `tests/MeowChan.SourceAdapter.Fixture/MeowChan.SourceAdapter.Fixture.csproj` | 0 | 0 | 0 |
| `tests/MeowChan.SourceAdapter.Tests/MeowChan.SourceAdapter.Tests.csproj` | 18 | 0 | 0 |
| `tests/MeowChan.Tests/MeowChan.Tests.csproj` | 446 | 0 | 0 |

## Tests and compatibility

- `dotnet run --project scripts/AnalyzerBaseline.Tests/AnalyzerBaseline.Tests.csproj`: **9/9 passed**.
- `python3 scripts/AnalyzerBaseline.Tests/test_prune.py`: **2/2 passed** (shrink-only, no partial writes,
  preserve uncaptured assemblies, refuse an empty capture).
- `dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-build --no-restore
  --filter FullyQualifiedName~GalleryTerminalRetirementTests`: **32 passed, 0 failed, 0 skipped**.
  These are framework-free retirement/source-order tests, not GUI smoke tests.
- `dotnet restore MeowChan.csproj -p:Configuration=Release`: **succeeded** with existing locked-restore
  conventions; no package/Release lock files changed.
- The first targeted Release build exceeded **600 seconds** on the contended machine and the killed
  build reported MSB4166 (child node exited prematurely). A smoke capture with the shared compiler
  was also stopped after making no progress. Both were retried with an isolated single-node compiler:

  ```sh
  dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj --no-restore \
    -m:1 -p:BuildProjectReferences=false -p:UseSharedCompilation=false
  dotnet build MeowChan.csproj -c Release --no-restore \
    -m:1 -p:BuildProjectReferences=false -p:UseSharedCompilation=false
  ```

  Both **succeeded**, with 307/271 warnings and zero errors respectively, using previously built
  dependencies. Host load reached 64.56 on 16 processors, so no full-solution build was attempted.
- Final snapshot: **1,089 diagnostics** (CA2000 897; CA2213 75; CA1001 66; CA2016 51).
  Running the pruning tool on the complete capture reports `1089 -> 1089 (0 removed; no additions)`.
  The 98 DesktopSmoke entries were included in the initial dated snapshot before handoff; this does
  not introduce any ongoing baseline-expansion command.

Raw command logs are session artifacts under `$COMMANDCODE_SCRATCHPAD/ledger22-*.log`;
this report preserves the outcomes and key output beyond the session.
