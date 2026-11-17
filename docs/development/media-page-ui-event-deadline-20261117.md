# media-page UI event deadline — 2026-11-17

## Finding

**Product-side keyboard routing defect, not a deadlock or an undersized deadline.**

The `media-page` registry scene runs `MediaPageChecks.RunAsync`. The failing
condition was in `MediaPageChecks.BooruMode.cs`, immediately after sending Delete
to the selected `surface` tag in the Booru sidebar:

```csharp
page.TagCountTask.IsCompleted && Surface().Polarity == 0 && page.Tiles.Count == all
```

`MediaPage.OnPageRateAndTagKey` tunnels through the page before the sidebar list.
It classified Delete as the page-wide `tagging.remove` shortcut and called
`OnGridKeyDown`, which marked the event handled and attempted to remove selected
media from the tagging queue. Consequently, `MediaTagSidebar.OnListKeyDown` never
received Delete to remove the filter. The tag stayed excluded, with 11 of 32
tiles shown. Waiting longer cannot repair an already consumed key event.

## Reproduction and controls

Base checked out from `origin/goal/gesamtprogramm-profile-a`: `740d0d565`.
All runs used the source-built Debug/net10.0 smoke harness, Xvfb (1920x1080),
Openbox, `MEOWCHAN_MEDIA_PAGE_ONLY=1`, `MEOWCHAN_DESKTOP_SMOKE_XVFB=1`, and
`MEOWCHAN_MOTION=off`. Fixtures/profiles were disposable and under the session
scratchpad; no user profile was opened. Final runs also used a private session
bus (abstract Unix socket, avoiding the long scratchpad pathname limit), private
XDG directories, and no inherited desktop/session routing.

- Baseline with diagnostics only: failed in **139 s** total; the specific wait
  expired after **30,003 ms / 8,458,830 probes**. The 711 preceding successful
  waits were at most 839 ms. Last action: Delete after Shift+Enter excluded the
  selected sidebar tag.
- Control with a real 20 ms delay between probes, but no product fix: failed in
  **168 s** total at the same condition, after **30,019 ms / 1,344 probes**.
  Immediately after Delete, the event was handled, its target was attached, the
  selected `surface` tag still had polarity -1, and the grid still had 11/32 tiles.
  The preceding Enter and Shift+Enter count updates took 123 and 97 ms.
- This control rules out a missing dispatcher yield as the explanation for this
  failure. Replacing the spin alone did not fix it.
- A genuinely low-load reproduction was **not available on this shared host**:
  16 logical CPUs, baseline one-minute loads 85.35 and 53.34, observed loads up
  to 110.92, and substantial CPU/I/O pressure. Do not interpret these as low-load
  measurements. No unrelated workloads were stopped. The repeatable consumed
  event and the paced-poll control establish the defect independently of load.

## Fix and regression coverage

- The page-level shortcut router leaves Delete/Backspace originating inside the
  Booru tag list to that list. Grid and other page shortcuts keep their existing
  routing. No protected tagging/settings/gallery files were changed.
- The existing real-page smoke now asserts that Delete actually removed the tag
  term synchronously, before waiting for counts. It additionally covers the
  sidebar's Backspace gesture, explicitly selecting the restored row after its
  zero-count temporary disappearance during counting. Each key logs handled
  status, tag-term count, and tile count.
- `UntilAsync` retains the **30 second** deadline. It uses a monotonic stopwatch,
  records caller/line/condition, elapsed time and probe count, and pauses 20 ms
  between probes to avoid millions of useless dispatcher operations. This is
  diagnostic/polling hardening, not the root-cause fix or a budget increase.

## Validation

App and harness builds passed with existing warnings and zero errors.
`git diff --check` passed. Three final serial runs of the complete scene:

| Run | Result | Wall time | Starting 1-minute load | Delete/count wait (four theme/language combinations) | Backspace/count wait |
| --- | --- | ---: | ---: | --- | --- |
| 1 | PASS, exit 0 | 459.765 s | 103.01 | 105–155 ms | 141–362 ms |
| 2 | PASS, exit 0 | 470.536 s | 65.67 | 163–740 ms | 0–221 ms |
| 3 | INCOMPLETE, outer cap exit 124 | 570.213 s | 81.58 | 256–342 ms | 289–1,604 ms |

All twelve Delete and all twelve Backspace regression checks passed. Neither
complete run nor the incomplete third run reported a UI event deadline.
The complete runs each processed 1,065 successful condition waits; their longest
were 9,745 and 11,865 ms. Run 3 processed 862 successful waits (maximum 17,893 ms)
and passed the Booru checks in all four combinations, then the outer process cap
terminated it later in `OwnStateJourneyAsync`, after its English tooltip update
had passed. It is **not** counted as a third full pass. The run included decoder
buffer allocation failures and severe host contention; the cause of its overall
slowness was not isolated. A low-load full rerun remains a validation limitation.

Raw logs, load snapshots and the isolated-run driver are in the task scratchpad:
`media-baseline/smoke.log`, `media-delay/smoke.log`,
`media-isolated-{1,2,3}/smoke.log`, `media-isolated-results.json`, and
`run-media-isolated.py`. This document preserves the measurements beyond session
scratchpad retention. The whole-scene caps were harness execution limits, not
changes to the checked-in per-condition deadline.

Early diagnostic iterations also exposed two harness-instrumentation issues
(reading a transiently absent row immediately after Delete, and reusing selection
after that row disappeared), both corrected before final validation. A separate
300 s attempt without a private bus was stopped under renewed host overload;
this was an outer process cap, not the 30 s UI deadline.
