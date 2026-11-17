# PKG-A Media selection-counter acceptance evidence

Base: `17db174fb`, including PRs #58 and #59. Product code is unchanged by this evidence package.

**Outcome:** nine deterministic cases and all **54 rendered counter cells** passed; every count stayed on one line and the complete grid rectangle shifted **0 dip**. See [the results table and 12 captures](RESULTS.md) and [raw bounds](counter-bounds.csv). The single native invocation's **overall harness verdict remained failed** because its initial GLX attempt logged one blacklisted-llvmpipe error before falling back to software rendering. All Media checks completed (`checks=True`), the window closed normally, and [the complete native log](native-linux-pass.log) preserves the error and strict failed verdict. No GUI retry or error suppression was performed.

## Provenance and scope

`332026fab` exists in the repository but is **not an ancestor of this base** (`git merge-base --is-ancestor 332026fab 17db174fb` returns 1). Its missing decimal-boundary acceptance coverage was ported onto the current selection strip rather than cherry-picking unrelated menu/shortcut changes. In particular, PR #59's left action group / right Clear + counter group and media-panel row spanning are retained. The existing base already has horizontal StackPanels, a reserved counter and the full Clear accessible name.

The supported minimum shell width is **760 dip**, pinned by `Desktop/Shell/MainWindow.axaml:5` (`MinWidth="760"`). A 640-dip stress window is not the minimum supported shell width. Selection **counts** 1099/1100 and window **widths** 1099/1100 are independently exercised.

## Deterministic contracts

`tests/MeowChan.DesktopTests/MediaSelectionCounterEvidenceTests.cs` runs nine separate theories, one for every requested selection count, against 1100 synthetic rows. Each theory checks:

- the actual ListBox selection count and exact displayed localized counter (no clamped fixture masquerading as a pass);
- unchanged four-digit reservation (`8888`), NoWrap counter text and horizontal action/state groups;
- all five pressure levels, in English and German: visible full/short Clear label as appropriate, but **always** the full localized accessible name and tooltip.

Result: **9 passed, 0 failed, 0 skipped**. Structured test evidence: [counter-contracts.trx](counter-contracts.trx).

The combined affected reader-hosting + counter run also passed **74/74**, zero skips. Only the DesktopTests and DesktopSmoke project builds were requested (and their normal dependencies), never the solution.

## Rendered-window acceptance

Geometry and screenshots require the installed templates, native window sizing, font shaping and real layout. A single native Linux X11 GUI invocation was run under Xvfb (2200×1200×24) with Openbox. This is the native Avalonia Linux backend, not Avalonia.Headless; the virtual display avoids changing the user's desktop. The registered `media-page` selector plus `MEOWCHAN_MEDIA_HEADER_CAPTURES` runs **only header/grid stability**, not the full Media journey or smoke suite.

The enhanced `MediaPageChecks.HeaderStability.cs` creates a real 1100-file private synthetic library, asserts the exact native client width, and checks every count at **760, 1099 and 1100 dip**, in **English and German** (54 cells). It asserts both selection groups share a single line, all visible controls fit, the full grid-surface rectangle stays fixed relative to the zero-selection baseline (0.5-dip tolerance on each coordinate/dimension), and Clear's accessible name stays full. Captures render the native window, then crop to the complete Media page; no browser or mock UI is involved.

The CSV records actual selection counts, line counts, grid geometry/max delta, shortening state and accessible names. Screenshots cover counts **1099 and 1100 at each of the three widths**, in both languages.

## Reproduction

```sh
# Baseline analyzer issue described below; no assertion/configuration is changed.
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  -p:RunAnalyzers=false --filter 'FullyQualifiedName~LargeReaderHostingSafetyTests|FullyQualifiedName~MediaSelectionCounterEvidenceTests'
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  -p:RunAnalyzers=false -p:BuildProjectReferences=false
# Start Xvfb + Openbox once, then run this once in that display, with TMPDIR pointing at a private scratch directory:
MEOWCHAN_MEDIA_PAGE_ONLY=1 MEOWCHAN_MEDIA_HEADER_CAPTURES="$CAPTURES" \
  dotnet tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll
```

### Build caveat

The default analyzer-enabled build is blocked by pre-existing `CA1001` at `Desktop/GalleryVideoPlayer.cs:1754` (`VideoCleanupWatch` owns `_timer` but is not IDisposable). The functional builds used command-line-only `RunAnalyzers=false`; no assertions, analyzer policy or baseline were weakened in the repository. The native runner build completed with zero errors. This evidence does not claim a green default analyzer gate.
