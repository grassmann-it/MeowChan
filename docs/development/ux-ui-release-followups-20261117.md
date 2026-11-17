# UX/UI release follow-ups — 17 November 2026

This is the short backlog for issues found while dogfooding the release candidate. Items are
kept here even when a focused fix lands, so the next visual review can check the whole class
rather than only one screen.

## Fixed in this release candidate

- **Quick tag input focus (breaking):** when Rate and tag opened with the rating row focused,
  clicking the tag editor could leave keyboard input on the stars. The editor now explicitly
  claims focus after a left click while preserving the native caret gesture.
- **Rating clear affordance:** the old generic `×` icon was ambiguous. The action now uses a
  struck-through star; its accessible name and shortcut remain “Remove rating”.

## Recheck in the next GUI sweep

- **Ambiguous icon semantics:** every icon-only action needs a tooltip/name and must be checked
  for “close”, “clear”, “remove”, and “reset” being visually distinguishable.
- **Empty-space/layout class:** List/Table toggles and archive/media toolbars should consume
  available width consistently; verify at half, normal, and full-screen sizes.
- **Reader/sidebar command routing:** rating, favorite, tag, and navigation shortcuts must reach
  the reader when a media sidebar or thumbnail has focus, without stealing text-entry keys.
- **Shortcut scope parity:** when a media sidebar or reader media has focus, the user should be
  able to request gallery-scoped media actions explicitly (for example `Alt+<gallery shortcut>`);
  document and test the modifier policy so reader commands and gallery commands never conflict.
- **Reader/sidebar rating-entry regression:** with an Archive/Media sidebar item focused, `R` must
  still open the Tagging/Rating surface; test the same routing from Downloads and the reader.
- **Tagging editor keyboard regression (breaking):** the tag input must accept typed text after
  opening the surface, and quick-tag number/letter shortcuts must work without preventing normal
  typing, caret movement, or IME/text-box input.
- **Entry-point state:** opening a thread or reader from Archive/Downloads should select and show
  the first post/media immediately; an explicit enlarge action should enlarge that selected item.
- **Media loading:** changing posts with the sidebar open must retain the previous image until the
  replacement is ready, or show a low-resolution placeholder; never flash a stale image and then
  leave a permanent grey loading surface.
- **Booru/plugin diagnostics:** current bundled plugin repair must be reachable and actionable;
  stale pinned builds should explain the repair path without silently changing the user pin.

## Later (1.2+ candidate)

- Shared query language and saved/recent searches on Media (and other collection pages): `and`,
  `or`, `not`, tag/title/source fields, and a compact combined search interface.
- **Header View menu icon:** the View/work-view menu entry should carry the same eye icon as
  `workspace.view-cycle`, so the menu is scannable and consistent with the header affordance.
- **Settings search alignment:** the settings search field should align with the content column
  (same left/right edges as the Interface section), not float narrower in the header.
