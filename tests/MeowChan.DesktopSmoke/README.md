# Desktop smoke harness

Linux GUI checks run in a private Xvfb display, Openbox window manager and DBus
session. Install `xvfb`, `xauth`, `openbox`, `x11-utils`, `dbus-daemon` and
`libdbus-1-3` on Ubuntu. A missing tool fails the run without starting a process;
there is no fallback to your desktop. Existing display, Wayland, authentication,
DBus and accessibility endpoints are removed from the child environment, and
runtime/config/cache/data directories are private to that scenario. The private bus
has no service directories, so it cannot auto-start host portals/accessibility or
user-systemd services. Missing session/accessibility services may be logged as
warnings; these integrations are outside this environment's acceptance.

```sh
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -c Release
python scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll --mode normal --output artifacts/desktop-normal
```

Omit `--mode` to select the five standard scenarios, or repeat it for a targeted
serial sequence. The runner stops at the first failure and records later scenarios
as skipped. Every run requires a fresh output directory to retain earlier failures
and avoid mixing screenshots. `--timeout` bounds each subprocess; cleanup targets
only the POSIX process group created by that run (including lingering descendants),
or the live Windows process tree. Interrupts also clean up the current process.
Cleanup failures fail the run and preserve skip/error evidence. This is process
group ownership, not a security sandbox: independently detached descendants and
Windows children surviving an already exited parent need stronger OS containment.
These guarantees have window-free fixtures in `tests/test_desktop_smoke_runner.py`.

Use `--mode board-admission` for the detached multi-board admission dialog. It
checks local text-rule clearing, preserved exceptions/reactivations, joint apply
after all previews and rules are valid, retry without lost drafts, empty catalogs,
and Cancel/Escape without committing the future-direct setting. Offline fixtures
also capture the 640×600 minimum dialog in EN/DE and Light/Dark from all three
workspace views, before and after keyboard activation of the local action. The
minimum dialog's controls may scroll; commit and cancel stay visible. Repeat with
`--scale 1`, `1.5` and `2` in fresh output directories. These checks validate
Avalonia focus and accessibility properties, not native AT-SPI/Orca behavior.

Use `--mode visual` for the release showcase in one retained main window:

```sh
python scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll --mode visual --scale 1 --output artifacts/r09-showcase
```

This seeds an isolated offline profile with original demo artwork and synthetic
posts, multiple matching result pages and manifest-recorded media. The real
Downloads, gallery with post context, archive search/media tiles, repeated HTML
export and About window produce `v0.9.0-{downloads,gallery,archive,export,about}.png`
under the scenario's screenshot evidence directory. Downloads states are passive
projections; no provider watches or HTTP requests run. Gallery decoding, local
archive reads and export services use the seeded files. Standard comparison
captures remain part of the normal harness checks.

The showcase uses Simple/Dark/English and a 1440×920 main window; the export
scene uses the real compact dialog layout and About its own window. Keep the
runner log and `results.json` with the source commit/WIP description, actual size,
RenderScaling, OS and artwork provenance when promoting captures to
`docs/screenshots/`. Use a fresh output directory and inspect all five PNGs before
calling them final. These are real source-build UI captures; they do not establish
native extracted-package acceptance or live source availability. This optional
scenario does not run the standard modal or tray-lifecycle tests.

The 16-window tray stress case requires a separate explicit invocation:

```sh
python scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll --mode tray-lifecycle --output artifacts/desktop-tray-stress
```

Use that stress case only on a disposable test desktop/CI. CI invokes it as a
separate step after the standard checks. Linux's no-tray Xvfb fixture cannot prove
native DBus registration behavior; use the separate private `tray-host` fixture below.
On Linux, `--native-display` requires exactly one explicitly selected `--mode` and
also requires `--allow-native-desktop` and is intended for such a dedicated
desktop. Do not run native suites or stress tests
on an active personal workstation. Windows/macOS use the runner's native graphical
session and require both a dedicated test desktop and `--allow-native-desktop`.
That flag is an explicit opt-in, not proof of isolation; evidence says so. The CI
workflow supplies it on disposable runners. Linux still uses Xvfb unless
`--native-display` is explicitly selected.

