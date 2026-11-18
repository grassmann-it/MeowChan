# Package E — Stage-0 Evidence Boundary

**Audit scope:** Stage-0 evidence coordination only. No product fix, test-policy change, CI change, baseline change, merge, or rebase of another worker branch is part of this package.

**Snapshot:** 2026-11-18 (Europe/Berlin)

**Audit worktree:** `/home/tomka/.worktrees/ochan-ledger/auftrag3-audit-E`

**Target branch:** `origin/goal/gesamtprogramm-profile-a`

**Target head at capture:** `abeb4c86453f669edd38d56850c4ea819a0656bd` (`Merge PR #89`)

## Boundary decision

> **PKG-E-STAGE-0:** Stage 0 records the evidence boundary and provenance contract. It is not a full interaction or runtime acceptance result.

Stage 0 may establish that the audit has named its target snapshot, scope, non-goals, and handoff requirements. It must not be reported as proof that the application behaves correctly in a live interaction run, remains responsive over time, persists state across restart, restores the intended focus, or cleans up native resources.

This document intentionally records no product behavior as green and does not substitute for the later evidence lanes.

Because this is a documentation-only boundary record, Stage 0 asserts no executable, assembly, package, or binary hash. The first runtime or interaction artifact must publish its own complete hash set before its result can be considered for acceptance.

## Explicitly not accepted by Stage 0

The following remain unverified until their own evidence is captured against the current build:

- full interaction acceptance, including pointer, keyboard, wheel/touch, resize, navigation, and end-to-end user flows;
- UI heartbeat, frame/ack timing, stall, timeout, or long-running responsiveness acceptance;
- persistence, restart, reload, recovery, or watcher/fence acceptance;
- keyboard focus, focus restoration, accessibility traversal, live-region, or screen-reader acceptance;
- native-window, native-input, media-host, disposal, shutdown, or other native-cleanup acceptance;
- platform-specific, display-backend, theme/contrast, forced-colors, or cross-platform acceptance;
- broad smoke, release, or CI acceptance beyond the exact checks reported by a later evidence package.

Absence of a Stage-0 failure is therefore not evidence that any item above passes.

## Later interaction evidence contract

Every later interaction or runtime claim must bind to the exact binary that produced it. Source provenance alone is insufficient: a source commit, branch name, or screenshot timestamp does not identify the executable under test.

The evidence record must retain, at minimum:

| Field | Requirement |
| --- | --- |
| Source commit | Full commit SHA used to produce the build. |
| Binary identity | SHA-256 for every executable or assembly whose behavior is relevant; record the exact path/name. |
| Build identity | Configuration, target framework/runtime, architecture, and relevant packaging or publish mode. |
| Execution context | OS/version, display backend or window host, locale, and any required test-environment switches. |
| Capture identity | UTC timestamp, command or scenario name, and links/paths to the raw logs, screenshots, traces, or bundles. |
| Scope result | The exact interaction, heartbeat, persistence, focus, or native-cleanup claim supported by that artifact; do not generalize beyond it. |

Evidence is stale for current acceptance when the relevant binary hash changes, even if the source commit appears equivalent. A rebuild, packaging change, runtime change, or replacement of a dependent assembly requires the affected evidence to be recaptured or explicitly revalidated with the new hash set.

## Handoff rule

The next evidence lane may close one of the deferred gates only when it:

1. starts from the current target snapshot or explicitly records the exact divergence;
2. records the complete binary hash set before the interaction begins;
3. keeps the artifacts and result scoped to the captured scenario; and
4. reports missing, stale, or unbound evidence as **unverified**, not green.

Package E owns this boundary record only. It does not add a product workaround to make a later interaction claim easier to satisfy.

## Reproducibility anchors

```sh
git fetch --prune origin
git rev-parse origin/goal/gesamtprogramm-profile-a
git status --short --branch
git diff --check
```

These commands identify and hygiene-check the source snapshot. They do not produce interaction, heartbeat, persistence, focus, or native-cleanup acceptance evidence.
