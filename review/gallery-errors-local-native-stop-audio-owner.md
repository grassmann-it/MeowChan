# Local gallery-errors terminal cleanup: own the fixture's audio backend

Base: `dd80b57cd3356a137ddb1bf6a7262892fabd5245` (includes readiness `20469f7d3`).
Linux X11 software video; this is NOT evidence of a macOS decoder regression.
No production player, media-input assertion, demux, timeout or native ownership
order changed. No CI dispatched.

## Original failure and exact reproduction

- `/tmp/mac-native-readiness-gallery-verified/normal/output.log`: four strict
  root/pointer cases passed, first Playing notification and terminal cleanup
  passed, second `terminal stop begin` never completed before the 180s watchdog.
- Two instrumented full-scenario repeats stopped earlier at the unchanged
  closing-root OS-pointer-entry assertion (not at target geometry). Logs:
  `/tmp/gallery-cleanup-diag-{1,2}/normal/output.log`. These are failures, not
  native-cleanup evidence; no assertion was weakened to bypass them.
- To isolate cleanup, a temporary diagnostic build ran the existing four
  `CheckNotificationTransportAsync` cases only, with all their assertions intact.
  It printed `SMOKE DIAGNOSTIC SUBSET ... not full gallery-errors acceptance`.
  The subset change is NOT in the commit or final build.
- `/tmp/gallery-cleanup-subset-1/normal/output.log`: first notification did not
  decode real frames before its existing deadline. During disposal, lifecycle
  request30/generation2 retained playback generation1 and ALL native resources.
  Phase `native-stop`, last completed `native-audio-detach`; operations-wait had
  already returned. No observer getter, engine join, UI detach or show admission
  was blocking this drain.
- Native stacks, captured while pending:
  `/tmp/gallery-cleanup-subset-1/native-stacks.txt`:
  - Thread19: `libvlc_media_player_stop -> input_Close -> pthread join`.
  - Thread5 (Vorbis decoder): `libvorbis -> libalsa -> snd_pcm_open ->
    _snd_pcm_pipewire_open -> pw_thread_loop_stop -> pthread join`.
  - Input/MKV thread waits in libvlccore. The private Xvfb session had no working
    audio server, so Pulse connection failure fell through to host ALSA/PipeWire.
- This reproduced Stop eventually returned after **18,484.759 ms** (the native
  debugger interrupted the process during observation). Final engine release
  took **66.778 ms**. Do not call this a proven permanent deadlock, or claim the
  original watchdog run had a captured native stack.

## Controlled comparison, same diagnostic assembly

Only invocation changed: existing `scripts/with-private-null-audio.py` owned the
private audio graph, then launched the same four notification/cleanup cases in
private Xvfb. No dummy video, silent replacement clip, audio-track disabling,
assertion removal, engine reset or timeout extension.

`/tmp/gallery-cleanup-subset-audio/normal/output.log`: all four Playing/Paused x
Overlay false/true cases AND terminal cleanup passed, exit0. Trace native Stop:
**22.866 / 1.101 / 8.821 / 0.989 ms**; engine release:
**4.095 / 3.914 / 2.893 / 3.172 ms**.
`/tmp/gallery-cleanup-subset-audio.json`: private muted sink, zero hardware cards,
closed graph verified before/after, owned services exited, cleanup complete.

The removed per-medium engine reset cannot explain this local change: prior code
required `playback.NativeFile != null`; `VideoNativeInput.UsesFileInput` is macOS
X64 only. Linux used StreamMediaInput before and after dd80. No speculative
reversion or native resource abandonment was attempted.

## Patch

- Register `gallery-error` with existing `FunctionalAudio`, so local Linux registry
  execution owns the private sink (as gallery-features already does).
- Wrap only the Linux `gallery_errors` workflow step with the existing audio owner,
  forwarding the selector/fixture explicitly through its sanitized environment.
  Keep the original 300s CI limit, non-Linux native path and artifact location.
  No offline watchdog/wrapper scripts edited.
- Optional `MEOWCHAN_GALLERY_CLEANUP_TRACE=1`: periodically flush bounded existing
  native/cleanup performance phases plus up to six managed lifecycle/ownership
  samples while fixture disposal is pending. These never acquire the native gate
  or release any resource. Add fixture case/Stop-return markers.
- Focused workflow/registry contract guards prevent losing this audio-owner wiring.

## Final validation (full source restored; only gallery-error selected)

- Release DesktopSmoke build: **0 errors, 34 warnings**, `--no-restore -m:1
  -nodeReuse:false -p:UseSharedCompilation=false`; `/tmp/gallery-cleanup-final-build.log`.
- `python3 scripts/run-desktop-smoke.py --assembly
  tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll
  --registry --only gallery-error --timeout 180
  --output /tmp/gallery-cleanup-final-registry`
  **1 passed, 0 failed, 0 skipped; one attempt, 41 seconds, exit0**.
  All four strict root/pointer cases, all four notification/cleanup cases, real
  unavailable-media/settings failures and reader-return checks completed; normal
  shutdown and exact scenario/PASS recorded. No broad GUI suite.
- `/tmp/gallery-cleanup-final-registry/registry.json`,
  `gallery-error/results.json`, `gallery-error/normal/output.log`,
  `gallery-error-audio.json` retain verdict and private-backend cleanup proof.
- New fixture invocation contract fails against the original workflow (expected
  missing private-wrapper assertion); restored patch passes. Two focused existing
  registry dispatch/verdict tests also pass: **3 tests, 0 failures**, 0.082s.
  `/tmp/gallery-cleanup-fixture-contract-{before,after}.log`.
- `git diff --check` clean. Native Intel media-input and package success were
  reported separately by the parent; this patch makes no changes to those paths.
