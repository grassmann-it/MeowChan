# Installed-DMG Intel H264/AAC: observation audit and next diagnostic

Base: `0c1839030`; worktree: `/tmp/ochan-intel-playback-diagnostics`.
Scope: shipped apphost playback selftest, runner, tests. No production player,
media options, fixtures, workflows, deadlines or acceptance thresholds changed.
Sol's `7092d84e6` demux candidate is **not** integrated. No CI dispatch or push.

## Original evidence

- Run `43908445672`, artifact `14247658029`, package source
  `ccb3be5e5096d9c9c6028352570d5f0fea62dfa7`.
- Log: `/tmp/ochan-mac-intel-package-43908445672.log`.
- Extracted evidence: `/tmp/ochan-intel-playback-evidence/`.
- Archive `/tmp/ochan-intel-playback-evidence.zip` SHA256
  `12b159276eb77dca3bd9258ba7ce9c09dc0622052c68bc30f0bee18206296807`
  matches the upload log.
- `playback/2-playback-h264-audio/metrics.json`: initial readiness timeout;
  Playing, time 0, duration 12000, seekable; published decoded video/audio,
  displayed pictures, played audio buffers and TimeChanged ticks all zero.
  SurfaceFrames 10, SurfaceNonUniform true. Transport/visible bounds ready.
  `result.json`: exit 1, **outer timedOut false**. The internal stage failed,
  not the 120-second process watchdog. Poster/cache succeeded.
- All four other fixtures passed. In particular, VP8/Vorbis's **first sample**
  has decoded audio 149, played buffers 74 and AudioAvailable true; its mute
  checks pass. This does not establish AAC-output health, but contradicts a
  blanket assertion that this job had no usable audio endpoint.

## Fixture/options audit

All five current default fixture sizes/hashes match their manifests. No fixture
replacement, new codec variant or re-encode is warranted before collecting logs.
The failing MP4 is H264 High/yuv420p, 160x90, 15fps, 12s, plus AAC-LC mono 48kHz.
`ffmpeg -v error -i ...playback-h264-audio.mp4 -f null -` exits 0 locally; this is
an integrity check, **not** macOS VLC acceptance.

The silent control and failing MP4 contain identical compressed video payloads:

```sh
ffmpeg -v error -i tests/Fixtures/release102/playback-h264-audio.mp4 \
  -map 0:v:0 -c:v copy -f hash -hash sha256 -
# repeat with playback-h264-silent.mp4
# SHA256=ca57e4d39b490d62e7c2f1dba4db056a3f8ada9d4bbb09d232c61010fb75f5f5
```

Their containers/interleaving differ, so this does not exonerate demux timing;
it makes a generic unsupported-H264 explanation particularly weak.

At the base, both MP4s use `:demux=avformat`; Intel uses a verified private file,
not callback imem. The silent MP4 passes that exact option combination. There is
no evidence yet that switching the demux is the right fix for the audio case.
The runner already supports repeated `--fixture` arguments. The shipped parser
accepts absolute local `.mp4`/`.webm`, non-symlink, at most 2 MiB. A custom subset
is diagnostic only; the release gate still requires all five pinned defaults.

## Exact observation semantics

The native package is `VideoLAN.LibVLC.Mac` **3.1.3.2**, containing VLC **3.0.24**,
revision `6de05adcbaf2e8b85fe86aad4169393098628119`. This agrees with
`docs/native-video-macos-source-manifest.json` and the cached x64 dylib strings.
The binding is LibVLCSharp 3.10.2, commit
`3082c09ca955131c705fc4767fde03e9c4a14999` (NuGet repository metadata).

1. `GalleryVideoPlayer.InspectPlaybackAsync` reads the owned `playback.Media`
   under the native operation gate; the base fences generation admission and
   result publication. Mapping is direct: DecodedVideo, DisplayedPictures,
   DecodedAudio, PlayedAudioBuffers. The binding's sequential 15-field, 32-bit
   int/float `MediaStats` agrees with `libvlc_media_stats_t`; no ABI mismatch was
   identified. The same observer returns positive counters for the controls.
