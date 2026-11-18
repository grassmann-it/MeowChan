# UX audit — Paket 7C plugin cards and colour editor

Date: 2026-11-18
Branch: `codex/worker-7c-plugin-cards-color`
Base: `origin/goal/gesamtprogramm-profile-a` at `866ad8ea2`
Scope: plugin-card statistics/order and the Settings colour editor only.

## Ledger comparison

The review used the accepted card contracts from `5621cbcf5` and `d194d4d9e` (uniform
`Sources / Filters / Running / Files` footer, effective-release counts, deterministic
activity/status/name ordering) and the colour-editor follow-ups through `6f8c18d9d`.
The existing colour-editor contracts are already covered by `ThemeConfiguratorTests`,
`ThemeAccentPresetTests`, `ThemeTrialSessionTests`, `ThemeFollowUpsTests`, and the
existing managed-input smoke contract; no second colour-editor behaviour was inferred
from screenshots.

## Reproducible finding and fix

`PluginActivityTracker` was keyed by the representative candidate's card key. A plugin
with multiple candidate files could therefore lose its ten-second activity hold when
selection changed from candidate A to candidate B: the same plugin received a new card
key on the next refresh, so it could move out of the running group immediately. The
candidate grouping and card-file UX intentionally allow this representative change, so
this was a source-level, reproducible ordering defect rather than a visual-only finding.

The tracker now uses the stable `PluginId` when one exists and falls back to the card key
for standalone entries. The retain operation uses the same identity. A focused unit test
covers the representative-file switch and the standalone fallback.

## Non-findings

- Card footer shape and effective-release/zero-safe values match the Ledger and remain
  unchanged.
- Card ordering remains deterministic: held activity, availability, case-insensitive
  name, ordinal name, then key.
- The colour editor's variant start, preset/preview updates, opaque hex input, contrast
  gate, live trial, Apply/Cancel/Discard behaviour and localization contracts showed no
  additional reproducible gap in the reviewed source and unit-test contracts.

## Boundary

No DesktopSmoke changes or runs, `.github/workflows`, baselines, lockfiles, generated
artifacts, or build infrastructure were changed. No unrelated package was touched.

## Verification

- `dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore --filter FullyQualifiedName~PluginCardOrderTests` — **12 passed**.
- `dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore --no-build --filter FullyQualifiedName~ThemeConfiguratorTests|FullyQualifiedName~ThemeAccentPresetTests|FullyQualifiedName~ThemeTrialSessionTests|FullyQualifiedName~ThemeFollowUpsTests` — **100 passed**.
- `git diff --check` — passed.
