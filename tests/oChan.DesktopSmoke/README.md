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
dotnet build tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release
python scripts/run-desktop-smoke.py --assembly tests/oChan.DesktopSmoke/bin/Release/net10.0/oChan.DesktopSmoke.dll --mode normal --output artifacts/desktop-normal
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

Use `--mode visual` for a targeted style comparison in one retained main window.
It captures empty/populated Downloads, Settings and keyboard focus in Dark/Light,
plus compact Barebones and Advanced details. The 1440×920 regular and 880×680
compact frames match the mock references. Passive rows make no provider requests;
the check also verifies rendered primary-action text contrast and retained drafts.
This optional scenario does not run the standard modal or tray-lifecycle tests.

The 16-window tray stress case requires a separate explicit invocation:

```sh
python scripts/run-desktop-smoke.py --assembly tests/oChan.DesktopSmoke/bin/Release/net10.0/oChan.DesktopSmoke.dll --mode tray-lifecycle --output artifacts/desktop-tray-stress
```

Use that stress case only on a disposable test desktop/CI. CI invokes it as a
separate step after the standard checks. Linux's no-tray Xvfb fixture cannot prove
native DBus registration behavior; that needs a disposable desktop with a tray host.
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

`OCHAN_DESKTOP_SMOKE_XVFB=1` selects X11 software rendering and installs the
no-tray fixture before Avalonia initializes. Openbox readiness is checked before
the harness starts. This environment is recorded in logs and `results.json`; it
does not validate GPU rendering or the user's window manager. Avalonia errors and
binding warnings still fail the checks. The real application has no test flags.

The harness creates and removes its own temporary `OCHAN_PROFILE`, including an isolated download path. It does not load the user's session. Harness flags belong only to this executable; the real-download case uses the desktop host construction boundary to inject an offline transport.

It starts the real application, waits for session restoration, renders passive thread and board snapshots, and verifies the final title after a burst of 1,000 updates posted from a worker onto the dispatcher. It also checks request admission/cooldown display, typed queued/active counts, retained check errors, terminal source state, board request progress, and rejection of updates after row disposal. The fixtures create no provider watches or HTTP requests. Host lifecycle and watch identity are covered separately by the desktop contract tests; this burst check does not establish end-to-end host notification coalescing. Normal `MainWindow.Close()` must finish, after which the harness reacquires the profile lease to verify shutdown released it. Avalonia binding warnings and errors fail the smoke run.

The normal run also checks numeric sorting with values 2 and 10, context targeting for two thread/board rows (including Ctrl-right-click through a hit-tested cell), clearing stale selection on blank space, and visibility of the Active column in the advanced view. Settings checks use temporary profiles and a scoped fake storage provider to cover pending/duplicate saves, close behavior, validation, save failures and retries, appearance preferences committed with settings or discarded on cancel, and folder-picker cancellation/errors/results. These checks never open a native file picker.

Presentation checks assert Simple as the initial view, persist all three view choices, verify that Barebones becomes smaller while keeping intake and view/application controls reachable, and roundtrip Dark/Light and English/German through the real settings flow. Pending URL text and a selected passive row must survive. Both a 760×500 regular window and Barebones at its 640×480 minimum, with expanded batch results, must retain URL intake and a full data row, with an inspector drawer that closes through its reachable button or Escape. Thread and board inspectors must display the selected snapshot's exact URL, assigned directory and current status; thread queue counts and errors must remain distinct from board data. These synthetic rows exercise presentation state, not live downloading across preference changes. Screenshots include compact, light, German, small-window and inspector views.

On Linux/X11, a fixture clipboard service is installed before the window is created. The harness verifies its identity before using Add, then covers explicit comma/newline input, multiline clipboard import, ordered per-entry results including invalid input, overlapping clicks, empty content and read failures. It also verifies copying the selected thread, selected board and all tracked URLs. It never reads or writes the native desktop clipboard. Other platforms explicitly skip these clipboard checks; their native clipboard behavior remains untested.

The `--locked-profile` case holds a conflicting lease before application startup and checks that a readable startup-error window appears and its Exit button closes normally. Window-only PNG renders are saved beside the smoke executable.

