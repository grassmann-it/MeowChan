# Targeted Linux native accessibility follow-up

Source: frozen `77e091b113fb5b94f68058ee5e375ae93e64def7` runtime from
`/home/tomka/Desktop/oChan/artifacts/plugin-implementation/2026-10-22/integration/final-01/normal-final-08/runtime`.
Root verified all 1166 runtime files against the original manifest before assigning this runtime.
The local run additionally records SHA256 for the smoke assembly, product assembly and core assembly in `native-targeted-01/assembly-manifest.json`.
No .NET build or product source change was made. Native C# fixture is unchanged.

## Result

Private Xvfb/Openbox, private DBus/AT-SPI session, disposable app/XDG/Orca profiles, audio/speech server disabled. Linux X11 software backend is explicitly reported by the app. Environment/tool versions and bundled Orca provenance are in `native-targeted-01/environment.json`. Native application and inspector both exited 0. No timeout was enlarged. `native-findings.json` honestly retains **36/38 passing targeted checks**, with the two first-Increase-focus failures below. This is not complete V29/native acceptance.

| Target, both EN/DE | Actual native result | Direct Orca output |
| --- | --- | --- |
| First focus request on volume Increase | AT-SPI returns true; actual focus moves to correctly named `PART_TextBox`, Increase remains unfocused | No Increase utterance; numeric entry speech instead |
| Decrease bridge focus | Correct actual button focus | Correct localized name |
| Repeated Increase focus after Decrease | Correct actual button focus | EN `00:16:19.419019`, DE `00:16:53.847485`, exact localized button names |
| Plugin category, Refresh status, Open plugin folder | All six localized native names and actual focus checks pass | Refresh/folder 4/4 exact localized utterances. Category already had focus when entering; repeated focus generated no new utterance, so category speech remains unproven |
| About update check | Correct localized name and disabled state exposed in both languages | Not focused/invoked: fixture sets `MEOWCHAN_DISABLE_UPDATE_CHECK=1` |
| About Close | Correct localized name, actual focus | Both exact localized utterances |

Increase can therefore receive real native focus and produce the correct direct Orca utterance after the ordinary numeric-control entry focus has happened. The first-focus transfer is reproduced on the current integrated source and remains open; this run does not change template/focus behavior. Previously proven numeric names/units and native Increase/Decrease value changes were not retested.

The live Orca log still contains generic ancestor utterances, including `Panel`, `VisualLayerManager`, `ContentControl`, `ContentPresenter`, and aggregated descendant text. For example, focusing EN Refresh at `00:16:02.309518` speaks a large `Panel …` ancestor before the correct `Refresh status` button name at `00:16:02.311942`. First numeric entry similarly speaks the settings ancestor text. This is concrete native/speech evidence of the remaining verbosity issue, not evidence that the focused button lacks its own accessible name. No speculative global accessibility-tree/template change is included.

## Scope and remaining limits

This run checks only the missing first/repeated Increase focus and newly added plugin overview/About update controls in EN/DE, at scale 1. It uses existing fixture checkpoints; old menu/board/favorite checkpoints are acknowledged without repeating their former acceptance matrix. The two required local fixture transitions still invoke the controlled favorite failure and clear a disposable board draft so the unchanged fixture can continue; no real board/download data is used.

The normal smoke fixture has no real PluginSession. Consequently the overview reports plugin management unavailable; no plugin cards or generated plugin settings, candidate controls, source-tool expander inputs, editor inputs, validation states or grants are exposed. Those remain unproven natively. About is examined only with updates disabled. Available-update, in-progress and update failure/status announcements remain unproven. No update request, external browser action, folder launcher or clipboard write was invoked. Refresh/folder controls were focused only. This is neither plugin-session E2E evidence nor live-source evidence.

No Windows/UIA, macOS/VoiceOver, audible audio output, physical DPI change, personal desktop or release acceptance is inferred. No personal display, DBus session, profile or audio endpoint was used.

## Evidence and reproduction

Raw application/inspector/session/Orca logs, full exposed trees, actual focus records and native event stream are preserved in `native-targeted-01/`. `speech-summary.json` takes exact utterances from the completed global log inside each recorded actual focus interval; absence remains false. `native-findings.json` keeps the two failures, not an adjusted PASS.

The existing isolated-session helper was copied from the earlier boards evidence and narrowly adapted for this external targeted client. It depends on that preserved private Orca bundle and original `run-desktop-smoke.py` / `with-isolated-window-manager.py`. It deliberately has no personal-session fallback. Python compile check passed. The run was coordinated with Root and serialized through the shared lock:

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock python3 /home/tomka/Desktop/oChan-sol61-workers/linux-a11y/artifacts/continuation/2026-10-23/linux-a11y/helpers/run-native.py --assembly /home/tomka/Desktop/oChan/artifacts/plugin-implementation/2026-10-22/integration/final-01/normal-final-08/runtime/MeowChan.DesktopSmoke.dll --output /home/tomka/Desktop/oChan-sol61-workers/linux-a11y/artifacts/continuation/2026-10-23/linux-a11y/native-targeted-01
```

The output directory must be new. The app's own 35-second checkpoint and wrapper's original outer deadlines are unchanged. This report follows V29/R10-17c and the historical V04 reports under `/home/tomka/Desktop/oChan-ui-workers/accessibility/artifacts/ui-implementation/accessibility/`.
