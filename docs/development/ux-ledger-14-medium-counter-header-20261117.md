# UX-LEDGER-14 — medium counter beside Post/Tags

## Scope

The requested header is `GallerySidePanel`'s **Post | Tags** segment row, not the gallery toolbar or the quick-tag surface's file-title row.

The existing live counter badge is detached from `QuickTagSurface.Header` and mounted in the side-panel header immediately after the segment (8 dip gap). The close button remains alone at the right edge, with at least 24 dip separation. The filename/title stays in the tag surface below.

This is a placement change, not new position state: `QuickTagSurface` still updates the same badge on navigation, same-medium resolution, marking, language changes and tagging-mode transitions. Existing localization (`tags.quick.gallery.position` / `positionMore`), marked suffixes, polite live announcements and single-counter suppression during tagging mode are retained. Standalone quick-tag popovers do not detach their counters. Releasing the tag surface clears its header slot, including switching back to Post.

At narrow panel widths the counter label can wrap to two lines within the same header row so ordinary localized counts stay readable rather than pushing the close button outside the header. Exceptionally long counts/suffixes ellipsize after two lines; the full current value remains available as a tooltip and accessible name. The counter introduces no focus stop.

No Reader media activation, Gallery context core or plugin signature repair code is changed.

## Regression coverage

- `QuickTagSurfaceTests`: actual badge reparenting, same header row/adjacent column, close-button clearance, no duplicate badge, live text/name/tooltip changes, hidden-counter behavior and detachment on release; viewer wiring contract preserves polite announcement initialization before reparenting.
- Existing `GallerySidePanelTests`, `GalleryTagPanelTests`, `GalleryTaggingChromeTests` and `GalleryMarkingTests`: segment focus/keyboard behavior, localized counters, mode suppression and marking.
- `GalleryMediaTagChecks`: rendered geometry, localization and polite live setting at panel widths 280/320/480 in English and German; screenshots `smoke-gallery-counter-header-{en,de}-{280,320,480}.png`; stale-counter removal when switching to Post. Existing smoke continues through navigation, marking, ratings and tag editing.

## Validation results

- Production build: successful, 0 errors (existing warnings only).
- Focused desktop tests: **168 passed, 0 failed, 0 skipped** across the five classes listed above, including the two new regression tests.
- Real-window gallery-media-tags smoke under private Xvfb/Openbox with `MEOWCHAN_MOTION=off`: **PASS**, no binding errors. All six English/German width cases passed geometry/localization/live-setting checks; switching to Post removed the old badge and reopening Tags created a fresh live badge. Navigation, marking/unmarking, rating/undo and tag editing checks also passed.
- Visually inspected the German 280-dip capture: `Medium 1 von 2` remains fully readable in the header beside Post/Tags, on two lines within the badge, with the close button separate. Captures are generated in `tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/` using the names above.
- `git diff --check`: clean.

Focused test filter: `FullyQualifiedName~QuickTagSurfaceTests|FullyQualifiedName~GallerySidePanelTests|FullyQualifiedName~GalleryTagPanelTests|FullyQualifiedName~GalleryTaggingChromeTests|FullyQualifiedName~GalleryMarkingTests`.

Build/test commands used `-p:UseSharedCompilation=false -p:RunAnalyzers=false -m:1 -nodeReuse:false` for the final validation to avoid contention with concurrent workers' shared compiler. Smoke selector: `MEOWCHAN_GALLERY_MEDIA_TAGS_ONLY=1`; the harness uses managed input, not native-input acceptance.

Delivery remains pending review/merge; this note does not mark the live ledger or release follow-up as merged/rechecked.