The `--download-session` case uses the real session host, provider parsing, queue, media storage and durable profile behind the native window. An injected in-process HTTP handler supplies fixture responses; no requests reach a live imageboard. It admits a URL through the UI, verifies downloaded media at the assigned path, closes the native window, creates a fresh host/window for the same isolated profile and verifies restoration before closing again. Its explicit application lifetime allows this restart without ending the process after the first window closes.

The `--no-tray` and `--tray-failure` cases keep native windows and rendering, but replace tray creation with a null result or an exception. With `MinimizeToTray` enabled, they invoke the native user-close callback and verify that the window remains minimized with taskbar access. They restore the window and use its visible Exit menu item to finish shutdown. Unknown or combined arguments fail with exit code 2. Only this non-packable harness opts into Avalonia's unstable windowing-platform service API; production code uses public tray APIs.

Avalonia 12 tray icons create their native implementation when attached to the application's `TrayIcons` collection. Each window owns its entry and removes/disposes it at shutdown. The `--tray-lifecycle` mode creates and closes 16 additional native windows as soon as restoration permits, checks the collection and reacquires the isolated profile lease after every close. It logs whether native tray attachment was actually exercised; the Xvfb no-tray fixture does not establish DBus registration behavior. There are no sleeps to let registration settle and no exception suppression. The other modes, including real download/restore, still run in separate processes.

Avalonia 12.1.4 includes [upstream fix #21980](https://github.com/AvaloniaUI/Avalonia/pull/21980) for late DBus tray registration after disposal. Its FreeDesktop dependency selects Tmds.DBus.Protocol 0.94.1 through the reviewed locks; the former direct 0.92.0 pin is removed. Source checks now distinguish the native collection from the no-tray fallback. These tests supplement the upstream state guards; a stress pass alone does not prove every asynchronous interleaving.

The application keeps taskbar access until a real tray activation demonstrates a route back to a hidden window. A later host loss is not reliably observable through the public API. Tray setup failures do not prevent startup. The Linux-only menu detach/dispatcher drain is retained for existing menu callbacks; it does not fix late registration by itself and is not applied to macOS.

Settings are tested as retained pages in the real shell: invalid text survives navigation/language refresh, footer commits synchronize only their affected field, failures preserve the draft, Save/Cancel stay on the page, and a pending commit/picker gates navigation. Accepted saves drain during app shutdown, late results do not update the ended editor, returned picker folders are disposed, and leases are released. Small EN/DE pages are captured with reachable actions. The compatibility dialog exercises the same editor; it is no longer the product's Settings navigation.

The harness enables application Debug logs to retain shutdown phases and caught exceptions, and records closing events, the return from normal `Close()`, and remaining owned-window state after the settings checks. On a macOS process timeout, the runner attempts a three-second native `sample` with an eight-second diagnostic limit before terminating the process. The sample is retained beside the mode log; the run still fails with exit code 124. These diagnostics do not replace the required `Closed` event, successful process exit, or profile-lease reacquisition.

This checks native desktop startup and lifetime integration on the OS and CPU recorded for each run, using synthetic input events and isolated platform-service fixtures. Windows and macOS harness results are native UI evidence for their respective runners; the [platform validation record](../../docs/platform-support.md#recorded-validation) identifies completed runs. The harness does not execute the extracted release apphost or test live HTTP services, native clipboard/file-picker integration or every tray host. Its real downloads use an in-process fixture transport. Full workflow acceptance of the final extracted packages remains a separate release check.

Rules checks save actual global/board overrides, verify field origins and disabled inheritance, preserve invalid drafts across scope/page/language/view changes, and exercise failed commits in the same window. The active scope and visible selection must agree. Compact EN/DE screenshots cover all three views. Diagnostics uses current synthetic failures and exact watch lifetimes; both pages retain the single host session.

The download-session case also captures a real provider fixture post, restarts, removes its watch, and reads retained posts plus a verified PNG from the archive. A delayed manifest-loader fixture proves local posts remain usable while media are waiting. Preview reactivation cannot block the list. Compact EN/DE readers retain at least 100 logical pixels of post viewport; dark/light screenshots supplement those assertions. These checks use sequential windows in the isolated runner, never the personal desktop.
