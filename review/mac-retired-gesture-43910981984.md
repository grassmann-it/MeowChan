# Intel retired-gesture transition: evidence and observer correction

Base: `7fd371666`; touched paths have no changes through integration `022084e64`.
Run: `43910981984`, source `1e986ec87687da95c4a2104870c662a0ec4d9228`.
Artifacts read from `/tmp/mac-popup-retest-43910981984/validation-macos-15-intel-native-X64/artifacts/media-native-input/`.

## Verified, not inferred

- Popup fix passes: native owner reentry, More pointer press/release, overflow open,
  selector ready probe 1; paused-drag-key and custom-routing-loop pass.
- Last selection is post 2002 at 156733.8428 ms. Actual PointerReleased follows at
  156735.3626 ms. First subsequent playback observation times out at 160847.655 ms
  (unchanged 4000 ms observer limit), before a successor playback sample exists.
- Failure screenshot shows post 3/3, `Verifying local video…`, clock `0:00 / --:--`.
  ShowCore has reset the successor UI; this does not identify which worker operation blocks.
- Shutdown reports gallery-cleanup returned after 3.0 seconds and native ownership
  released true. The evidence does not establish a permanent deadlock.
- VP8 get_buffer/thread_get_buffer errors also precede successful stages. They are
  not sufficient evidence for a specific decoder/thread teardown bug.
- No operation-phase trace/native stacks were collected in this run. The observer
  timeout alone cannot distinguish semaphore contention, worker scheduling, native
  creation/play, or a blocking native getter.

## Minimal correction

The existing observer, unlike the command adapter, read whichever native slot it
found after waiting for `_operations`. It could read retiring A under a selected B,
or B for an observation requested on A. Stop can also retire a generation during a
native read. The production observer now fences both admission and result publication
by the requested lifetime generation. Retired reads are explicitly non-ready; no
clock/frame evidence is invented, cached or accepted for a successor.

The smoke's WaitAsync timeout previously left its observer queued. Cancel that
queued observation after the sample finishes/fails. An admitted native read still
owns the semaphore until native return; cancellation never frees live inputs or
allows cleanup to run concurrently with a getter.

Four deterministic regressions cover predecessor/successor slot mismatch, a queued
old request, abandoned queued observation, and retirement/cancellation while native
read holds ownership. These are proven observer defects, **not proof of the cause
of the Intel 4-second stall**.

## Diagnostic handoff

The same bounded opt-in performance sink now records observer scheduling/gate/native
reads (individual getter names), seek gate/set/readback, audio probes, native
runtime/engine/player/media creation and materialization. Existing show/verify/play,
command and cleanup phases join that timeline. `interaction.json.playbackTrace`
contains video-only events and the timestamp frequency. The timeout event also
contains current managed generation, published playback generation, cleanup phase
and the last 256 video events; collecting these never takes `_operations` or calls VLC.
The four-second sample deadline, 6.5-second readiness loop, native input, playback
assertions and release ordering are unchanged.

Harvey's `7092d84e6` changes file-backed ISO-BMFF demux selection. The failing media
input fixture here is VP8/WebM, not H264/AAC MP4, so that candidate is not imported.
Neither its installed MP4 stall nor this native transition stall is claimed fixed.

## Local validation

- Release focused DesktopTests: 38 passed, 0 failed (observation/lifetime/command/
  timeline/demux; includes the four new observer tests).
- Release DesktopSmoke build: 0 errors, 34 warnings.
- Python media-interaction wrapper contracts: 12 passed (mocked driver, not native acceptance).
- `git diff --check`: clean.

No CI dispatch. No macOS execution for this patch. ARM `43910975654` was cancelled
without acquiring a runner (user-provided terminal job evidence), not a code failure;
ARM native validation remains missing.
