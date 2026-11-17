# O2-04: large-reader hosting exception safety

Base: `origin/goal/gesamtprogramm-profile-a` at `409975af09e9b21c704c880bf373b81c6829ab4b`.

## Failure map (before this change)

Avalonia child collection operations, property writes/bindings, event registration and
layout/render callbacks can throw, including after partially publishing a mutation.
Plain field assignments/increments do not throw; they are included below because they
explain mismatched state at the next failure. An allocation can also fail before a
mutation. Fatal failures are never converted into success by the error reporter.

| Step / fault boundary | State an exception could previously leave |
| --- | --- |
| Shell: capture origin, set close target | Pane still belongs to `ArchiveSplit`; shell origin/close-label metadata may already have changed. |
| `detach.01`–`04`: retire inline playback, invalidate search/focus, resolve pane | Pane still embedded; media/search/focus work may have been invalidated. These cancellations are intentionally not undone. Playback release returns a task, not a new host. |
| `detach.05`–`06`: remove pane, resolve export dialog | Pane has **no parent**; shell still thinks the large reader is closed. |
| `detach.07`: remove export dialog | Both pane and export dialog have **no parent**. |
| `detach.08`–`12`: add export to pane, set page-large flag, dispose/install Tab override, set large layout | Export belongs to an unhosted pane; page may say large while shell says closed; Tab/layout state may be partially applied. |
| `wire.01`–`06`, `detach.13`: add reader handlers / key context | Same unhosted pane, with a prefix of reader-only handlers or a live context override. |
| `detach.14`–`19`: margin, close/read buttons, navigation, layout, render thread | Same ownership gap; large-reader visuals can already be published. Render/layout can call arbitrary control/binding code. |
| `wrap.01`, `sidebar.created/configured`, `sidebar.01`–`05`: create frame/sidebar, hide previous sidebar | Pane still unhosted; sidebar creation could cache an incomplete sidebar. Previous sidebar may already be hidden. |
| `sidebar.06`–`08`: remove splitter/sidebar, collapse old columns | Splitter, then sidebar, can lose their parent; `_mediaSidebarHost` still names the old host. |
| `sidebar.09`–`15`: change visibility/rows/insets/border | Sidebar/splitter remain unhosted, with some new-host styling already applied. |
| `sidebar.16`–`19`, `wrap.02`–`03`: add sidebar/splitter, publish sidebar host, save pane column | Sidebar/splitter progressively belong to the new, itself unhosted frame; pane still has no parent. |
| `wrap.04`–`07`, `detach.wrapped`: add pane, layout sidebar/Tab scope, release unused picture | Pane/export/sidebar belong to a frame that **does not yet belong to the window**. Shell's assignment has not run because its right-hand side has not returned. |
| `shell.content`: assign frame to `ReaderHost` | Setter may fail before or after adoption. Shell open flag is still false; frame may be orphaned or hidden in the host. |
| `shell.visible`, `shell.presentation`: show host, set shell-open flag, refresh resume presentation | Window can show the reader although flags/presentation disagree. |
| `shell.scope`: apply frozen scope / start scope freeze | Ownership is complete, but a later setup failure formerly left a partially opened reader. Async freeze failures are separate from the synchronous transfer. |

## Repair contract

`DetachReader` captures its rollback before the first mutation and repairs even when
it never reached wrapping. `HostLargeReader` extends the transaction through the
shell's content assignment, visibility, presentation and scope setup. It does not
try to repair an incomplete detach by calling the success-only `AttachReader` path.

Repair restores pane/export ownership first, then sidebar/splitter ownership, flags,
Tab scopes, pane-only event handlers, margins/buttons, scope generation and layout.
Each repair is attempted even if another throws; secondary errors are aggregated
with the original, not swallowed. A completed sidebar is published only after its
construction/configuration succeeds, so a retry cannot reuse a partially built one.

The shell clears its open/origin state and close label, then reports through
`UnhandledErrors.TryRecover(error, "reader.open.hosting")`. Refused recovery (tests,
fail-hard smoke policy, out-of-memory) propagates the original exception, or the
aggregate when repair itself failed. Async scope startup is observed via
`OpenActions.Start`; existing scope-query feedback remains unchanged.

