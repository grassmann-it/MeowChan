# URSA-21 isolated image comparison

Product default stays on `Image`/`TranslateTransform`. This is a separate executable,
with no product menu, profile, network access, player, persistence, rating owner or
decoder change. B really uses the themed Ursa 2.2.0 `ImageViewerPresenter`; A uses
the production image/explicit-size/translation approach and `GalleryGeometry`.
`GalleryImageDecoder.cs` is compiled by link from the working copy, including
`OpenVerifiedReadAsync`, its serialized gate, 64 MiB/80M budgets and cancellation.
The only sample shim is the decoder's EN/DE language lookup.

Ursa's published package repository commit is
`00fef728ce87cc8cbc9085f3162867daa1d57490`. Its real API is `Source: IImage`,
`Zoom`, `OffsetX/Y`, `Min/MaxZoom`, `FitToView()`. Zoom is in image-DIP units;
physical 100% therefore sets `Zoom = 1 / RenderScaling` for these 96 DPI fixtures.
The B adapter supplies automatic Fit on resize and pan clamps. Native Ursa drag,
wheel, arrow and presenter code remain active. Manual zoom remains fixed across
resize; pan is clamped to the reachable image. A uses the same resize convention.
Ursa and A wheel minima/default arrows are deliberately observable differences;
the sample does not conceal them with a replacement shared gesture handler.

The actual 2.2.0 Semi template omits bindings for the presenter's independent
MinZoom/MaxZoom. Its default minimum 0.1 crops a large image even when the outer
ImageViewer reports the correct smaller Fit. The B adapter therefore synchronizes
both limits when the template is applied. Regression checks inspect the actual
presenter Zoom and Render destination rectangle at Fit before/after resize and at
100%, in addition to the outer value. Earlier `231ef1a5` performance captures used
the cropped B image for this fixture and cannot establish equal-workload resource
or rendering equivalence; preserve them as the observed failure evidence.

Both sources are cleared before disposal, exactly one variant attaches at a time.
The first pass hands the **same Bitmap instance** successively to both variants.
The second pass invokes the **same verified production Original decode** for each
variant, separately recording decode and first render timings. Fit does not mean
a different thumbnail file. Original is necessary to inspect real physical 100%.

## Commands for W20 only

All dotnet/GUI executions belong to W20, serially under the existing lock. Run
from the owning checkout (or its integrated successor). No personal profile is
used or required. Fixture generation and analysis use Python 3 + Pillow.

```sh
python3 samples/MeowChan.ImageViewerComparison/generate-fixtures.py --output /tmp/ursa21-fixtures-UNIQUE
flock /tmp/meowchan-ui-1.0-build.lock dotnet build samples/MeowChan.ImageViewerComparison/MeowChan.ImageViewerComparison.csproj -c Debug --nologo
flock /tmp/meowchan-ui-1.0-build.lock /usr/bin/time -v -o /tmp/ursa21-time-UNIQUE.txt env AVALONIA_GLOBAL_SCALE_FACTOR=1 LIBGL_ALWAYS_SOFTWARE=1 dotnet samples/MeowChan.ImageViewerComparison/bin/Debug/net10.0/MeowChan.ImageViewerComparison.dll --fixtures /tmp/ursa21-fixtures-UNIQUE --output /tmp/ursa21-en-dark-b-1-UNIQUE --auto --language en --theme dark --mode b --backend-description 'Linux X11; Mesa software; record actual GPU/driver/version here'
python3 samples/MeowChan.ImageViewerComparison/analyze.py /tmp/ursa21-en-dark-b-1-UNIQUE
```

Use the existing native display for input/focus checks, or W20's established Xvfb
recipe for in-process checks. `LIBGL_ALWAYS_SOFTWARE` requests Mesa software but
does not by itself identify Avalonia's renderer: record the actual backend/driver
and X11/Wayland/Xvfb/session details. Preserve stdout/stderr and `/usr/bin/time -v`
for whole-process max RSS in addition to the host's sampled peaks. Metadata binds
assemblies, source manifest, runtime, OS, scale, dimensions and fixture manifest.
Check **recorded `scale`**, not only the requested environment variable.

Full measurement matrix: EN/DE × **1, 1.5, 2** with the same chosen backend and
`--mode b --theme dark`; all six independent processes run 5 warmups + 20 measured
AB/BA sequences per valid fixture **in each pass**, then 50 open/close cycles per
variant, cycling all five valid fixtures. Warmups are flagged and excluded by the
analyzer. Repeat candidate regressions on unchanged hashes before deciding.

