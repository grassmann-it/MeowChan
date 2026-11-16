# P0 reader journey decomposition

Base: `ba1842af424bc1275ec0d440fa89be009f7a072f`. Line ranges below refer to
`tests/MeowChan.DesktopSmoke/ReaderUxChecks.cs` **at that base**, not the extracted file.
These are GUI journey definitions, not claims of native acceptance from contract tests.

## Exact baseline coverage

| Required independent registry ID | Original coverage | Current execution |
| --- | --- | --- |
| `reader-navigation` | 174–211 (plus existing 212–216 scoped return) | Original Downloads-first setup/export sizing, wide geometry, real Archive query, favorites/footer/filter/list-width/poster checks, real reader open/icons/favorites/interaction/ideas/navigation-overlay matrix; returns before all following groups. |
| `reader-owned-navigation` | 217–248 | `RunJourneyAsync` real query/open → `CheckOwnedNavigationAsync`: loaded large preview and overlay ownership, export inside reader, post-page Down/Up, thread Left/Right and frozen query's 1049→1050→1051 end/no-wrap, reset to Hero/post1000 and await media. |
| `reader-gallery-roundtrip` | 249–319 | `RunJourneyAsync` real query/open → `CheckGalleryRoundtripAsync`: post1000 attachment button, real gallery bitmap, captions/window/small-overlay chrome, next to1003, zoom/drag/Fit matrix, association key ownership, Read Post→gallery return retaining viewer+zoom, close→reader focus/visible post after preview loading, unassigned asset retains anchor+selection+focus, then 24 Tab containment checks and actual reader close/ownership+embedded margins **after both gallery returns**. |
| `reader-list-gallery-return` | 320–425 | `RunJourneyAsync` real query/open/actual reader-close button → `CheckListGalleryReturnAsync`: actual Next Threads to second page (2 results), actual result toolbar/overflow hit target and focus, Enter down/up invokes exactly one click, overflow closes, gallery→Read Post→return→close, exact query/page retention and real toolbar-or-More return focus. |
| `reader-returns` | 426 | Existing `ReaderReturnChecks.RunAsync`, independently selected. |
| `reader-stability` | 427 | Existing `ReaderStabilityChecks.RunAsync`, independently selected. |
| `reader-groups` | 428 | Existing `ReaderGroupChecks.RunAsync`, independently selected. |
| `video-lifecycle` | Outer Program reader preamble, optional `VideoLifecycleChecks` hook | Explicit required `VideoLifecycleChecks.RunAsync()`; no optional-reflection skip, no other reader/gallery checks. Controlled blocking-native-stop lifecycle fixture, not codec acceptance. |
| `window-minimum` | Outer Program reader preamble | Explicit required `WindowMinimumChecks.RunAsync(window)` only. |
| `gallery-close` | Outer Program reader tail | Existing `GalleryCloseChecks.RunAsync(window, videoFixture, …)` including its close-owned completion; retains conditional video-fixture handling. |

Each ID above is an alternative under `--reader-ux`, with exactly one
`MEOWCHAN_<UPPER_UNDERSCORE_ID>_ONLY=1`. No new baseline ID requires a video fixture.
Registry invocation produces a separate invocation, not an in-process loop. The workflow
owner schedules each with its own process/profile; **fresh3 means repeat each selected
journey three times in fresh processes/profiles**, not three sequential calls on one window.
The three new journeys share only actual fixture preparation (52 archived threads,
strictly bound saved media, real search/sort/apply/select/open controls). They do not call
one another, replay `reader-navigation`, assign gallery/post/focus/zoom outcomes, restore
asserted state, bypass the real toolbar, or catch-and-continue failures.

`Program` dispatches every selected reader alternative before the outer lifecycle/minimum
checks, then marks success, closes and returns. `reader-media-input` keeps its existing
early dedicated path. `gallery-close` is deliberately excluded from this early return.
Plain `--reader-ux` with no alternative retains the old debug sequence, including the
three extracted blocks in original order, return/stability/group tail, optional real
`VideoGalleryChecks` when a video fixture exists, and gallery close. Existing observation
selectors remain in that debug path. Combining transition/help observational review with
any scoped reader alternative is rejected by selector validation, so review cannot
preempt a selected journey and produce a misleading scoped PASS.