Callers that would otherwise hide a bookmark manager, enter a group scope or suspend
the gallery now check that opening actually succeeded. Other list/download callers
may continue loading the requested thread into the restored embedded reader: this
is a usable, reported fallback, not a silent unhosted control.

## Call-site audit

Direct shell callers, using original base line numbers:

- `MainWindow.PostBookmarks.cs:112`: return false on failed opening, keep manager usable.
- `MainWindow.ReaderGroups.cs:81`: do not start group navigation after failed opening; return to manager.
- `MainWindow.Gallery.cs:488`: do not change reader scope / suspend gallery after failed opening.
  Its transitive callers are empty-gallery Read posts, Read post, Read quote and Follow post.
- `MainWindow.Selection.cs:515` and `MainWindow.Menus.cs:122`: `OpenReaderOnThreadAsync` may load inline after recovery.
- `MainWindow.MediaReturn.cs:89`: already checks `_largeReaderOpen` immediately afterward.
- `MainWindow.Navigation.cs:195`: installs `ArchivePage.OpenReaderRequested`.
  Its callers are `ArchivePage.Selection.cs`, `ArchivePage.Commands.cs`,
  `ArchivePage.Gallery.cs` (list/table double click, menu and Read posts), and
  `ArchivePage.Resume.cs` (resume can restore its anchor inline).
- `MainWindow.Reader.cs:18`: convenience overload delegates to the guarded implementation.

## Similar sites, audited but not independently fixed

- `MainWindow.Reader.cs`, `CloseLargeReader` and `ArchivePage.Reader.cs`, `AttachReader`:
  inverse transfer clears shell content before unwrap/reattach; these remain a separate
  close-direction exception-safety follow-up. This change tests the normal close after a repaired open.
- `ArchivePage.MediaSidebar.cs`, `MoveMediaSidebarTo` / `UnwrapLargeReader`:
  sidebar/splitter detach/style/add also occurs during ordinary sidebar layout and
  closing. The new rollback covers it **when called inside opening**, not independent
  preference/resize/close transfers. No general sidebar-transfer rewrite was made.
- Gallery context panel: `GalleryViewer.Overlay.cs:437`, `LayoutContextPlacement`,
  calls `MoveOverlayControl(_contextPanel, overlay ? OverlayHost : _body)`;
  `MoveOverlayControlAt` delegates to `GalleryControlTransfer.Move` at line 409.
  This is the same detach/attach exception-risk class, via the transfer below.
- `GalleryControlTransfer.cs`, `Transfer.Execute`: removes a control, optionally drains
  source layouts, then adds it to a destination. `finally` restores transaction counters,
  but a drain/attach/callback exception does not restore the original parent.
- `GalleryViewer.WorkView.cs`, `PlaceToolOnBar` / `PlaceToolInMenu`: removes tools before
  restyling/insert and changes `OnBar` before insertion. Similar risk for gallery chrome.
- `GalleryVideoPlayer.Inline.cs`, `GalleryVideoPlayer.Transport.cs`: transport/volume
  controls are removed before another placement. Native transport lifecycle is not changed here.
- `MainWindow.Gallery.cs`, `StopAndDetachGalleryAsync`: retirement rather than rehosting;
  deliberately awaits native Stop **before** clearing content, unlike the defective open path.

## Deterministic coverage and validation boundary

`LargeReaderHostingSafetyTests` enumerates every production fault checkpoint and
injects the original exception after each mutation boundary (including partial
handler wiring and each individual sidebar remove/add). It asserts exact single
logical ownership, original parent/column/margin, restored Tab value source, hidden
empty shell host, restored page state, surviving workspace action routing and a
successful subsequent open/normal close. The fixture contains a captured thread,
so render/layout are exercised, not just an empty pane. Cold-sidebar checkpoints
exercise initialization failure/retry. A real `Content` property-change failure
also tests an exception **inside** a host setter, rather than only afterward.
Secondary-repair errors must retain both errors and must not prevent later repairs.

These are deterministic desktop component tests using the real page and shell-host
transaction, not a native `MainWindow` startup or GUI smoke suite. Validation is
limited to `tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj` and its required
project-reference build graph; no solution build or GUI smoke run is performed.
