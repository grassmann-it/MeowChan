# PF00 offline shell/gallery measurements

This harness is source-built. It does not run a published apphost and makes no native Windows/USB claim.
Program also wires the root-owned `ShutdownOrderingChecks.cs` (direct reference), and discovers the optional
PF03/PF06 checks by reflection in their existing branches. Include the shutdown helper source in both prepared
source variants; it uses existing release APIs and its extra mode is not selected by the shell matrix.

All .NET builds and runtime checks below belong to the integration owner, serially under
`/tmp/meowchan-ui-1.0-build.lock`. The PF00 implementation worker ran only Python fixtures/static checks.

`Shared/PerformanceDiagnostics.cs` must be compiled **once, in libmeowchan**:

```xml
<Compile Include="../Shared/PerformanceDiagnostics.cs" Link="Diagnostics/PerformanceDiagnostics.cs" />
```

Do not link it in desktop/test/plugin assemblies: their existing friend access reaches the same static sink.
Root now compiles `UiResponsivenessProbe.cs` in MeowChan and removes its duplicate test links.
New smoke/Core test files are included by their normal SDK glob.
After PF02 integration, link the candidate-only adapter into DesktopSmoke to preserve its optional SQL capability:

```xml
<Compile Include="../../Shared/DiagnosticAdapters/ShellLoadConditionalStoreAdapter.cs" Link="TestSupport/ShellLoadConditionalStoreAdapter.cs" />
```

Omit that optional adapter/link from the release baseline (the interface does not exist there); the shared harness
sources remain byte-identical. This is a transparent forwarder for PF02's existing capability, not a backend fix.
Without it, the decorator hides `IConditionalWorkStateStore`, and the candidate measurements exercise its compatible
fallback instead of the intended point/conditional-write paths. The adapter wraps conditional success writes in the
same measured gate and forwards UnderFence calls without introducing a second independent resource fence.

The frozen contract is `PerformanceDiagnostics.Record(string phase, string outcome = "", long request = 0,
string? watch = null, string? asset = null, long epoch = 0, long revision = 0, long value = 0)`.
`BeginRun(runId, sourceHash, fixtureHash, capacity = 16384)` returns a disposable run with `Snapshot()`;
`NextId()` allocates monotonic IDs; `Measure(phase, request, watch, asset, epoch, revision)` is a struct scope.
A disabled scope allocates no heap object. Measure emits `begin`/`end`, with elapsed Stopwatch ticks in end.Value.
Other value units are defined by the phase. Snapshot includes Stopwatch frequency, run/source/fixture binding,
ordered bounded events, and total dropped events. Full buffers retain latest events. Contention drops a write
instead of making product work wait. Strings are bounded to 256 characters. One run can be active per process.
Scopes capture their run; late Dispose never emits into a later run. Copying an active value scope is unsupported.

The fixture generates three recognizable, original-verified gallery PNGs and 64 finite media responses before
measurement. A terminal handler rejects all unknown requests; natural downloads read 4-KiB chunks at 25 ms
intervals, then use the real queue/storage. Every final file is compared with its original fixture bytes.
No product integrity, publication or generation contract is bypassed. Synthetic unassigned gallery observations
are not a production acquisition-authority proof. Fixture byte/pacing identity is SHA256-bound in the report.

Examples (replace assembly paths and choose fresh output directories):

```sh
flock /tmp/meowchan-ui-1.0-build.lock dotnet test tests/MeowChan.Tests/MeowChan.Tests.csproj --no-restore --filter FullyQualifiedName~PerformanceDiagnosticsTests
flock /tmp/meowchan-ui-1.0-build.lock dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore --filter FullyQualifiedName~UiResponsivenessProbeTests
flock /tmp/meowchan-ui-1.0-build.lock python3 scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll --output /tmp/pf00-held1000 --mode shell-load --timeout 90
MEOWCHAN_SHELL_LOAD_SCENARIO=downloads flock /tmp/meowchan-ui-1.0-build.lock python3 scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll --output /tmp/pf00-downloads --mode shell-load --timeout 90
```