Layout/behavior matrix: EN/DE × Dark/Light × b/s/a × 1/1.5/2. Use
`--checks-only` in place of `--auto` for remaining combinations; it explicitly
records one task sequence, not performance/50-cycle evidence. The b/s/a options
are bounded host viewport presets (640×480 / 760×500 DIP), **not a claim that the
product workspace modes were exercised**. No product integration is made here.

Each task runs open → Fit → 100% → pan → two zooms → resize → oversized pan clamp
→ Fit → close. Assertions cover shared source identity, only one attached variant,
actual Ursa presenter, physical scale, fit/centering, stable manual zoom, bounded
pan, source clearance, owner counts, focus restoration, visible decoder errors,
and closing during a pending decode. Screenshots of every valid fixture/variant
at Fit are saved for independent visual inspection; generated pixels/grid make
distortion, transparency and 100% inspectable. Check 100% visually in the native
run as well; property checks do not prove physical output on a monitor.

The two-animation-frame fence measures submission, **not photon latency**. CPU
includes the same action sequence and frame waits; 20 ms process RAM sampling may
miss short codec peaks, hence the separate OS max-RSS measurement. Decode bytes
are width×height×4 host-owned BGRA bytes, not a claim about all native allocations.
Residual RAM and bitmap ownership are reported separately after each of 50 cycles;
zero host owners alone does not prove absence of native/cache growth. The analyzer
reports median/p95/min/max/MAD and conservative noise/regression candidates; it
does not turn one noisy run into a resource pass.

## Native input and independent decision

Launch the same command **without `--auto`**, into a fresh evidence directory.
Select each variant and fixture, open, and use the native mouse/keyboard. The
host writes `native-state.json` every 100 ms only in interactive mode, allowing
xdotool/AT-SPI/UIA/VoiceOver probes to read active/source/focus/physicalZoom/pan and
owner state. This telemetry is disabled during measurements. Record native tool,
platform, requested/actual scaling, screenshots and results alongside that file.

1. Tab to each labeled selector/action; open from its button. Fit includes the
   whole image without stretch. 100% makes the one-pixel raster physical 1:1.
2. Drag only the image, wheel and use arrows with image focus, then click/tab to
   each toolbar/selector and wheel there. Compare `physicalZoom`: wheel outside
   the image and focus changes must not zoom. A/B arrow/default gesture differences
   are observations for the decision, not automatically an advantage.
3. Resize at Fit and at manual zoom, including shrinking; image stays reachable.
   Escape closes from the image **and from every toolbar control**, sources/owner
   drop to zero, and opener focus returns. Start opening near-budget and immediately
   close; no late bitmap/status resurrection. Open all four failure fixtures and
   inspect their visible message and safe recovery.
4. On Linux AT-SPI/Orca, Windows UIA and macOS VoiceOver, inspect action names,
   disabled state, keyboard route and error announcement. Separate native evidence
   per platform; absent platforms remain open. No fabricated accessibility pass.

Gate: retain default if any contract fails, no concrete interaction/maintenance
benefit exists, or repeatable CPU/median/p95/peak RAM regression exceeds 10% outside
baseline variability; residual growth over 50 cycles also blocks adoption. Compare
alternative C: the existing approach plus only the targeted fix required by an
observed failure. B already requires DPI/refit/clamp adaptation; compare that actual
maintenance burden with A, not upstream advertised simplicity. One targeted adapter
round may be tested again. An actual negative result is a completed comparison.
No isolated product preview currently exists to adopt automatically; a positive
suitability result is sufficient and never authorizes a new gallery/reader feature.

Before the image tasks, full runs also record 20 empty-host CPU/RAM sequences with
the same twelve two-frame waits per task; these remain separate `emptyHost`
statistics rather than silently subtracting idle measurements of another duration.

W14's later media changes require final integrated `GalleryFitDecodeTests`,
`GalleryFitChecks`, `ReaderPreviewChecks` and gallery close/focus regressions by
W20. A sample PASS alone does not claim product regression coverage.

## Fixture provenance

`fixtures/nasa-PIA18033.jpg` is NASA's 640×640 Earth Blue Marble photo composite,
downloaded 2026-10-23 from
https://images-assets.nasa.gov/image/PIA18033/PIA18033~small.jpg . NASA API metadata
identifies NASA as secondary creator and JPL as center; catalog:
https://photojournal.jpl.nasa.gov/catalog/PIA18033 . The original NASA photograph
is US federal government work; the sample only crops it to landscape and portrait
offline. NASA imagery guidelines: https://www.nasa.gov/nasa-brand-center/images-and-media/ .
The grid PNG fixtures are generated locally; no runtime network request is made.
Fixture manifest records exact resulting hashes, including a declared-then-removed
missing file, damaged bytes, a sparse 64 MiB+1 file, 79M-pixel near-limit PNG and
81.009M-pixel over-limit PNG. No budget is raised for the test.
