# Desktop review artifact bundles (schema 1)

The non-packable `tests/MeowChan.DesktopSmoke` executable implements the evidence
triple from review protocol §2.0. Nothing is compiled into, injected into or added
as a dependency of the release app. Existing assertions remain authoritative.

## Run and validate

```sh
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -c Release
python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll \
  --registry --only tagging-page --only gallery-favorites --only settings-search \
  --review-artifacts --timeout 240 --output artifacts/review-20261117
python3 scripts/check-review-artifacts.py \
  --directory artifacts/review-20261117/tagging-page/normal/review-artifacts
```

Use a fresh output directory. Linux retains the existing private Xvfb/Openbox/DBus
isolation; native platforms retain the runner's explicit disposable-desktop opt-in.
The flag also works without `--registry`. For direct harness invocation,
`MEOWCHAN_REVIEW_ARTIFACTS=/absolute/fresh/output` is the output root; the Python
runner instead binds it to each scene's destination, including retries. No flag
means no writer, timer or extra render opportunity.

## Files

Each scene contains `review-artifacts/NNNN-gesture-or-capture-label/`:

| File | Contents |
| --- | --- |
| `bundle.json` | `schema_version`, `label`, `complete`, `files`, rendering limitations. Written **last**; absence means partial evidence, not a successful bundle. Existing bundle directories are never reused. |
| `screenshot.png` | Actual window client bitmap, captured by `Program.CaptureWindow` at observed `RenderScaling`. Existing PNG checkpoints are copied, not rendered again. |
| `grayscale.png` | Same PNG transformed with Rec.709 channel weights; **not OS forced-colors**, not a separate UI state. |
| `control-tree.json` | Window-client DIP bounds; realized visual hierarchy including hidden controls; separate `detached_roots` for discovered open popup trees, with popup-local DIP coordinates. |
| `state.json` | Independent control/model state described below. |
| `timing.json` | Gesture clocks, UI heartbeat, focus trace, memory high-water mark; or an explicitly untimed checkpoint. |

Tree elements expose `id`, `parent_id`, `name`, `type`, `bounds`, `visible`,
`effectively_visible`, `opacity`, `is_enabled`, `effectively_enabled`,
`is_pointer_over`, `is_focused`, `is_keyboard_focus_within`, `focusable`,
`is_hit_test_visible`, sorted `visual_state_classes` (including `:focus`,
`:pointerover`, etc.), attached `accessible_name` / `accessible_help_text`,
`tooltip`, `text`, `desired_size`, `clip_to_bounds`, `clipped_or_overflow`.
The last flag is a **candidate**, computed from desired-size overflow and ancestor
clip/viewport bounds, not a glyph-ink, occlusion or arbitrary clip-geometry test.
Accessible fields are Avalonia attached properties, not a native AT event stream
or a screen reader's resolved/derived naming algorithm.

State fields include `selection_sets` (native model indexes, selected item keys,
lead and visibility), `counter_labels`, `view_modes`, `filters` (text, selected
index, toggle state), `panel_widths`, `open_popovers_overlays`, `detached_popups`,
`owned_windows`, theme/language/window activation and geometry. `tagging` adds
canonical selected queue keys, independent count/counter/progress, filter and
list/grid mode, busy/stamp/confirmation state. `media` adds canonical selected
file identities, sort, saved panel width and selection overflow membership.
`background_work_tokens` lists direct product-control Task/CTS/busy/pending
fields plus one product-helper level, using field paths rather than object hashes.
A CTS's presence is **not** evidence of a running task: `pending` is null and
`cancellation_requested` is explicit. Unexposed jobs are **not inferred idle**.

Ordering and field names are deterministic. Tree IDs are structural child-index
paths, not persistent item IDs: compare controls by name/type/parent as layout
changes, and selections by domain keys. Ignore timing values and normalize
fixture/profile paths when comparing separate runs. Virtualized, unrealized rows
are intentionally absent from the visual tree; selected model keys are separate.

## Timed gesture coverage

`ReviewArtifacts.GestureAsync(window, label, input, visibleAck, settle)` surrounds
one scripted operation. `settle` reuses the existing scene's completion wait;
assertions have not been relaxed. The visible-ack predicate is sampled at Avalonia
animation-frame opportunities. This measures **observed feedback**, not OS input
arrival, compositor presentation or photon latency. Null acknowledgment stays
null, with an explanation; it is never replaced by dispatcher-queue completion.

All three timestamps (`t_input`, `t_first_visual_ack`, `t_final`) use monotonic
milliseconds from one process epoch. A 25 ms UI-dispatcher heartbeat retains all
samples and every successive gap **strictly greater than 100 ms**, including
synchronous input stalls via endpoint samples. Its coverage ends before bundle
rendering/serialization, so writer work is not charged to the product gesture.
`focus_trace` records initial focus and routed focus gains (control, attached name,
TopLevel scope); tree/state record final focus and activation. Native popup focus
routing and full focus-scope walking still require dedicated probes.

Initial integrations:

- **`tagging-page`**: all 20 existing navigate-away/back cycles (list/grid × one/two
  selected × five revisits), Delete, list-context-menu open/close: 43 timed gestures.
- **`gallery-favorites`**: mark favourite, open held-F confirmation, Enter to keep:
  three timed gestures. Existing repeat/failure/pointer assertions still run.
- **`settings-search`**: initial `cache` query and six existing width transitions
  (640, 1100, 1600, 819, 820, 1100 DIP): seven timed gestures.

Every other existing `Program.CaptureWindow` checkpoint gains the same bundle
files when enabled, but its timing `kind` is `checkpoint`, with null input/ack
and an explicit unverified reason. **Not every input in these scenes or in the
entire registry is timed yet.** Extend the wrapper at each additional scripted
input boundary; do not relabel screenshots as latency evidence. Gestures must
not overlap. Capture failures fail the smoke; a scene failure retains prior bundles.

## How to review

1. Keep `results.json`, `registry.json` and scene `output.log` alongside the bundles.
   Validate bundle integrity with `check-review-artifacts.py`; its success is not
   a UX pass. A failing scene remains red even if its earlier bundles are valid.
2. Apply Block 0 first: compare canonical selection keys, native model indexes,
   count labels, focus and visible selected classes across away/back pairs. The
   existing tagging assertions verify parity independently of these JSON files.
3. Compare popup-open/closed trees for stale `is_pointer_over`, classes, visibility,
   opacity/hit-testing and cancellation state. Check widths/overflow candidates
   across settings-resize steps. Null accessibility text is explicit missing data,
   not an invented label. Window bitmaps do **not** composite detached native popup
   pixels; popup trees are provided, but visual popup review needs its own capture.
4. Subtract the timing clocks, inspect `heartbeat.stalls` and inspect grayscale
   state pairs. Record unobserved acknowledgments and uncovered matrix cells as
   **unverified**, not green. This harness alone does not run the protocol's whole
   edge/corner hover matrix, 10k scale probes, restart/persistence, live-region
   event capture, pseudo-localization or native forced-colors matrix.
