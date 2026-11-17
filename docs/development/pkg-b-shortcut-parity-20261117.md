# PKG-B reader/gallery shortcut parity — 2026-11-17

## Outcome and interpretation

The resumed implementation is committed on `fix/reader-gallery-shortcut-parity-20261117`. The focused desktop tests and both scoped Linux desktop smoke journeys pass. This is source-built Avalonia routed-input evidence, not published-apphost or native Windows/macOS keyboard acceptance.

**H means the existing catalog command `post.hide`.** No help/history alias, Primary+H, or Alt-specific fallback was invented. If the desired product action is something other than the catalog's existing H command, that requires a separate explicit binding decision.

## Implementation

- `CommandCatalog.cs`: queue and media-tag commands participate in the reader execution context.
- `ArchivePage.MediaActivation.cs`: explicit gallery activation resolves the focused attachment, including its preview button. Only that preview opts into effective Shift+Enter; ordinary Enter and unrelated native controls retain their actions.
- `ArchivePage.MediaSidebar.cs` / `ArchivePage.PostFilter.cs`: the sidebar routes the existing post-filter command against its own active surface. Gallery activation reads the focused strip item's index before opening.
- `ArchivePage.InlineVideo.cs` / `ArchivePage.Reader.cs`: embedded reader playback routes before post-list typeahead, with the existing native-editor, text-selection and current-context guards. Embedded readers also observe KeyUp/deactivation/focus changes so activation latches cannot remain stuck.
- `ArchivePage.axaml.cs`: install the focus-decided page key-context host after the page tunnel handlers, matching Avalonia's reverse registration order.

No PKG-B edits were made to the excluded tagging/settings/QuickTag/gallery-panel/media-page implementation files. Other workers' changes in those areas entered only through target integration merges.

## Verification

At `a05afad7e` (integrating target `535b5515a`):

```text
Passed! - Failed: 0, Passed: 168, Skipped: 0, Total: 168
```

This includes 17 PKG-B tests and adjacent activation, video, command-router, key-context and shortcut-scheme tests. Windows, Linux and macOS bindings are exercised as explicit platform models: Primary is Control on Windows/Linux and Meta on macOS; explicit Ctrl remains Control on all three.

Both isolated Linux runner results have `passed: true`, `exit_code: 0`, `timed_out: false`, RenderScaling 1.0:

```text
SMOKE shortcut-parity PASS large-reader-post-focus-Q
SMOKE shortcut-parity PASS large-sidebar-medium-focus-H
SMOKE shortcut-parity PASS large-sidebar-medium-ShiftEnter
SMOKE shortcut-parity PASS large-inline-medium-ShiftEnter
SMOKE shortcut-parity PASS large-button-native-Enter
SMOKE shortcut-parity PASS large-tag-input-focus
SMOKE shortcut-parity PASS embedded-reader-post-focus-Q
SMOKE shortcut-parity PASS embedded-sidebar-medium-focus-H
SMOKE shortcut-parity PASS embedded-sidebar-medium-ShiftEnter
SMOKE shortcut-parity PASS embedded-inline-medium-ShiftEnter
SMOKE shortcut-parity PASS embedded-button-native-Enter
SMOKE shortcut-parity PASS embedded-tag-input-focus
SMOKE shortcut-parity PASS gallery-focus
SMOKE PASS: reader-ux scoped reader-media-sidebar: selected journey completed; unrelated reader/gallery journeys not claimed; no binding errors.

SMOKE embedded-video-shortcut PASS post-Space
SMOKE embedded-video-shortcut PASS post-M
SMOKE embedded-video-shortcut PASS post-L
SMOKE embedded-video-shortcut PASS inline-video-Space
SMOKE embedded-video-shortcut PASS inline-video-M
SMOKE embedded-video-shortcut PASS inline-video-L
SMOKE PASS: reader-ux scoped reader-inline-video: selected journey completed; unrelated reader/gallery journeys not claimed; no binding errors.
```

Here `M` denotes Shift+M. Native Enter is verified by exactly one actual routed Button.Click, no explicit gallery open, and retained focus. Exact-medium gallery checks target attachment position 2, not the first attachment. Video scenes toggle and restore state through keys, asserting retained focus. Independent parity/video shortcut scenes collect failures rather than skipping later scenes.

Session evidence under `$COMMANDCODE_SCRATCHPAD`:

