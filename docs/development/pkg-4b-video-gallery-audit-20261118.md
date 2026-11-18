# Package 4B — Video/Gallery audit (2026-11-18)

Scope: reader video keys, gallery shortcut parity, and fullscreen/immersive affordances. Worktree: `worker-4b`, based on `origin/goal/gesamtprogramm-profile-a`.

## Change

`EffectiveShortcutBindings.Build` previously treated every collision involving a saved binding as an editor-blocking conflict. That made a saved reader command (or a saved gallery command) block a generated video/gallery default even when the two commands had disjoint execution contexts. Runtime routing already handles these cases by yielding the default only where the saved command is active.

The conflict pass now applies the editor-only semantic conflict rule only when **both** commands have explicit saved overrides. A saved override versus a generated default remains available in its non-overlapping context and does not make the profile unsaveable. Two explicit saved bindings, including separate modal surfaces, retain the existing symmetric conflict behavior.

A regression test covers a saved reader binding on `Space`: the reader command and gallery video play/pause both remain effective, and no blocking video conflict is emitted.

## Existing affordance evidence checked

- Reader and media video routes use `ClassifyUnclaimed`, so reader/media-owned commands win before video fallback keys.
- Gallery video transport exposes fullscreen and the gallery medium double-click path raises the window fullscreen request.
- Media focus mode has separate chrome/immersive state and fullscreen feedback coverage.
- Gallery shortcut scheme tests cover changed defaults, saved-key preservation, and the reader/media video fallback parity.

No DesktopSmoke, workflow, Baseline, lockfile, or restore-infrastructure files were changed.

## Verification

Passing focused run (after the fix):

```text
ReaderVideoKeysYieldTests | GalleryShortcutSchemeTests | MediaPanelKeysTests |
MediaFocusModeTests | MediaFocusFullscreenTests | FullscreenFeedbackReplayTests
134 passed, 0 failed
```

A broader focused run also included `GalleryEntryPointAuditTests` and reported one pre-existing/stale contract failure:

```text
GalleryEntryPointAuditTests.TheSlideshowKeyThatCannotStartSaysWhy
```

The test sends plain `D`, while the current catalog/default for `gallery.slideshow-toggle` is `Shift+D`; this is outside the 4B change and was not altered here. The test is documented as a blocker rather than silently claiming a clean all-audit run.
