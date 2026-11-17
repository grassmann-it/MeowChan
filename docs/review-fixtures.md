# Private stage-0 review fixtures

This harness is tooling only: all desktop entry points live in the non-packable
`tests/MeowChan.DesktopSmoke` executable. It never accepts an existing profile.
It does not inspect, sample or copy a user's library, filenames, tags or boards.

## Run

Requirements: .NET 10, Python 3, ffmpeg with libvpx, and the Linux isolated desktop
prerequisites of `scripts/run-desktop-smoke.py` (Xvfb/Openbox/private DBus).
There is deliberately no native-desktop fallback.

```sh
dotnet build tests/MeowChan.DesktopSmoke -c Debug -m:2 -p:RunAnalyzers=false
python3 scripts/run-review-fixtures.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll \
  --output "$COMMANDCODE_SCRATCHPAD/review-matrix" \
  --seed 20261117 --media 100 --timeout 1800
```

Output must not exist. Separate runs cannot clobber captures: this calls the
existing runner's `run_mode`, including its private assembly copy, isolated
working/profile/XDG tree and owned-process cleanup. This is **not** the smoke
suite; no registry entries or unrelated smoke assertions run.

The matrix is 13 surfaces × 5 widths (760/900/1099/1100/1440 client DIP) ×
2 themes × DE/EN = **260 scenes**, including the optional gallery (240 core).
Height is 920 DIP; workspace mode is Advanced. `booru-workbench` denotes the
watcher/workbench tab; `booru` denotes search results. The actual retained Booru
page uses existing in-memory service fixtures, never HTTP.

For a single debugging scene add `--scene media-760-dark-de`. For profiles without
screenshots add `--generate-only` (retains `run/review-artifacts/fixture-profile`).
Capture runs instead generate into the runner's disposable working-directory
profile, avoiding host usernames/project paths in Settings labels and dump text;
only its synthetic `fixture.json` summary is retained with the captures.
Repeat `--media` using **0, 1, 100, 1000, 10000**
in *separate output directories*. The 260-scene matrix uses the selected size,
not a disguised 5-size cross product. Zero media deliberately reports reader /
large-reader / gallery as missing: it must not substitute a different surface.

```sh
python3 scripts/test_review_fixtures.py
```

## Synthetic data contract

`ReviewFixtureData.cs` creates geometric Skia PNGs (including 4K, tall and wide)
and a half-second ffmpeg/lavfi WebM. No external media inputs are read. PNGs carry
deterministic per-medium trailers so the index does not collapse equal hashes.
Video muxing and encoding request bit-exact operation and a single encoder thread.
The seed changes colors, hashes and tag identities; IDs/order/text/time are fixed.
Profile-root paths, measured timings and SQLite physical bytes are not portable
content checksums. Reproducibility means the generated media and logical records,
not byte-identical process logs or screenshot font rendering across machines.

Threads use sizes 1, 50, 250, then 1250 posts until the requested media count is
exhausted. Titles/posts include long runs, CJK and German compounds. Tag assignment
probes are 0/1/8/30/128 tags per medium, applied to the first 1000 media to stay
within the store's assignment ceilings. The live store contains **4096 distinct
synthetic tag definitions** (bounded by the product's runtime definition ceiling,
currently 20000, not the per-item tag-set limit). A separate
`synthetic-vocabulary-probe.json` exports those words for suggestion/limit tests.
Queue entries are resolved from the synthetic media index; downloads are passive
synthetic snapshots.

## Evidence and fail-closed manifest

Top-level `captures.json` lists every expected scene, including missing scenes,
seed, media size, theme/language, measured geometry, artifact paths and SHA-256
checksums. It is atomically replaced. Each scene is written under a private
`.pending-*` location and committed by a same-filesystem directory rename only
when the existing `ReviewArtifacts` writer has finished the complete bundle:

* `screenshot.png`
* `control-tree.json` (every realized control, stable tree ID, name or fallback
  type@path name, bounds, visibility/enabled/focus state, text, desired size,
  clipping, accessible name/help, hover/classes; detached roots included)
* `timing.json` (preparation/navigation/settling/bundle capture and total times)
* `state.json`, `grayscale.png`, `bundle.json` completion marker

The Python collector rechecks hashes, the marker and required dump fields. A
missing dump is **missing**, never reviewable, even if a screenshot exists. A run
with missing scenes or runner failure exits nonzero. Consumers should use the
manifest and ignore `.pending-*` trees. A visual finding must cite a dump name
(and preferably its `id`), not invent a control from pixels.

## Deterministic prechecks

`prechecks.json` is machine readable. The command-catalogue export checks effective
default bindings on Windows/Linux/macOS with the real collision/scope resolver.
Runtime EN/DE catalogues (not regex-parsed C#) are compared with all bundled packs
for missing/extra keys, missing locale plural forms and byte-identical English.
CJK languages correctly do not require `.one`; RU/PL/CS require `.few`.

XAML Margin/Padding/Spacing/RowSpacing/ColumnSpacing literals and setters are
compared to `Workspace.Space.*`; matching numeric literals are separately listed
as non-token references. Literal C# Margin/Padding/Spacing assignments are also
checked; dynamically computed spacing is outside this source check. Palette foreground/background pairs are calculated
with the WCAG sRGB formula (4.5 text, 3 non-text); these are **token** contrast
measurements, not invented pixel measurements. Byte-identical terms and raw
spacing observations are not automatically translation/design defects.
Unavailable exports / unresolved colors appear in `incomplete`, not as passes.

## Explicit protocol limits

This static matrix is stage-0 capture instrumentation, **not** a claim that the
entire protocol §2.0 interaction programme passed. Grayscale is not OS forced
colors. Native popup pixels are not composited, though detached control roots
are dumped. Timing logs measure scene setup and capture, not physical-input to
photon latency. Hover-release, continuous resize, input heartbeat/memory,
pseudo-localization, focus walks, persistence/restart and live-region event
journeys need dedicated subsequent scripted runs. The existing `ReviewArtifacts`
gesture API remains available for them. No model is called by this harness.