- `pkg-b-current-target-unit.log` and `pkg-b-current-target-unit/pkg-b.trx`
- `pkg-b-current-target-sidebar/results.json` and `reader-ux/output.log`
- `pkg-b-current-target-video/results.json` and `reader-ux/output.log`
- `pkg-b-current-target-{sidebar,video}-clean.log`: NUL-normalized copies for searching the captured logs

The branch was initially rebased onto `afc35bc4e`; after its first push, later target updates were merged, not force-pushed. Target `ae3bf885c` was subsequently integrated after the above GUI measurements in merge `5f0f3ffaf`. The final affected-test rerun at that merge also passed **168/168, zero failed or skipped**, in 1 minute 59 seconds; it is recorded in `pkg-b-latest-target-unit.log`. The GUI measurements above are deliberately attributed to their actual tested commit, not relabeled as measurements of a later merge.

## Failure classification during verification

- **Product, fixed:** missing reader command contexts; sidebar H routing; the generic native-button guard blocking explicit Shift+Enter; embedded playback/latch scope.
- **Smoke, fixed without removing assertions:** a non-focusable toolbar button used as the native-Enter target; relying on a retained thread id after a list refresh while its media snapshot was empty; logical video focus in an inactive diagnostic window; pressing mute before its transport was enabled. The fixtures now explicitly load the thread, activate the diagnostic window and await transport readiness.
- **Runner/environment:** the first real-video fixture had no audio track; the private desktop had no usable audio output. Final playback uses an audio-bearing WebM and a private ALSA null sink, not the user's audio server. This verifies routing, decoder/playback state and mute intent, not audible output quality.
- **Runner/environment:** early 60-second runner termination and later saturation (load averages above 80 on 16 CPUs) interrupted otherwise independent scenes. The runner budget was sized from observed startup/scene costs; the 15-second condition assertions were not increased. Final serial runs passed.
- An early broad DesktopTests attempt was interrupted after 120 seconds with failures in toolbar/design-lint/hygiene/media-affordance areas. It was not a full-suite pass and was not used as PKG-B acceptance. Only the focused 168-test result is claimed here.

## Exact next CI action

Open a PR from `fix/reader-gallery-shortcut-parity-20261117` to `goal/gesamtprogramm-profile-a`. On the final PR merge head, run the following affected-project gate, then both smoke selectors serially. Keep results.json, TRX and logs as artifacts. Repeat on disposable Windows/macOS graphical runners with `--allow-native-desktop`; do not run these native suites on a personal desktop.

```sh
export DOTNET_PROCESSOR_COUNT=2
FILTER='FullyQualifiedName~ReaderGalleryShortcutParity|FullyQualifiedName~ReaderMediaActivation|FullyQualifiedName~ReaderVideo|FullyQualifiedName~CommandRouter|FullyQualifiedName~KeyContextHost|FullyQualifiedName~EffectiveShortcutBindings|FullyQualifiedName~GalleryShortcutScheme|FullyQualifiedName~PageKeyScheme'
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -m:1 --filter "$FILTER" --logger trx
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -m:1
ASSEMBLY=tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll
MEOWCHAN_READER_MEDIA_SIDEBAR_ONLY=1 python3 scripts/run-desktop-smoke.py --assembly "$ASSEMBLY" --mode reader-ux --timeout 240 --output "$EVIDENCE/sidebar"
MEOWCHAN_READER_INLINE_VIDEO_ONLY=1 MEOWCHAN_VIDEO_FIXTURE="$VIDEO_WITH_AUDIO" python3 scripts/run-desktop-smoke.py --assembly "$ASSEMBLY" --mode reader-ux --timeout 180 --output "$EVIDENCE/video"
```

Set `EVIDENCE` to a fresh CI artifact directory. On isolated Linux without an audio server, write `pcm.!default { type null }` to a job-local ALSA configuration and pass its path as `ALSA_CONFIG_PATH` for the video invocation. A suitable offline fixture can be generated with:

```sh
ffmpeg -f lavfi -i testsrc2=size=160x90:rate=12 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 180 -c:v libvpx -b:v 80k -c:a libvorbis "$VIDEO_WITH_AUDIO"
```

Remaining acceptance risks: native Windows/macOS player-overlay focus, native OS key delivery, and audible backend behavior. No native-platform or complete-solution green result is claimed.