The held default is 1000 ms (`MEOWCHAN_SHELL_LOAD_HOLD_MS=250` selects the smaller control).
Natural scenarios are `paused`, `metadata`, `downloads` (`MEOWCHAN_SHELL_LOAD_SCENARIO`).
Metadata issues real explicit watch checks and asserts new metadata responses without any media request.
Paused asserts confirmed pause with zero transferred/committed bytes. Downloads asserts positive transfer AND
successful commit deltas in each cold/warm navigation window; its full finite download set then drains and verifies.
Held asserts UI answers and completed target navigation occur strictly inside the gate-held interval, not merely
before/after it. The gate release uses an independently started dedicated OS thread, never a timer/Task.Run.

`measurement.json` contains raw correlated events/pings, separate intent/accepted/render-ready navigation records,
median/p95/max/count/mean/stddev, actual commit/byte rates, resource time series (CPU, allocations, GC, memory,
OS threads/handles and ThreadPool availability/queue/completions). Missing OS metrics are -1 with sampleErrors.
A render-ready timing is not proof of physical presentation. Separately recorded rendered viewport center pixels
check target identity before/after timed work. The first timing window comes from a fresh process after initial-target
pixel control; the subsequent window is warm in that same process. No OS-cache clearing is claimed.
The sampler writes a bounded progress snapshot each second so evidence survives a hard hang.

The independent Python watchdog owns the child it starts. It observes changing UI-answer sequence numbers,
not merely a fresh producer timestamp; startup silence, invalid responses, UI silence and hard end deadline fail.
If even the heartbeat producer stops publishing, that is distinguished from UI silence while fresh producer frames continue.
It captures bounded kernel thread/wait evidence after a diagnostic trigger. It kills only its own child process group/tree.
The runner uses a private working-directory profile, so interrupted/hard-killed fixtures are also cleaned up.

For a deliberate hang control **outside primary timing**, expect nonzero runner status and watchdog reason
`ui-silence`. Confirm managed output contains the UI Thread.Sleep and probe/sampler stacks (if the platform/tool
permits attachment). Managed capture is bounded to five seconds and 256 KiB, and unavailable/failed/truncated
capture is reported explicitly. No managed attachment occurs in successful primary timing runs.

```sh
MEOWCHAN_SHELL_LOAD_WATCHDOG_STALL=1 MEOWCHAN_DOTNET_STACK=/tmp/pf-continuation/dotnet-tools/dotnet-stack flock /tmp/meowchan-ui-1.0-build.lock python3 scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll --output /tmp/pf00-watchdog-control --mode shell-load --timeout 45
```

For repeatable A/B comparison prepare two separate source copies with **byte-identical PF00 harness** and
separately built binaries. The harness discovers optional session projection by reflection so the same sources compile on release HEAD.
Legacy state controls capture GetView in the background; measured UI interactions deliberately retain legacy
IsPaused reads. `baseline.tar` from the continuation handoff already includes partial fixes and must never be
labeled as an unmodified release baseline. `--baseline-description` must state the actual provenance.
Root must retain build receipts tying each binary inventory to its source; inventory at
execution alone cannot prove which sources the compiler used. Historical baseline reports remain historical.
The driver builds nothing, snapshots uncommitted source and binary hashes, verifies them before every run,
checks fixture identity across all runs, reverses AB/BA and condition order on alternate repetitions, and launches
only serial fresh-process cells. It keeps failed characterization cells and continues the remaining matrix.
Its own nonblocking runtime lock prevents two drivers from executing together (`--lock-held` only when root already
holds that same lock). `--dry-run` writes inventory/plan without starting runtime. Full matrix: 3 repetitions ×
5 conditions × 2 variants = 30 processes, each with cold/warm windows (held has before/held/after controls).

```sh
python3 scripts/run-shell-load-matrix.py --baseline-description "release HEAD plus identical PF00 harness and workStoreFactory injection; build receipt retained separately" --baseline-root /tmp/pf00-baseline --baseline-assembly /tmp/pf00-baseline/tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll --candidate-root /tmp/pf00-candidate --candidate-assembly /tmp/pf00-candidate/tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll --output /tmp/pf00-ab --repetitions 3
python3 -m unittest tests.test_shell_load_diagnostics tests.test_desktop_smoke_runner
```

Native Windows runs need `--allow-native-desktop` on a disposable test desktop; the runner never silently uses a
personal Linux display. An external USB media/profile matrix, natural slow SQLite attribution, physical presentation
latency and actual native stack capture success still require the integration owner's platform/runtime evidence.