2. [LibVLCSharp Media.cs, lines 419–423](https://github.com/videolan/libvlcsharp/blob/3082c09ca955131c705fc4767fde03e9c4a14999/src/LibVLCSharp/Shared/Media.cs#L419-L423)
   returns **default (all zeros) if `libvlc_media_get_stats` returns false**. The
   current evidence has no success bit; unavailable and valid-zero snapshots
   cannot be distinguished from this JSON alone.
3. [VLC lib/media.c, lines 695–735](https://github.com/videolan/vlc/blob/6de05adcbaf2e8b85fe86aad4169393098628119/lib/media.c#L695-L735)
   copies the input item's last statistics snapshot; it does not interrogate
   live decoder threads. [src/input/input.c, lines 657–685 and 714–765](https://github.com/videolan/vlc/blob/6de05adcbaf2e8b85fe86aad4169393098628119/src/input/input.c#L657-L765)
   publishes timing/stats in the input loop (250ms update schedule, after demux
   work). A stalled input loop can therefore leave both clock and stats stale
   while other threads still deliver pictures. **This is a mechanism, not proof
   of where this run blocked.**
4. SurfaceFrames counts visible bitmap copies from the player's vmem display
   callback, independently of the stats snapshot. NonUniform is spatial pixel
   variation; neither proves ten *distinct* decoded frames or advancing video.
   It is not the separate poster cache. Zeros must not be described as proof
   that H264/AAC never decoded, nor may these copies replace native assertions.
5. AudioTracks is VLC's `audio-es` choice count (`lib/audio.c:377–388`), not
   ffprobe's count of encoded streams or proof of selected/decoded AAC. This
   fixture has one encoded audio stream. Volume 70 alone proves no audio output.

Pinned upstream source excerpts used for the audit are in
`/tmp/ochan-intel-playback-research/`; they are not part of the change.

## Added diagnostic, not a speculative playback fix

- `scripts/apphost-playback-smoke.py` sets **VLC_VERBOSE=2 in the owned child
  environment only**, records `vlcVerbose`, and captures native stderr in the
  existing `output.log`, including failure/timeout. No CLI media option changes.
  The exact [VLC console logger](https://github.com/videolan/vlc/blob/6de05adcbaf2e8b85fe86aad4169393098628119/modules/logger/console.c#L121-L140)
  reads this variable; the shipped `libconsole_logger_plugin.dylib` contains
  that hook. This obtains real demux/decoder/audio-output module and buffering
  diagnostics without reaching into Zeno's player or adding a second decoder.
- The shipped selftest annotates poster vs gallery stages and readiness attempts
  in that log. `metrics.json.diagnostics.Readiness` retains the latest **128**
  readiness observations: sequence, Stopwatch timestamp, attempt, exact observed
  playback/UI state and the result of the **unchanged** predicate. Failed
  observations remain separate from the successful acceptance `samples`.
- `diagnostics.PlaybackTrace` reuses the base's managed operation/getter trace,
  a ring of **8192** events filtered to `video.*`, with frequency/start time and
  dropped-event accounting. Drops count the underlying ring before filtering.
  `playbackLifecycle` is the existing managed-only generation/cleanup snapshot.
  Diagnostics make no additional native calls and never acquire the player gate.
- Atomic report checkpoints occur at readiness entry and at most once per second
  after completed readiness observations, plus existing acceptance/final writes.
  Thus a later hung getter still leaves the previous complete checkpoint. This
  is **not** a background native-hang monitor: a getter that never returns cannot
  produce a new sample. Existing outer-timeout process diagnostics still apply.
- Schema 2 acceptance, strict native time/decoded/displayed progression, surface
  checks, audio/no-track/decoded-without-output proof, 10s readiness stage, 100ms
  poll interval, 120s outer default and complete five-fixture gate remain intact.

## Parent / Zeno handoff

Run the unchanged installed-app wrapper on the integrated candidate. For a
first discriminating subset (NOT full release acceptance), use the existing
arguments on a disposable Intel native desktop:

```sh
python3 scripts/apphost-playback-smoke.py /absolute/MeowChan.app/Contents/MacOS/MeowChan \
  --allow-native-desktop --evidence-dir /fresh/evidence/path \
  --fixture tests/Fixtures/release102/playback-h264-audio.mp4 \
  --fixture tests/Fixtures/release102/playback-h264-silent.mp4
```

First inspect `output.log` **after the gallery stage marker**, not poster decoder
logs: actual selected demux/codec/aout, AAC initialization errors, buffering/PCR
messages. Correlate the clock/surface trajectory with completed getter phases:

- Repeated completed reads + zero published counters + surface activity is not
  a queued observer; native log evidence must decide where input progress stops.
- A begin without end in operations-wait/native-read is a different failure;
  use the existing timeout stack evidence, not a media-option workaround.
- If logs remain inconclusive, ask Zeno to expose **native get_stats success**,
  full read/demux/error counters, selected audio-track ID and buffering events
  under existing ownership/admission. A success bit distinguishes unavailable
  from zero, but still cannot prove snapshot freshness. Do not treat zero AAC
  counters as permission to disable audio or weaken the optional-output proof.

No native root cause is proven and no macOS fix is claimed. Native CI, any
production change, and other lifecycle/input workers remain parent-owned.

## Validation

- Python runner contracts: **15 passed**, including child-only verbose logging,
  failed/outer-timeout report retention, unchanged 120s default, pinned corpus,
  native-frame strictness despite surface/diagnostic claims, and strict audio
  evidence. Log: `/tmp/ochan-intel-playback-python-tests.log`.
- Focused Release DesktopTests: **31 passed, 0 failed/skipped**, including all
  four new diagnostic tests plus ApphostStartupSmoke, VideoPlaybackObservation,
  and VideoStreamDemux tests. Command:

  ```sh
  dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Release \
    --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false \
    --filter 'FullyQualifiedName~ApphostPlaybackDiagnosticsTests|FullyQualifiedName~ApphostStartupSmokeTests|FullyQualifiedName~VideoPlaybackObservationTests|FullyQualifiedName~VideoStreamDemuxTests' --nologo
  ```

  Build/test log: `/tmp/ochan-intel-playback-diagnostics-serial-tests.log`.
  A subsequent focused `--no-build` verification also passed all 31, exit 0:
  `/tmp/ochan-intel-playback-diagnostics-focused-test-results.log` (the first
  command's shell wrapper used zsh's read-only `status` variable after testing).
  The earlier parallel-capable build invocation was stopped at the host-load
  instruction; it is not a test result.
- `git diff --check`: clean. Fixture pins and local FFmpeg integrity/elementary
  video-payload comparison as above.
- **Runtime proof pending**: no GUI run (host-load restriction), no macOS/installed
  DMG execution, no native logger runtime validation, no CI/push. Source/binding
  audit and mocked runner contracts are not native playback acceptance.
