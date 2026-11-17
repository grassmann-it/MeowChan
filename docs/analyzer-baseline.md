# UX-LEDGER-22: curated analyzers and temporary debt baseline

Frozen **2026-11-17**, based on `origin/goal/gesamtprogramm-profile-a` at `e2ee324ef`.
Debt fixes belong to **UX-LEDGER-23**, not this change.

## Policy

`Directory.Build.props` extends (does not replace) the existing version/Release-lock configuration:

```xml
<EnableNETAnalyzers>true</EnableNETAnalyzers>
<AnalysisMode>Minimum</AnalysisMode>
<WarningsAsErrors>$(WarningsAsErrors);CA2000;CA2213;CA1001;CA2016</WarningsAsErrors>
```

`.editorconfig` explicitly enables those four rules at warning severity, including rules not enabled
by Minimum. `WarningsAsErrors` promotes exactly those four CA rules to errors. Existing NuGet security
errors in individual projects remain intact. There is no blanket `TreatWarningsAsErrors`.

The eight `NoWarn` entries each have an adjacent one-line reason: CA2007 (UI context), CA1822
(instance-shaped APIs), CA1031 (intentional UI exception boundaries, including
`Desktop/GalleryTagPanelSuggestions.cs:108`), CA1515 (API visibility review), CA1308/CA1307/CA1305
(identifier conventions), CA1032 (restricted domain exception construction). Other Minimum diagnostics
remain visible as warnings; they are not hidden by the debt baseline.

## Baseline: exact diagnostics, not whole files/types/methods

`build/analyzer-baseline-2026-11-17.tsv` is the dated, reviewable snapshot.
`Directory.Build.targets` adds the `scripts/AnalyzerBaseline` Roslyn diagnostic suppressor and the
baseline as an AdditionalFile. The helper references the repository's pinned SDK compiler assemblies;
it adds no NuGet dependency and is not referenced as runtime application code. Its own build and its
compiler tests are excluded from the analyzer ProjectReference to prevent recursion.

Each entry matches **assembly + repository-relative path + SHA256 of the entire LF-normalized source
file + rule + exact start/end line/column + SHA256 of the invariant diagnostic message**. Only those
four rules can be suppressed. Missing baseline, changed file, new location/message/assembly or new
file all fail closed. Moving the checkout or switching between LF and CRLF does not invalidate it.

This deliberately conservative mechanism cannot silently grandfather a newly introduced diagnostic
inside an existing type or method. **Editing a debt-bearing file invalidates all its entries**. The
follow-up should clean that file's four-rule debt, not regenerate its suppressions. This is stricter
than a symbol-level GlobalSuppressions file, which could hide future leaks in the same member.

### How the baseline shrinks

1. In UX-LEDGER-23, fix a debt-bearing file's four-rule diagnostics (or delete that unreachable file).
2. Capture unsuppressed diagnostics for the affected project(s), forcing compilation:

   ```sh
   mkdir -p "$COMMANDCODE_SCRATCHPAD/analyzer-capture"
   dotnet build path/to/project.csproj --no-incremental \
     -p:DisableAnalyzerBaseline=true -p:WarningsAsErrors= \
     -p:AnalyzerBaselineCaptureDirectory="$COMMANDCODE_SCRATCHPAD/analyzer-capture"
   python3 scripts/analyzer-baseline.py "$COMMANDCODE_SCRATCHPAD/analyzer-capture"
   dotnet build path/to/project.csproj
   ```

   Use an empty capture directory each time; never mix stale logs with current sources. Capture also
   covers project references. The warning-only override is for inspection, never CI/release builds.
3. The pruning command intersects the old baseline with the capture and **cannot add any entry**.
   It preserves assemblies not captured by this targeted build. If any remaining diagnostic has no
   exact old entry, it refuses the update; finish cleaning the edited file. Commit the removed TSV
   rows with the fixes. Do not reseed the baseline or change source hashes to accommodate new debt.
4. Delete the suppressor, TSV, build wiring and prune tooling when the baseline is empty.

Compiler-level tests (no GUI):

```sh
dotnet run --project scripts/AnalyzerBaseline.Tests/AnalyzerBaseline.Tests.csproj
```

The nine cases cover the exact match, new file, new symbol in an existing file, edited source,
different assembly, changed message, absent baseline, pruned entry and CRLF checkout.

## Dead-code reference proof

Before removal, repository-wide exact-identifier searches over **both `*.cs` and `*.axaml`**, including
tests and reflection strings, found exactly **one occurrence (the declaration)** for each of these
14 members. No XAML subscription/binding or test/reflection caller matched. Locations below are the
base revision's locations, not line numbers after deletion:

| File | Deleted members |
| --- | --- |
| `Desktop/Views/Archive/ArchivePage.Selection.cs:393-402` | `OnArchiveRemoveSelection`, `OnArchiveCopySelection`, `OnArchiveCheckSelection`, `OnArchiveRetrySelection`, `OnArchiveSelectionDetails`, `OnArchiveLeadAction` |
| `Desktop/Views/Archive/ArchivePage.axaml.cs:965` | `ShowPreviewAsync` |
| `Desktop/Views/Plugins/PluginsPage.Actions.cs:351` | `CreatePluginActionMenuItem` |
| `Desktop/Views/Archive/ArchivePage.PostBookmarks.cs:141` | `CreatePostCopyItem` |
| `Desktop/Views/Archive/ArchivePage.Ratings.cs:87` | `CurrentMediaRating` |
| `Desktop/Views/Archive/ArchivePage.Gallery.cs:283,293` | `OnThreadReadMenu`, `OnThreadExportMenu` |
| `Desktop/Shell/MainWindow.Plugins.cs:46` | `OnPluginsNavigationClick` |
| `Desktop/Commands/CommandBindings.cs:385` | `BindShortcutHelp` |

The six Selection shims are actually **five async-void methods and one synchronous void method**;
the requested line numbers identify all six unambiguously. Their used Task-returning bodies remain:
`CopyArchiveSelectionAsync`, `RemoveArchiveSelectionAsync`, `CheckArchiveSelectionAsync`,
`ShowArchiveDetailsAsync`, plus the used `RunArchiveLeadAction`. Keyboard dispatch and current menu
builders still call these. `OnThreadGalleryMenu` also remains: a smoke check calls it by reflection.

`OnOverlayPointerExited` was renamed to `OnOverlayPointerMovedOrExited` in its declaration and both
PointerEntered/PointerExited subscriptions. Its geometry-based body is unchanged. The reflection
name in `GalleryTerminalRetirementTests` was updated without removing/weakening any assertion.