Add `--scale 1`, `--scale 1.5` or `--scale 2` for display-scale checks. The isolated
screen is 3200×2000 physical pixels, large enough for the regular minimum at 200%.
The harness records actual window `RenderScaling`, client/frame size and working
area; a missing or different measurement fails any run with an explicit scale request.
Captures flush layout and use the observed density, excluding native decorations.

`MEOWCHAN_DESKTOP_SMOKE_XVFB=1` selects X11 software rendering and installs the
no-tray fixture before Avalonia initializes, except for `tray-host`, which retains the native DBus implementation. Openbox readiness is checked before
the harness starts. This environment is recorded in logs and `results.json`; it
does not validate GPU rendering or the user's window manager. Avalonia errors and
binding warnings still fail the checks. The real application has no test flags.

The harness creates and removes its own temporary `MEOWCHAN_PROFILE`, including an isolated download path. It does not load the user's session. Harness flags belong only to this executable; the real-download case uses the desktop host construction boundary to inject an offline transport.

It starts the real application, waits for session restoration, renders passive thread and board snapshots, and verifies the final title after a burst of 1,000 updates posted from a worker onto the dispatcher. It also checks request admission/cooldown display, typed queued/active counts, retained check errors, terminal source state, board request progress, and rejection of updates after row disposal. The fixtures create no provider watches or HTTP requests. Host lifecycle and watch identity are covered separately by the desktop contract tests; this burst check does not establish end-to-end host notification coalescing. Normal `MainWindow.Close()` must finish, after which the harness reacquires the profile lease to verify shutdown released it. Avalonia binding warnings and errors fail the smoke run.

The normal run also checks numeric sorting with values 2 and 10, context targeting for two thread/board rows (including Ctrl-right-click through a hit-tested cell), clearing stale selection on blank space, and visibility of the Active column in the advanced view. Settings checks use temporary profiles and a scoped fake storage provider to cover pending/duplicate saves, close behavior, validation, save failures and retries, appearance preferences committed with settings or discarded on cancel, and folder-picker cancellation/errors/results. These checks never open a native file picker.

Presentation checks assert Simple as the initial view, persist all three view choices, verify that Barebones becomes smaller while keeping intake and view/application controls reachable, and roundtrip Dark/Light and English/German through the real settings flow. Pending URL text and a selected passive row must survive. Both a 760×500 regular window and Barebones at its 640×480 minimum, with expanded batch results, must retain URL intake and a full data row, with an inspector drawer that closes through its reachable button or Escape. Thread and board inspectors must display the selected snapshot's exact URL, assigned directory and current status; thread queue counts and errors must remain distinct from board data. These synthetic rows exercise presentation state, not live downloading across preference changes. Screenshots include compact, light, German, small-window and inspector views.

On Linux/X11, a fixture clipboard service is installed before the window is created. The harness verifies its identity before using Add, then covers explicit comma/newline input, multiline clipboard import, ordered per-entry results including invalid input, overlapping clicks, empty content and read failures. It also verifies copying the selected thread, selected board and all tracked URLs. It never reads or writes the native desktop clipboard. Other platforms explicitly skip these clipboard checks; their native clipboard behavior remains untested.

The `--locked-profile` case holds a conflicting lease before application startup and checks that a readable startup-error window appears and its Exit button closes normally. Window-only PNG renders are saved beside the smoke executable.

The `--download-session` case uses the real session host, provider parsing, queue, media storage and durable profile behind the native window. An injected in-process HTTP handler supplies fixture responses; no requests reach a live imageboard. It admits a URL through the UI, verifies downloaded media at the assigned path, closes the native window, creates a fresh host/window for the same isolated profile and verifies restoration before closing again. Its explicit application lifetime allows this restart without ending the process after the first window closes.

The `--no-tray` and `--tray-failure` cases keep native windows and rendering, but replace tray creation with a null result or an exception. With both `CloseToTray` and `MinimizeToTray` enabled, they exercise the minimize caption, native user-close callback and close caption and verify that the window remains minimized with taskbar access. They restore the window and use its visible Exit menu item to finish shutdown. Unknown or combined arguments fail with exit code 2. Only this non-packable harness opts into Avalonia's unstable windowing-platform service API; production code uses public tray APIs.

