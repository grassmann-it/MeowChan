# Closing-root native pointer regression — 2026-11-16

Base: `d69afeb08`. Failure evidence: ARM run `43928857940`, job
`133515872042`, `/tmp/ochan-arm-native1.zip`, member
`artifacts/closure-gallery-errors/normal/output.log`.

The first three context/root combinations passed. The external-root + post-overlay
combination timed out waiting for `footer.IsPointerOver && pointerRoot.IsPointerOver`.
The artifact reports macOS 15.8.2 ARM64, one 1920×1080 display, scale 1 and a
1920×982 working area starting at (0,25). It does **not** record the failing
window's position, active state or pointer pixel; it cannot prove off-screen
placement versus a missing native tracking transition.

The old path accepted layout readiness and a local hit-test fallback, posted one
motion, and waited. It neither established the actual root's activation nor
ensured a changed cursor coordinate after recreating the root. The fix explicitly
centers these private windows, waits for the selected root to be active/arranged,
checks the actual top-level hit and working-area containment, seeds the cursor at
the target center, then requires real routed native motion at two distinct points.
Handlers only observe input: no RaiseEvent, synthetic pointer, activation click,
relaxed footer/root predicate or increased existing deadline. New log lines expose
position, scale, target pixel, working area, active state and actual root hit.

## Focused local checks

Disposable verification directory: `/tmp/ochan-gallery-pointer-check`.
No application/plugin build, full suite, workflow edit, CI run or push.

- `dotnet test ProbeTests.csproj`: **9 passed**. Standalone project links the exact
  committed geometry helper and xUnit test file, with Avalonia 12.1.4. Covers
  narrow/normal/wide targets, interior points, non-overlapping acknowledgement
  tolerances, unrealized/non-finite geometry, and both coordinate axes.
- `dotnet build compile/ChangedPath.csproj`: **0 warnings, 0 errors**. Compiles the
  exact two changed context/entry methods and native helper against existing local
  application assemblies; small sibling-method shims replace unrelated checks.
  This is a focused compile, not a clean application build. An earlier attempt to
  compile all smoke sources against those old binaries encountered unrelated
  missing playback lifecycle APIs and was not used as a passing result.
- `dotnet build native/Native.csproj`: **0 warnings, 0 errors**.
- `MEOWCHAN_DESKTOP_SMOKE_XVFB=1 xvfb-run -a -s '-screen 0 1920x1080x24' dotnet native/bin/Debug/net10.0/Native.dll`:
  **4/4 passed**, real XTest input. The disposable minimal Avalonia runner embeds
  the exact `EnterClosingRootAsync` method and links the unchanged real
  `MediaNativeInput` plus committed geometry helper. It recreates a window four
  times at the same position and asserts pointer-over after both native motion
  acknowledgements. All four report position (635,290), seed/final pixel (960,760),
  entry pixel (952,760), scale 1, active root and unobstructed Border hit.
  The labels exercise repeated creation, **not** the actual gallery layout matrix.

Raw logs: `tests.log`, `changed-path-build.log`, `native-build.log`, `native.log`
in that disposable directory.

## Residual verification

Linux validates real input delivery and same-coordinate root recreation, **not**
NSWindow/CGEvent behavior or the actual gallery's four layout combinations. Rerun
`closure-gallery-errors` on the authorized disposable ARM macOS desktop (and Intel
when available). Require all four existing gallery PASS lines, unchanged terminal
pointer/timer retirement assertions, and normal stage completion. The readiness
logs must show active, unobstructed, on-screen coordinates. No macOS pass is claimed.