## Shared helper registrations (helper-owned implementations)

All entries below are independent required **Root** scenarios at `SmokeStage.Ready`;
selectors are `MEOWCHAN_<UPPER_UNDERSCORE_ID>_ONLY=1`, without command-line arguments.
There is no blanket catch, NightlySkip or ownership prerequisite hidden behind playback.
The direct GIF ownership root explicitly preserves the old playback entry's disposable-desktop
authorization as a failclosed `SmokeRun.Precondition` (Linux private Xvfb; otherwise native
authorization), checked before profile/window creation.

| ID | Exact API |
| --- | --- |
| `gif-playback-core` | `GifPlaybackChecks.RunPlaybackAsync(window)` |
| `gif-successors` | `GifPlaybackChecks.RunSuccessorsAsync(window)` |
| `gif-verified-failures` | `GifPlaybackChecks.RunVerifiedFailuresAsync(window)` |
| `gif-reader-association` | `GifPlaybackChecks.RunReaderAssociationAsync(window)` |
| `gif-ownership` | `GifOwnershipChecks.RunAsync(window)` |
| `gallery-errors-retirement-clip` | `GalleryErrorInteractionChecks.RunRetirementClipAsync(window)` |
| `gallery-errors-layout-roots` | `GalleryErrorInteractionChecks.RunLayoutRootsAsync(window)` |
| `gallery-errors-notifications` | `GalleryErrorInteractionChecks.RunNotificationsAsync(window)` |
| `gallery-errors-failure-reader-return` | `GalleryErrorInteractionChecks.RunFailureReaderReturnAsync(window)` |

GIF roots inherit the existing GIF debug root's pinned WebM and MP4 environments.
Gallery-error roots inherit existing gallery-error's video and functional-audio environments.
Original `gif-playback` (including ownership tail) and `gallery-error` remain unsplit debug
roots. Helper coverage details: `tests/test_gif_gui_isolation.py` and
`tests/gallery-error-decomposition/README.md`. Helpers were independently committed as
`e45ac6697` and `91bcd0b2e`; integrate those before compiling shared registrations.

## Focused contracts / validation boundary

`ReaderJourneyIsolationTests` checks exact invocation/selector exclusion, independent
root bindings and fixture gates, early-return ordering, required outer-scope bindings,
real query/open setup, and SHA-256 equality of **every original statement** in all three
217–425 journey blocks. Normalization removes only trailing line whitespace. This is an
extraction guard: intentional future journey edits must update the documented baseline
and hashes after review, not silently delete checks.

`SmokeScenariosTests.EveryRegisteredScenarioHasAReachableEntryPoint` also recognizes the
registry-driven reader selector read, requiring both the Program dispatch hook and the
explicit ID in the Runs switch for alternatives without a direct environment read.

Validation performed without rebuilding the product: isolated .NET10 xUnit project
linking `SmokeScenarios.cs`, `SmokeSelectors.cs`, `SmokeScenariosTests.cs` and
`ReaderJourneyIsolationTests.cs`; smoke C# source-only compilation against already-built
parent dependency DLLs (including both helper commits). Neither is native GUI acceptance.
No full build, native CI or push was performed. Native fresh3 evidence belongs to the
workflow owner and must remain a required gate before claiming cross-platform acceptance.

Run the dependency-free dispatch contract (builds only a tiny temporary recording-port harness):

```sh
python3 tests/reader-journey-decomposition/check_dispatch.py
```

It extracts the actual reader switch and nine actual root delegate expressions from
`SmokeScenarios.Runs.cs`, compiles and executes all 15 routes against recording ports,
and requires exactly one expected call per route. It also executes the actual ownership
authorization precondition in denied and authorized states. It does not exercise real controls,
fixture correctness, process startup, or native teardown. The xUnit registry/source suite
passed 52/52; this dispatch harness passed 15/15; helper contracts passed 3/3 GIF and 2/2
gallery-error. Smoke-source-only compilation completed with zero errors (33 existing
warnings) against the parent dependency binaries; no product projects were built.
