# C6: parallel smoke capture ownership (2026-11-17)

## Fixed finding and boundary

This implements the **shared build-output/capture-directory part of Astra's ranked finding #1**
("smoke execution is not safely parallel within one process/build output", very-high likelihood).
It does not claim that independent processes share C# statics: they do not.

`run-desktop-smoke.py:run_mode` previously launched children from the same assembly directory.
Fixtures write `smoke-*`/`v0.9.0-*` evidence to `AppContext.BaseDirectory`, and the runner
attributed PNGs using mtime changes. POSIX flock serialized cooperating runners, but the
no-fcntl path (Windows) had no exclusion. Simultaneous successful children could silently
receive each other's screenshots, and equal basenames could overwrite evidence.

Each invocation now gets a unique, **copied** assembly/dependency tree. Stages live below
the original bin so ancestor-based repository fixture discovery still works. Staging
excludes other stages, the obsolete lock file, and old captures. No hard links are used.
The child is reaped (including owned process-group cleanup) before evidence is collected;
the stage is disposed only after collection. Capture ownership comes from the private
namespace, not filesystem timestamps. `captures.json` records the scenario and files
collected from that namespace, including JSON/HTML sidecars that were previously lost.
The former capture-only POSIX lock is no longer needed. No xUnit project parallelization
settings, application assertions, deadlines, or retry policies were changed.

Scope: cooperating calls through this runner. Direct `dotnet <original assembly>` calls
and explicit caller-supplied shared output paths remain the caller's responsibility.
Each concurrent invocation requires a full copy of the runtime tree and writable bin
parent. The build must be complete before testing; concurrent rebuilding is unsupported.

## Measured before/after (parallel ENABLED)

Base: `3c89d6c92` (`origin/goal/gesamtprogramm-profile-a` when the worktree was created).
Python 3.14.7, Linux. These are **capture-ownership regression results, not Avalonia smoke
pass rates** and not estimates of Windows-native UI reliability.

| Affected test | Workers | Before | After |
| --- | --- | --- | --- |
| `ParallelCaptureIsolationTests.test_concurrent_children_own_their_captures_without_platform_lock` | 2 simultaneous real child processes per iteration | 0/10 iterations passed (0%) | 10/10 passed (100%) |

Per-iteration exit codes (iterations 1–10):

- Before: `1 1 1 1 1 1 1 1 1 1`
- After: `0 0 0 0 0 0 0 0 0 0`

The regression uses the actual `run_mode` orchestration and real subprocesses; only the
`dotnet` executable is replaced with a Python fixture. Both children write the same
basename plus an owner-specific basename next to their assembly, then rendezvous through
an external barrier before either can exit. Thus serialization cannot produce a false
green. `fcntl` is unavailable during the entire pair, exercising the old Windows contract
on Linux without changing platform-wide `os.name`. Both child smoke verdicts were green
on the broken baseline; the strict evidence-ownership assertion exposed contamination:
alpha's evidence included `smoke-beta.png`. The fixed test also checks stale-file
exclusion, nested dependency availability, untouched original bin, and stage cleanup.
There are no retries.

Additional verification:

- `python3 -m unittest discover -s tests -p 'test_*smoke*.py'`: **49 tests passed**.
- `dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -c Release --nologo`:
  **succeeded**, 0 errors / 47 existing warnings (8m56s).
- Failure-path regression: a failed child retains its JSON evidence and manifest even
  with an epoch-zero mtime; the child remains failed and its stage is cleaned up.
- Independent-copy regression: changing a staged dependency changes neither another
  simultaneous stage nor the original, and exceptions dispose both stages.

### Reproduce

Run the focused test ten times (each iteration starts two concurrent children):

```sh
for i in $(seq 1 10); do
  python3 -m unittest discover -s tests -p test_smoke_parallel_isolation.py \
    -k concurrent_children_own_their_captures_without_platform_lock
done
```

For a red comparison, apply **only the new test file** to the base above, leaving its
runner unchanged. Then use the identical command. No CLI `--no-parallel` or assembly
parallelization override is used in either measurement.

Session evidence is under `$COMMANDCODE_SCRATCHPAD/c6-evidence/`: `before/{1..10}.log`,
`after/{1..10}.log`, and `all-smoke-python.log`.

## What this does not establish

The historical `download-session` (~34.8% failures) and `normal-core` (~37.5%) statistics
are not re-labelled as fixed. Capture misattribution is independently reproduced and
fixed; it does not explain an in-process gallery build-count assertion. Astra also
identified UI/dispatcher readiness, download-session host retirement/persistence, and
global localization/environment mutations in desktop xUnit tests. Those require separate
lifecycle/state ownership fixes and actual GUI repeat gates. In particular the exact-once
gallery assertion is retained unchanged, not weakened.

An initial concurrent GUI preflight could not reach the application: both Openbox
instances reported `Failed to open the display` / `The isolated window manager exited
during startup`. After the build completed, a baseline pair did enter the application,
but both scenarios exceeded the unchanged 60-second harness deadline. The host's measured
one-minute load was **82.8 on 16 CPUs**. Further GUI stress was interrupted rather than
raising deadlines or treating infrastructure-dominated outcomes as product evidence.
These attempts are **not** the ten-iteration before/after proof above; no claim of
improved historical GUI smoke pass rates is made. Logs are retained in
`c6-evidence/gui-preflight/` and `c6-evidence/gui-before/`. Native Windows/macOS UI runs
were not available. No broad desktop-test assembly parallelization switch is re-enabled
on the strength of this filesystem-only proof.

PR: <https://github.com/grassmann-it/MeowChan/pull/32>.
