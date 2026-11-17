# C1: reader-navigation focus assignment

## Outcome and classification

**Scene defect in the historical enabled-edge focus attempt.** An enabled,
focusable navigation button is not an eligible focus target while its ancestor
`ReaderNavigationHitHost` is collapsed. This is distinct from shortcut routing.
The current rebased target already reveals the capsule before focusing its
focusable border; the historical error did not recur with that setup.

Rebased onto `409975af09e9b21c704c880bf373b81c6829ab4b` (includes PR #41 reader
routing and PR #38 review instrumentation). No product code was changed.

## Exact failure boundary

Historical commit `273d5f7b77be1e7221740fefd1bc07d78165ab41` selects an enabled
`ReaderPreviousPost`/`ReaderNextPost`, then calls `Focus()` immediately after
`WaitHiddenAsync()`. It does not reveal the overlay first. Checking enabled state
cannot fix the hidden-ancestor precondition:

- `Desktop/Views/Archive/ArchivePage.axaml`: `ReaderNavigationHitHost.IsVisible`
  binds to `ReaderNavigation.IsHitTestVisible`; the focusable
  `ReaderNavigationBar` and the edge buttons are descendants.
- `ArchivePage.Reader.cs`, `UpdateReaderNavigationShown`: idle sets opacity to
  zero and hit testing to false, collapsing that host through the binding.
- `ShowReaderNavigation` restores activity and the hit-test state; only then is
  focus assignment meaningful for this retention test.

The rebased target already contains `ShowReaderNavigation()` before border
focus. Therefore a new failing whole-scene run of that historical version is not
claimed. The added diagnostic probe repeats the old enabled-edge `Focus()` call
at the original hidden point, inside the same PR #38 focus-trace gesture as the
successful reveal/focus/retention sequence.

## Changes

Kept the coherent inherited smoke-only changes and committed/pushed them before
building or running anything:

- Report visibility, enabled state, focusability, host visibility and actual
  focus in failures. Do not change any control's focusability.
- Require actual border focus and `IsKeyboardFocusWithin`, including after the
  idle interval, not merely a successful `Focus()` return.
- Observe retention for twice the configured idle delay (3000 ms). The previous
  1400 ms observation was shorter than the existing 1500 ms delay and could pass
  on recent activity alone. This strengthens an assertion; no idle delay,
  assertion deadline, runner default or CI watchdog was raised in source.
- Reveal the capsule before secondary-tab shown-overlay geometry checks, as the
  other geometry checks already do. Media loading can outlast the idle delay.
- Wrap the focus sequence with `ReviewArtifacts.GestureAsync`, retaining
  `(control, accessible_name, scope)` focus changes and failure evidence.

## Verification

Only `tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj` was built, Release,
`--no-restore`; build succeeded. No solution build or full smoke suite ran.

Command (the 600-second watchdog is the existing registry CI value in
`.github/workflows/desktop-smoke-registry.yml`, not a changed test timeout):

```sh
python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll \
  --registry --only reader-navigation --review-artifacts --timeout 600 \
  --output "$COMMANDCODE_SCRATCHPAD/c1-reproduction"
```

At `29062675651ec9e797f76ec10c205c2c97d6b6d4`, registry result: **1 passed,
0 failed, 0 skipped**, exit 0, 114 seconds, `timed_out=false`. Exact selector:
`reader-ux+MEOWCHAN_READER_NAVIGATION_ONLY`.

PR #38's `0040-reader-navigation-focus-retention/timing.json` records:

| Process-relative time (ms) | Control | Accessible name | Scope |
| --- | --- | --- | --- |
| 56770.1389 | ReaderPostItem | null | MainWindow |
| 56773.4209 | ReaderNavigationBar | Reader navigation | MainWindow |

Gesture completed at 59792.3722 ms with `failure=null`, no heartbeat stalls,
and no intervening focus transfer. This directly verifies focus assignment and
retention beyond the unchanged 1500 ms idle delay. Auto-hide, counters, modes,
geometry and selected-scene completion all passed.

An initial invocation accidentally used the runner's generic 60-second default;
it exited 124 during preceding reader setup, before this focus check. That is
not evidence of a focus defect. The subsequent invocation used the unchanged
600-second registry CI watchdog shown above.

## Final diagnostic rerun

At `8f24592ce2d47c656feeb6530d579c089826d1ea`, the same single-scene command
(output directory `c1-final`) again passed: **1 passed, 0 failed, 0 skipped**,
exit 0, 144 seconds, `timed_out=false`. Smoke-only rebuild: 0 errors,
47 existing warnings.

Exact historical failure reproduced at the hidden focus call:

```text
SMOKE reader navigation hidden focus probe: target=ReaderNextPost visible=False enabled=True focusable=True assigned=False; target=ReaderNavigationBar visible=False enabled=True focusable=True hostVisible=False focused=False within=False; large=True;mode=Auto;active=False;pointerOver=False;visible=True;focus=False
SMOKE reader navigation focus retained beyond idle delay: target=ReaderNavigationBar visible=True enabled=True focusable=True hostVisible=True focused=True within=True; large=True;mode=Auto;active=False;pointerOver=False;visible=True;focus=True
```

The final PR #38 trace starts on `ReaderPostItem` at 71987.9581 ms, records no
focus transfer from the rejected hidden-edge call, then transfers to
`ReaderNavigationBar` / `Reader navigation` / `MainWindow` at 71990.1181 ms.
Completion is at 75007.3688 ms with no gesture failure. The final state has
`active=False` but `focus=True`: retention is genuinely due to focus, not
unexpired recent activity.

Durable, unmodified runner evidence is checked in alongside this document under
`C1-reader-navigation-evidence/` (`registry.json` and `focus-timing.json`). Large
screenshots and full runtime logs remain session artifacts, not project files.

## Remaining boundary

This is focus **retention on a revealed overlay**, not proof that Tab can discover
a hidden capsule. The product comment saying hidden buttons remain reachable
conflicts with the collapsed ancestor in XAML; deciding that keyboard-discovery
contract belongs with overlay ownership (PR #36), not a silent smoke workaround.
No assertion was removed or weakened. No force-push was used. CI still needs to
run the branch; no cross-platform or full-suite result is claimed.
