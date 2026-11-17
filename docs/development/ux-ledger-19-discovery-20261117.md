# UX-LEDGER-19 — point-of-use shortcut discovery

Initial base: `origin/goal/gesamtprogramm-profile-a` at `209caadf6`.
Merged current goal `fffa5c0e7` (PR #63 analyzer cleanup) without rewriting pushed commits.
PR: https://github.com/grassmann-it/MeowChan/pull/65.
Branch: `fix/ledger-19-point-of-use-discovery-20261117`.

## Before / after

Examples below are English Windows/Linux defaults, not strings baked into the implementation.
On macOS the same effective gestures use the catalogue's platform renderer.

| Gap | Before | After |
| --- | --- | --- |
| Seek | Position names only `,` and `.` and the configured step. | Keeps those step instructions; adds **Back 5 seconds · Alt+Left**, **Forward 5 seconds · Alt+Right**, **Back 30 seconds · Ctrl+Shift+Left**, **Forward 30 seconds · Ctrl+Shift+Right**. Tooltip and accessible help agree. |
| Volume | Volume/audio-unavailable label only; no accessible help. | Label plus **Decrease video volume · Shift+Down** and **Increase video volume · Shift+Up**, also in accessible help. |
| Context honesty | Reader/Media video hints include gestures owned by their page; archive rating caption omits its Reader context. | Player receives its host's execution context. Hints ask the same `ReaderVideoKeys.ReaderClaims` / `MediaPanelKeys.PageClaims` policies as routing. Unsupported gallery-only video commands and context-yielded defaults are omitted. Caption asks in `reader`, including its accessible help. |
| Reader activation | “Enter or a click … Space pauses …”; shortcut-window note also hard-codes Enter/Space. | Both sentences inject the effective activation and play/pause bindings. Rebinding to Alt+F8 / Alt+F9 changes the sentences; removing bindings removes those key names. Media-hosted activation uses `media.open`. |
| macOS suggestion chips | Constructed `⌘1…9` regardless of catalogue. | Each chip asks for its own `tags.quick.suggestion-N` binding. Defaults render as `⌥1…9` (Alt), unless an effective override changes them. Removed bindings leave no accelerator claim. |
| Alias cap | Tooltip discarded the third and later gestures. | Every distinct active gesture remains in binding order, including favorite tips. Tests assert the complete list, not a larger arbitrary cap. |
| Quick-tag navigation | Local field help mentions fixed Page Up/Down and only one dynamic commit gesture; marking/thread-section commands missing. | Field and surface help enumerate Next, Previous, Commit-next, Mark-next, Mark-previous and Thread-section from effective bindings. Popovers with navigation disabled do not advertise them. Native empty-field guidance is retained separately. No first/last range is invented for independently rebound suggestions. |
| Accessible chips / rating | Chips have no shortcut help; rating gestures only in visual tooltips. | Chip help mirrors its tooltip and clears obsolete accelerator metadata. Rating group retains the rating/favorite summary and adds all five rating bindings plus clear; each star also has accessible shortcut help. |

## Mechanism

- `CommandCatalog.TooltipGestures` reads `ShortcutService.Current`, retains all aliases,
  filters context yields, and calls `ShortcutHelp.ActsHere` for the real inline-video ownership policy.
- `ShortcutHelp.Commands` composes localized command labels with those effective gestures.
  It never formats a guessed default key.
- `GalleryVideoPlayer.ShortcutContext` is gallery by default; inline slots propagate Reader or
  Media from their host, including the shared transport controls.
- `QuickTagKeys.SuggestionGesture` delegates to effective bindings. Platform-model calls rebuild
  the current section for that platform, preserving genuine catalogue overrides instead of inventing one.
- Reader prose required a one-time migration of **two previously non-parameterized strings** to
  placeholders in EN/DE and all ten packs. Future gesture moves require no language-pack changes.
- Attached controls refresh when the committed shortcut snapshot changes; suggestion reuse refreshes
  tooltip, help and accelerator together.

## Ownership debt exposed by editing

`ShortcutBindingsEditor.cs` had a baselined CA1001 for its capture lease. The editor now implements
`IDisposable`, releases capture, cancels queued validation reveal and detaches its shared session.
Direct test owners dispose editors. Its one obsolete CA1001 baseline row is deleted;
no analyzer suppression or baseline addition is introduced. The landed PR #63 supplies the
shared video-watch/fixture ownership cleanup and is preserved by the merge.

## Validation

Final validation **passes with normal analyzers enabled**, after merging current goal's PR #63:

- `MeowChan.csproj` Debug build: **0 errors**, 275 advisory/compiler warnings.
- `MeowChan.DesktopTests`: **261 passed, 0 failed, 0 skipped**, 1 m 42 s.
- Normal SARIF: **0 unsuppressed CA1001/CA2000/CA2213/CA2016 diagnostics** in both projects.
- Tests include default/rebound/unbound controls, all three platform models, all twelve
  languages for activation help, context shadowing, full aliases and editor disposal.
- `git diff --check` passes.

Only the application and DesktopTests (plus their initial required reference outputs) were built;
no solution build or GUI smoke suite was run. The final commands use `BuildProjectReferences=false`
to reuse those dependencies and `UseSharedCompilation=false` to avoid the overloaded server.
No analyzer-disabling override is used in the final pass.

[Exact test output, commands, family counts and receipt hashes](evidence/ux-ledger-19-validation-20261117.txt).
Raw TRX, build logs and SARIF are under the session scratchpad's `ledger19-*` paths.

Earlier validation is not hidden: the first shared-compiler run stalled >18 minutes under load
63–75 with >16 GB swap in use and was cancelled. A functional-only pass was followed by the final
normal-analyzer pass above. Reserved-key and cross-context test fixtures were corrected without
removing assertions; binding-validity assertions were added. Before merging PR #63, application
analysis reported two unchanged Apphost Parse ownership errors; the landed goal fix removes both.

## Neighbouring defects deliberately not fixed

- **UX-LEDGER-18 remains:** `QuickTagKeys.Route` still has fixed command paths (including the old
  macOS Meta suggestion path). Old keys can continue working after rebinding. This PR changes
  discovery, not routing/collision semantics; it does not claim to close that ledger entry.
- `ReaderInlineVideoSlot` still has its native fixed Enter activation path and fixed navigation
  interception. This work does not remove native control activation or revise UX-LEDGER-16.
- A slideshow-owned repeat action still has its existing ownership/reason behavior; no playback
  policy was changed under the discovery task.

Coverage limits: managed controls, effective-binding/platform models and localized text only.
Native macOS/Windows event delivery, screen-reader speech and visual tooltip sizing are unverified.