Avalonia 12 tray icons create their native implementation when attached to the application's `TrayIcons` collection. Each window owns its entry and removes/disposes it at shutdown. The `--tray-lifecycle` mode creates and closes 16 additional native windows as soon as restoration permits, checks the collection and reacquires the isolated profile lease after every close. It logs whether native tray attachment was actually exercised; the Xvfb no-tray fixture does not establish DBus registration behavior. There are no sleeps to let registration settle and no exception suppression. The other modes, including real download/restore, still run in separate processes.

Avalonia 12.1.4 includes [upstream fix #21980](https://github.com/AvaloniaUI/Avalonia/pull/21980) for late DBus tray registration after disposal. The application directly references Tmds.DBus.Protocol 0.94.1, matching Avalonia FreeDesktop and the reviewed locks; the former 0.92.0 pin remains removed. Source checks now distinguish the native collection from the no-tray fallback. These tests supplement the upstream state guards; a stress pass alone does not prove every asynchronous interleaving.

The application checks Linux StatusNotifier host registration and its own registered icon before hiding. Requested icon visibility and a native menu exporter alone are insufficient. A bounded host check runs while hidden; losing the host or watcher restores taskbar access. Tray setup failures do not prevent startup. The Linux-only menu detach/dispatcher drain is retained for existing menu callbacks; it does not fix late registration by itself and is not applied to macOS.

Settings are tested as retained pages in the real shell: invalid text survives navigation/language refresh, footer commits synchronize only their affected field, failures preserve the draft, Save/Cancel stay on the page, and a pending commit/picker gates navigation. Accepted saves drain during app shutdown, late results do not update the ended editor, returned picker folders are disposed, and leases are released. Small EN/DE pages are captured with reachable actions. The compatibility dialog exercises the same editor; it is no longer the product's Settings navigation.

The harness enables application Debug logs to retain shutdown phases and caught exceptions, and records closing events, the return from normal `Close()`, and remaining owned-window state after the settings checks. On a macOS process timeout, the runner attempts a three-second native `sample` with an eight-second diagnostic limit before terminating the process. The sample is retained beside the mode log; the run still fails with exit code 124. These diagnostics do not replace the required `Closed` event, successful process exit, or profile-lease reacquisition.

This checks native desktop startup and lifetime integration on the OS and CPU recorded for each run, using synthetic input events and isolated platform-service fixtures. Windows and macOS harness results are native UI evidence for their respective runners; the [platform validation record](../../docs/platform-support.md#recorded-validation) identifies completed runs. The harness does not execute the extracted release apphost or test live HTTP services, native clipboard/file-picker integration or every tray host. Its real downloads use an in-process fixture transport. Full workflow acceptance of the final extracted packages remains a separate release check.

Rules checks save actual global/board overrides, verify field origins and disabled inheritance, preserve invalid drafts across scope/page/language/view changes, and exercise failed commits in the same window. The active scope and visible selection must agree. Compact EN/DE screenshots cover all three views. Diagnostics uses current synthetic failures and exact watch lifetimes; both pages retain the single host session.

The download-session case also captures a real provider fixture post, restarts, removes its watch, and reads retained posts plus a verified PNG from the archive. A delayed manifest-loader fixture proves local posts remain usable while media are waiting. Preview reactivation cannot block the list. Compact EN/DE readers retain at least 100 logical pixels of post viewport; dark/light screenshots supplement those assertions. These checks use sequential windows in the isolated runner, never the personal desktop.

The download-session case additionally exercises local archive search and filters,
quote navigation onto another thread's second post page, backlinks and reading
history. Its HTML workflow exports a synthetic retained reply together with the
real fixture PNG through the shared library, verifies actual output counts, rejects
repeated output without overwriting and moves the completed folder.
Set `MEOWCHAN_SMOKE_EXPORT_EVIDENCE` to a fresh existing output parent to retain that
relocated folder; otherwise the fixture removes its export after checking it.

For actual browser acceptance, install Python Playwright in a test environment and
use an installed Chromium executable (no browser download is needed):

```sh
python scripts/check-html-export-offline.py --directory 'artifacts/export-evidence/relocated thread' --output artifacts/export-browser --chromium /usr/bin/chromium
```

This opens the relocated `file://` export in a fresh headless context with network
access disabled. It checks decoded copied images, own CSS, real internal quote
anchors, retained posts, missing/external target explanations and absence of active
source content. Requests must remain within the exported folder. JSON evidence and
a full-page screenshot are written to the chosen output directory.

### Private Linux tray host fixture

```sh
python scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll --mode tray-host --output artifacts/desktop-tray-host
```

This Linux-only mode requires `/usr/bin/python3` with `dbus` and `gi` modules
(`python3-dbus python3-gi` on Ubuntu), plus the runner's normal Xvfb/Openbox tools.
The runner launches `with-fake-tray-host.py` inside its private session bus. It
keeps Avalonia's actual native DBus tray implementation enabled; the fixture
implements the watcher/host protocol and invokes the exported item's Activate
method. Production never starts or depends on this script.

Checks cover independent Save/Cancel preferences, disabled minimize with a host,
first minimize and close without a prior tray interaction, removal from the
taskbar, native DBus activation restoring the window, missing host fallback,
host loss, watcher restart/re-registration and watcher loss while hidden. This
proves these protocol paths, while final native shell presentation on KDE,
GNOME extensions, Windows and macOS remains a separate package acceptance check.


`--reading-position` checks V27 and Settings V04/V32. The isolated runner supports
`--mode reading-position`. For an actual two-process restart, run it twice with
`MEOWCHAN_READING_TEST_PROFILE` pointing at the same fresh disposable path. The
harness marks and retains that path, refuses unmarked existing directories, and
the second process verifies normal startup followed by explicit reader resume.
The first process scrolls through the actual reader wheel-input route and records
the first visible post, selected post and a required nonzero viewport offset.
After the second process invokes the resume menu action, it compares the rendered
post and offset with that independent record (the same <2 DIP tolerance as V18),
also checking the persisted anchor and unchanged startup page. Logs include both
process IDs, scale, client size and the measured offset; screenshots show before
close, startup before resume and the resumed reader. Use a fresh profile for each
two-process pair; an older retained fixture lacks the anchor expectation file.
Remove the retained test profile after both runs. Without that variable the test
uses its usual temporary profile and removes it after the run.

The targeted plugin UX review uses the existing `--mode plugins` entry with
`MEOWCHAN_PLUGIN_UX_REVIEW=1`. It selects exactly one initial reference cell:
English/Dark/Advanced at an observed 1280×800 DIP. It checks the original purpose
and ordinary action in the first detail viewport, real developer disclosure,
full selectable metadata, invalid source draft/error focus, settings-only pending
configuration plus a retained stale draft, menu intent and a different-row
right-click, and undescribed/failed/missing candidates. The fixture uses the real
profile store with passive catalog/activation data; any compiler, worker or site
call fails. It does not replace the real compiler/worker/probe `--mode plugins`
workflow, which also checks a foreign-row menu while a real probe is busy.

After the reference images have been reviewed and fixes integrated,
`MEOWCHAN_PLUGIN_UX_PRESENTATION=1` selects only 12 short EN/DE × Dark/Light ×
Barebones/Simple/Advanced minimum-size cells (640×480 or 760×500 DIP), without
repeating compiler, probe or semantic tasks per cell. Run one requested scale
at a time with `--scale 1`, `1.5` or `2` in fresh output directories; the runner's
actual scale check still applies. The selectors are harness-only. Use only one
of them, and retain source/WIP/fixture bindings in the run report.

`PLUGIN_UX` records actual geometry, selection, focus and expected/observed tasks.
`smoke-pux-overview-*.png` depicts the ordinary first viewport; selected
`smoke-pux-menu-*.png` images render the actual open ContextMenu visual separately
because a parent-window bitmap does not include a separate popup. Input is
hit-tested in-process routed pointer input or routed key/context requests, with
explicit focus setup. This is not native OS Tab/Shift+F10, UIA/VoiceOver/Orca or
human discovery proof. The technical copy button is checked only through the
existing identity-verified fixture clipboard; without that fixture it is skipped
before touching a native clipboard. Record actual image review as retain/adjust/change;
technical assertions alone do not decide whether the hierarchy is understandable.
