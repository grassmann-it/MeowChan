# Intel native observation stalls: exact operation evidence and lifecycle fix

Run `43916342134`, job `133471424958`, source `0c1839030`.
Direct API confirmed completed/failure. Artifact `14250781957` downloaded to
`/tmp/mac-native-diagnostic-43916342134/`. Source evidence is
`artifacts/media-native-input/interaction.json`, including `playbackTrace`.
Frequency = 1,000,000,000; dropped performance events = 0.

## Exact blockers (not stalled observer getters)

| Stage | Observed blocker | Duration |
|---|---|---:|
| focused-pointer-away, post 2000 | Cleanup `engine-dispose-after-file-playback`, request 57; observer gen3 waits behind gen1 cleanup under `_operations` | 5226.773 ms |
| paused-drag-key, return to post 2000 | Engine disposal request81 + subsequent engine creation request84 | 3508.392 + 3585.410 ms |
| retired-gesture-transition, post 2002 | `video.native.engine-create`, request154/gen4; observer waiting for `_operations` | 4455.624 ms |

First timeout: sequence3698 starts engine disposal; 3708 starts observer gate wait.
At timeout cleanup evidence reports engine disposal pending for4582ms, after native
Stop returned in131.528ms and player/surface/media/input/stream/file were released.
Sequence3723 records actual engine disposal return after5226.773ms.

Retired gesture: previous media cleanup completes at8778. Verification request154
returns at8812 in0.715ms; runtime-init returns at8815 in0.001ms. Engine construction
starts8816, observer waits8820, and engine creation only returns8829 in4455.624ms.
There is no observer native-read entry before its timeout. A concurrent scrub-poster
engine disposal (8799→8822) takes2474.785ms; retaining a gallery engine also avoids
making every medium transition a cold engine/module lifecycle alongside that work.
No claim is made about VLC's internal lock without a native stack.

Paused return does not hit the per-observation deadline: one sample returns Stopped
and the next Opening after cumulative teardown/recreation consumes the unchanged
6.5-second readiness loop. This is not the previous selector bug. The new run's
selector probes are ready; no owner-reentry timeout is recorded.

## Minimal production remedy

Remove the Intel-only per-medium engine disposal introduced in `27526f0659`.
Reuse this player's engine until its terminal DisposeAsync, matching the existing
other-platform lifecycle. Every medium still gets a new MediaPlayer, Media, verified
input/file and surface, and retains the same generation fences. Native Stop and
player release still join before freeing callback/input resources. Final engine
release remains awaited under `_operations` by VideoPlaybackLifetime.DisposeAsync;
no detached cleanup task, early resource free, timeout increase or assertion change.

The old commit is a12-line reset with an MKV recovery comment but no regression test
or attached evidence. The new trace directly demonstrates this reset blocking media
transitions after media retirement. Reusing the engine is therefore a targeted
candidate, but native WebM seeking/repeat and retired-gesture assertions MUST still
pass on Intel to exclude the earlier hypothesized MKV issue. This patch does not
claim that native verification has already happened.

New regression coverage: a production-source wiring guard forbids engine disposal/
clearing in ClearPlaybackAsync (catches27526f0659) while requiring full medium release,
reused-engine creation and final disposal; a coordinator test forces final engine
join to stall, proves repeated medium transitions never enter it, then proves terminal
disposal still waits and closes admission. These are native-free ownership tests,
not an emulated native latency acceptance.

No demux/audio/statistics reinterpretation; `7092d84e6` remains excluded. Astra's
installed H264/AAC investigation is separate. No CI dispatch, no new broad GUI run.

## Local validation

- Release focused tests: 40 passed, 0 failed, 0 skipped (engine lifetime, playback
  lifetime, commands, observations, timeline and demux).
- The source-wiring regression was also run against the prior production source:
  failed as expected on `_engine.Dispose`; restored patched source: passed. No
  claim that this source guard executes VLC.
- Release DesktopSmoke build: 0 errors, 39 warnings.
- Both builds used `-m:1 -nodeReuse:false -p:UseSharedCompilation=false`.
- `git diff --check`: clean. Native macOS retest remains required.
