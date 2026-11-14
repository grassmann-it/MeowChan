# Changelog

## 1.1.0 — unreleased — Media page, own tags, Danbooru and plugin SDK 1.1.0

- Draft, not published: no release date, package or verification. See the
  [draft release notes](docs/releases/v1.1.0.md), which mark what is still pending.
- Add a Media page over every saved file with filters, duplicate grouping by SHA-256,
  multi-select tagging, gallery and original export, and a way back from the thread.
  Tiles show favorite, rating, own tags and the video poster, and tags can be edited on
  the tile.
- Share one right-click menu across the Media page, Archive table, tiles, inline
  attachments, gallery and video overlay (same order, labels and reasons; also via
  Shift+F10 and the Menu key); the gallery menu now offers own tags.
- Add private own tags for threads and exact media versions, with Reader and gallery
  editing, Archive tag filters, a Tag manager, cleanup protection and tag hand-over
  when a source changes an attachment key. Loose and legacy files that no captured post
  binds can be tagged too, with orphan check and retryable cleanup; own tags are not
  exported.
- Rework the media tag dialogs around search: one field filters a single list of toggle rows
  with use counts, the tags on the file first, then the most used, then A to Z (or sort by
  most used), with one-step undo from the footer. Tagging several files together shows for each
  tag whether all, some or none of the ticked files carry it ("3 of 5"). A vocabulary of 20000
  tags opens and filters smoothly, and the tag manager gets the same sort choice.
- Give tags, filter values and badges one chip look: own tags are filled, thread tags carry a
  house mark, Danbooru source tags have a dashed outline and a category mark, so they differ by
  shape and not only by colour. Every active Media page filter shows as a removable chip under the
  toolbar, even while the filter area is closed, with **Reset all** and a count on the button
  ("Filters (3)"). Tiles show their state in fixed corners (type, provider, not saved,
  no media tags yet, favorite star on hover or focus).
- Select several files in a thread's Media tab, as on the Media page: Ctrl/Cmd+click,
  Shift+click, Shift+arrows, Ctrl/Cmd+A, Esc. With two or more selected, **Tag together**,
  **Selection as gallery**, **Export originals** and **Clear selection** act on all of them, also
  from the right-click menu.
- Add **Import files…** (menu, or drop files and folders on the window) for your own files.
  MeowChan checks each file by SHA-256, copies new ones to `meowchan-import/` in the download
  folder and only links files it already has; your originals are never moved or changed. You can
  add own tags for the new files, stop after the current file, and open the batch on the Media
  page; imported files are loose, taggable media in the index and gallery. The dialog names the
  settings row when a limit stops a selection.
- Show reader posts as cards on the page tone, with the opening post directly on the page. The
  header is one line (subject, name, date, number); a click on the number copies `>>number` for
  quoting. Thumbnails sit flat under a file line with name and size, lines starting with `>` are
  green, and `>>number` in the text jumps to that post (the quote buttons stay as the keyboard
  path). Post bodies are set in a bundled Noto Sans subset (Latin, Greek, Cyrillic; CJK uses the
  system fonts), credited in About and the README next to Inter; the interface stays in Inter. The
  HTML export follows the same cards, colours and greentext.
- Reader: rest the pointer on a quote, or focus it and press Space, to preview the quoted post
  (Esc closes it, Enter jumps there) and see the target flash after a jump. A line marks where new
  posts begin ("N new posts since your last visit", a divider across the card width) and **Back to last read** returns to it. Posts over
  24 lines fold to 18 with **Show more** (remembered per post; find opens them). The reply counter
  in the header opens the replies as nested cards, five more at a time. References to posts that
  were not captured are struck through, spoilers stay hidden until you click them or press
  Enter or Space, code is monospace, external links are underlined; the HTML export shows the same.
  An external link opens in your browser only after a confirmation that shows the address with its
  host in ASCII form; the confirmation starts on Cancel, and "Don't ask again this session" lasts
  until you quit. Only http and https addresses of at most 2048 characters without user
  information or hidden Unicode format characters count as links; anything else stays text.
- Add the setting **Animations** (Follow system, On, Off): short fades, hover and chip transitions
  and toast changes follow the system reduced-motion setting or stay off. Progress bars and
  spinners always stay.
- Draw the keyboard focus ring in two shells so it stays visible on any background, and show the
  selected state by shape on chips, tiles, thread media tiles (drawn check mark) and the settings
  sidebar (a bar), not only by colour.
- Give every window and dialog the app's own title bar. Windows and Linux keep Snap, resize and
  shadow; Windows 11 gets square corners; macOS keeps its native traffic lights, full screen and
  rounded corners with the app caption beneath. `MEOWCHAN_WINDOW_CHROME=system` brings back the
  platform title bar everywhere.
- Announce a found update with a quiet toast (**Release notes**, **Later**, **Skip this version**)
  and a tray entry; the footer notice stays as the indicator. Each version is announced once per
  start, never for a skipped version, with automatic checks off, or when information notifications
  are off. Nothing is downloaded automatically.
- Downloads: removing from the watch list offers **Undo**, retries state the reason and the next
  step in plain language, the tray menu can pause and resume, the search also matches the status
  text, a link to **Show in list** appears for addresses that are already watched, and toasts last
  by category.
- Gallery and Media page: a saved file without a viewer shows **Open with default program** and
  **Show in folder** (runnable types such as scripts are never handed to the default program),
  **Copy file path** joins the shared menu, the gallery export button reads **Original…**, the
  slideshow duration is a dropdown next to the playback mode, the reader post menu copies text,
  number and (4chan) link, and the archive media footer has **Manage saved ratings**.
- Make German and the counted texts consistent: always "du", one wording per action
  (Reader, Downloads, Beobachtung, Post), correct singular forms (also Russian 2 to 4), and
  one file-size format that never shows 0 MB for a small file.
- Add the Danbooru source, read-only and enabled by default (it sends nothing until you
  search or a watcher checks): tag search with restricted previews, single posts, watchers
  that continue below a post id cursor without a page limit, a one-time check of a paused
  watcher, readable errors in all twelve languages, provenance and offline use. It is
  reachable from the Workspace menu and bundled with the plugin package. Downloaded
  Danbooru originals show in the Media page, gallery and favorites; own tags on them are
  not available.
- Danbooru window: a search shows at most 5 pages of 20 results; every result and watcher
  card offers **Open post in browser** and **Copy link**; a **Danbooru watcher**
  notification reports originals that a check added to the download queue; the detail
  dialog copies source tags and uses up to two of them as a search; pending originals
  are picked up in the background at start. Result cards list rating, score, favorites,
  date and original file as facts with the same rating and tag-category names as the detail
  dialog, and every source tag is a button that adds it to the search (up to two; nothing is
  requested until you press Search). Several Danbooru search addresses in one Add input open the
  window with the last one and report the unused ones once; words after a space stay part of
  an address; a dropped board name such as `/g/` counts as a link, not as a file. Any text
  that mentions a Danbooru host is redacted from logs and feedback context.
- Add the settings **Danbooru catalog limit (MiB)** (default 32, 8 to 128), **Own tag
  limit** (default 1024, 1024 to 20000) and **Own tag file limit (MiB)** (default 32, 8 to
  128). The Danbooru limit warns in the watcher list from 75 %; a refused new tag or write
  names the setting in the user's language. Lowering a limit deletes nothing and never makes
  a saved file unreadable; removing, renaming and cancelling keep working above a lowered
  limit. They sit on the renamed settings page **Storage and limits** (formerly named after
  the plugin-process expander) next to the free-space reserve. The first-run text and the
  Danbooru warning now point to that page, Downloads shows the effective parallel limit live
  with a short hint, and plugin settings say that they apply at the next start.
- Make the remaining amount and size limits settings under **Storage and limits**:
  entries per favorite/thread-favorite/rating list (default 50000) and their file size
  (8 MiB), post bookmarks (10000), saved searches (500), reader groups (500), the question
  before "Export all matches" (500 files or 2 GiB), the video preview cache (512 MiB,
  2048 files), the seven import limits (including 32 tags per batch and 100000 batches) and
  the media index cache (256 MiB, 1000000 entries, 200000 threads). Lowering a limit deletes
  nothing; a full list names the setting in the user's language, and the status bar warns
  from 75 % of the favorites, thread favorites and ratings limits and of the media index
  cache. Fixed technical limits are listed in the help.
- Raise the minimum height of the archive metadata, gallery details, media occurrences,
  reader-group prompt, saved searches, experimental activation and startup error windows so
  the footer stays visible below the title bar.
- Add plugin SDK 1.1.0 (booru post search, blocked-declaration status), reviewed and
  moved to Shipped, with a one-time automatic transition for bundled plugins that only
  change the declared SDK version. The booru post identity is `BooruPostId` (renamed before
  the freeze; JSON shape unchanged).
- Bundle five further experimental chan sources (disabled by default) and two documented
  technical blocks; stop passing the 2ch provider MD5 as an integrity expectation, so archived
  2ch originals are fetched once more on re-check.
- Add **Inspect transfer package…** in the Archive header: packages are verified and staged
  before any write, planned as a new thread or a merge, applied only on confirmation;
  several threads apply one after another with a receipt; **Check again** re-reads a
  package changed on disk.
- Record byte-level merge conflicts and review them under **Manage conflicts** (keep,
  discard, or use as the active version, each after confirmation); reclaim unreferenced
  discarded alternatives after a confirmation.
- Offer to discard an import or merge that stopped before its commit, in a two-step
  question at startup.
- Accept picked or typed Archive source/board/state filters immediately; show archive status
  as an overlay; open filters over the results; keep the Reader toolbar on one row.
- Show the orange cat in the light theme and the grey cat in the dark theme; soften the light theme
  (the palette corrections follow below).
- Show the theme-appropriate cat in the header and both cats side by side in the About window for documentation
  and credits (window, tray and dialog icons keep following the theme), and credit the photographer of both cats (Simba, orange; Mila, grey),
  Alexander Huxel · Webentwicklung Huxel, in THIRD-PARTY-NOTICES, the README and About > Credits.
- Keep Archive, Downloads and Settings usable while the session is still starting; writes
  wait for Ready.
- Write a rolling per-user log file outside the profile and show unexpected UI-thread
  errors as a banner instead of crashing.
- Restore every readable watch past an unreadable work journal row, restart failed
  download loops on the next signal, and open Ctrl/Cmd+F find in the large Reader.
- Return focus to the export button or **More** after closing export, scroll the settings
  validation error into view in small windows, and keep secondary windows at normal size
  after a title-bar restore on Linux/X11.
- Rework the default keyboard shortcuts. **Media** is **Ctrl/Cmd+3** in footer order, with
  **Rules 4**, **Sources 5**, **Plugins 6** and **Diagnostics 7**, then **Statistics 8**; footer,
  menus and the F1 help list the pages in the same order. New defaults: **Ctrl+T** thread tags, **Ctrl+Shift+T** media tags, **Ctrl+D** thread favorite,
  **Ctrl+Shift+F** filters (also for board filters, which moved from Ctrl+Shift+R), **F3** and
  **Ctrl+G** for find next and **Shift+F3** and **Ctrl+Shift+G** for find previous in the large Reader,
  **S** slideshow, **I** details, **R** rating, **Ctrl+E** export original and **Ctrl+O** show in folder
  in the gallery and Media page, **Ctrl+A** select all media, the Menu key for the context menu, one
  **Alt+Left** Back and one **Alt+PageUp/PageDown** page gesture for the commands that never apply
  together. macOS gets its own defaults (Cmd for Ctrl, no Fn-only keys, Cmd+Delete, Cmd+Left, Option+Cmd+R
  to refresh, Control+Cmd+F for full screen), Apple glyph notation, and a native menu bar. **Settings,
  Keyboard shortcuts** can turn the single-character shortcuts off. Saved shortcuts are never changed:
  a new default yields to a saved binding of another command instead of silencing it, and the startup
  warning appears only for bindings that are really blocked.
- Speed up large collections: the saved media index loads in the background with a status
  instead of freezing the window, is written only when it changed, and unchanged threads and
  imported batches are reused without opening their manifests; the own tag store and the import catalog are
  indexed and skip re-parsing an unchanged backup; the tag list lays out again only when the filter
  text really changed; finding the reading line no longer walks the visual tree on every scroll;
  the download speed timer stops while the window is hidden or minimized; opening a folder on an
  unresponsive network share no longer freezes the window (2 s limit); chan plugins now ask for
  compressed responses.
- Fix write and queue problems found in review: pausing, resuming, removing and cancelling in the
  Danbooru catalog keep working above a lowered limit (only growth is refused), a failing
  acknowledgement can no longer fail an original already accepted into the queue, and leftover
  temporary catalog files from a crash are cleaned up at the next start.
- Improve screen reader and keyboard use: hidden spoilers get a neutral name until revealed,
  tiles and Danbooru facts have named values (type, untagged state, "term: value"), the quote
  buttons stay reachable with Tab after the post text.
- Show every file size in binary units (KiB, MiB, GiB, TiB; 1 MiB = 1024 KiB), matching the
  limit and bandwidth settings: tiles, export, import, the gallery file plate, attachments and
  the Danbooru window now use one format ("1,5 MiB" in German, "1.5 MiB" in English). The size
  limit of a Rules editor is entered in MiB; saved rules keep their exact byte count, so an old
  "10 MB" rule now shows as 9.537 MiB (rounded to three decimals for display only; an unedited
  field keeps its exact byte count). A language pack may be 2 MiB.
- Add seven bundled UI languages next to English, German, Spanish, French and Russian: Simplified
  Chinese (简体中文), Japanese (日本語), Korean (한국어), Brazilian Portuguese (Português (Brasil)),
  Czech (Čeština), Italian (Italiano) and Polish (Polski). They are AI-authored translation
  previews that no native speaker has reviewed, so the chooser marks each one **(Untested)**;
  missing texts fall back to English, and the saved language is never changed on its own. Counted
  texts follow each language's plural rules (Czech and Polish have separate forms for 2 to 4).
  Chinese, Japanese and Korean use the fonts of your system, with the regional glyph shapes of the
  chosen language first; nothing is bundled, so **Linux needs Noto Sans CJK installed**
  (`noto-fonts-cjk`, `fonts-noto-cjk`).
- Correct the colour palettes (Light and Dark) against measured contrast: Dark buttons, text and
  combo boxes get an outline of at least 3:1; error and success colours stay apart for red-green
  colour vision (Dark error is coral, Light success a clear green); the empty progress track is
  visible in Light, and the Dark progress fill is the accent blue again; the gallery overlay and
  the export backdrop follow the theme (the Light export backdrop is 40 percent dark, so the
  dialog card stands out). Contrast of the key pairs is pinned by a unit test; a measurement of
  the whole app on a real display is still open.
- Redesign the Sources page: maturity, maintenance and status show as chips on the list instead of
  a chain of words (only where a source deviates, for example **Experimental**), the list is sorted
  stable, unclassified, experimental, then by name, and the selection is a bar. A source's watches
  are listed in two lines each (address, then status) without an inner scroll area; the list shows
  8 by default and **Show all n in Downloads** continues (setting **Sources page: watch rows per source**, 3 to
  200). The URL field is gone from the page: **Add in Downloads** opens Downloads with its URL
  field focused.
- Fill the remaining gaps in the settings under **Storage and limits**: the clipboard monitor's
  URL count (default 32, 1 to 500) and text size (default 64 KiB, 4 to 4096 KiB) are settings, and
  the suggestion says when URLs were skipped or a text was too large to search; bandwidth
  (up to 100000 MiB/s), parallel downloads (up to 256) and the free-space reserve (up to
  65536 GiB) have a maximum, and a larger value saved by an older version is reduced instead of
  marking the file damaged; the favorite file limit can go down to 1 MiB and the own tag limit down
  to 256. The page says once that a lower limit deletes nothing; hints, errors and the waiting
  line of the free-space reserve name the setting, and the preview cache hint states its real
  memory cost. A 33rd archive filter tag is refused with a sentence in your language.
- Polish the interface in a second review round: Archive filter fields carry a label above them
  and short placeholders; the favorite button shows a filled star for a marked file and puts a
  failed write in its tooltip; the board wizard shows one count line per board; Diagnostics turns
  off watch actions for a removed result; the Plugins page header moves under the title in narrow
  windows and a failed plugin step shows a sentence with the next step; the URL-format example for
  a mistyped link uses a neutral 4chan address; the German texts say Strg and Umschalt in shortcuts,
  "experimentell" and "Anker"; the empty Downloads boards state offers the clipboard import; saved
  searches, the date field (which shows its pattern in your language) and the SHA-256 search
  explain themselves in plain words; the cleanup, board admission, filename search, import and
  reader group windows fit small screens; screen readers get names for the license list, the URL
  import report and the board preview. Reader cards with media are one block (their width follows the **Post width** setting),
  and the navigation cross floats over the content (see the Reader overlay entry below). Danbooru results and watchers are
  cards, dialog headings and counters use one type step and tabular figures, and popups share
  one shadow and corner radius.
- Make external links in a reader post clickable: a link opens only after a confirmation shows its
  full address (the host in its ASCII form, highlighted); hover, focus, a text selection or a hidden
  spoiler never open it, and "Don't ask again this session" is not saved. The HTML export keeps
  plain links.
- **Animations: Off** now also reaches menus, flyouts and popups, and a theme change no longer
  cross-fades colours.
- Speed up more starts and returns: the plugin pipeline begins reading the profile and describing
  plugins while the window comes up (the plugin start probe), the plugin profile file is no longer
  rewritten by a start that changes nothing, the Media page shows stored sound results at once
  and keeps its place when you return while the list is still being filled, post lookup in an
  archived thread uses a cached index, and a verified file is remembered by its full file identity,
  so a swapped file of the same size and date is checked again.
- Add a **Statistics** page (**Ctrl/Cmd+8**) that counts the network and disk traffic of
  MeowChan's own media: a history graph, a table per source (network now, this session, today
  and the kept days, disk read and written, requests, errors) and a **Reset** that asks first.
  Network counts the response data MeowChan receives and, as upload, the request payload that the
  application writes to the connection once a write has completed (both without headers or
  encryption; the number of requests is shown as well); disk counts the media files MeowChan itself
  writes or reads back, not thumbnails, previews, archive or index files. The header download graph
  is now a button that opens the page. It shows the two directions of the network (download
  solid with a filled area, upload dashed) and of the disk (write solid, read dashed) as two labelled
  blocks, **Network** with "↓ x ↑ y" and **Disk** with "R x W y", each with its own scale that
  steps by 1, 1.5, 2, 3 and so on and a decaying maximum; the page shows network download, disk write
  and disk read as three separate graphs and counts download only (upload is shown in the header,
  the tray and the accessible name). The setting **Header graph** has six choices: Automatic
  (network and disk, each with its own graph, in every work view), Network only, Disk only, Network
  and disk stacked, Network and disk side by side, and Text only. Graphs need a window of at least
  1000 dip and side by side one of 1280 dip; narrower windows keep the labelled values and drop the
  graphs. The tray tooltip shows the current rates and the number of active downloads, the tray
  menu starts with the same labelled network and disk lines while downloads run (a click opens
  the page), and nothing is measured while the window is hidden and nothing downloads. A
  **Statistics** block on the Behavior page holds nine settings: update interval
  (default 1000 ms, 500 to 10000), graph length (120 readings, 30 to 3600), update interval in the
  tray (5000 ms, 2000 to 60000), tray tooltip and tray menu (on), keep statistics on this computer
  (on), days kept (30, 1 to 365), saving interval (5 minutes, 1 to 60) and most sources (64, 8 to
  256). Daily totals are kept in `io-stats.json` in your profile (upload is an optional field, so
  files from before stay valid); with the switch off they last only until you close MeowChan.
- Rework the Rules page layout: the footer lines up with the 880 dip content column and is the
  only place for the status; an invalid field shows its error at the field, **Save** stays
  available, the footer names the invalid field and offers **Go to field**; the subtitle is one
  sentence and gives way to the form below 600 dip window height; the template buttons are
  grouped, and the actions for a selection appear only while something is selected.
- Share one behaviour between the Archive and Media filter panels: **Esc** closes the panel and
  returns focus to its toggle button in both.
- Add the setting **Post width** under Reading & media: **Fit to content** (default) sizes every
  post card to its header, text and media, up to a reading width of 1120 dip, left-aligned like a
  post on an imageboard; **Centered column** gives every card the full reading width in the middle;
  **Full width** stretches every card across the whole Reader. The open
  Reader follows a change at once.
- Make the limits of the saved sound check of videos settings under **Storage and limits**:
  **Sound check cache: size** (default 64 MiB, 16 to 128) and **Sound check cache: videos**
  (400000, 50000 to 1000000). Lowering one never makes an existing file unreadable; above the
  limit only the answers that fit are saved and the rest is checked again when the Media page
  opens.
- Make the HTML export read like the Reader: reply posts are cards that fit their content up to
  52 rem, the opening post sits on the page, each post has one header line, and files float beside
  the text; narrow screens stack them and printing uses the light palette. Every exported page has
  a switch **System / Light / Dark** that needs no script (nothing is stored, so a reload returns
  to the system default) and an **About** button that opens a dialog with the thread facts the
  export already knows, the MeowChan version, a link to grassmann-it.de (the one external
  link; captured addresses stay inert) and the cats Simba and Mila with the photographer's
  credit; the picture is copied as `meowchan-cats.png` next to `index.html`. The export still has
  no script, no external load and the same strict content policy; its texts follow the export
  language, in all 12 interface languages (the seven newer texts are AI-authored and not reviewed by
  native speakers).
- Explain an unusable settings file at startup in your language instead of an English technical
  sentence: the window says what happened (a usable backup exists, the file is from a newer
  version, it is damaged without a backup, or the profile has data but no settings file), that
  nothing was changed or deleted, the profile folder and the way out.
- Stop a damaged backup (`.bak`) from blocking every save of reader groups, saved searches and
  ratings: the damaged copy is moved to `.bak.bad` (a numbered name if that exists, nothing is
  overwritten or deleted) and the good file becomes the backup. A backup from a newer version or
  one that cannot be opened still refuses the save before any file changes, now with a sentence
  that names the file and the way out. A settings file is no longer written when it would exceed
  the 4 MiB that the next start can read, and the stores treat an undecodable property name as
  damage instead of failing the check.
- Harden the external link confirmation: it starts on **Cancel**, so a held Enter cannot open the
  link; screen readers hear the full address, Tab stays inside the dialog, and reader shortcuts do
  nothing while it is open; addresses with hidden Unicode format characters (direction overrides,
  zero-width characters) and hosts that cannot be written in ASCII stay plain text; a link that
  could not be started (no default browser) is reported in your language. Saving settings no
  longer cuts running transitions short when the theme did not change.
- Add a **View** menu to the large Reader. **Hide media** and **Only posts with media** apply to
  every thread for the session; a deliberate jump to a hidden post (a quote, find, a bookmark or
  the reading position) shows it temporarily, and **Hide again** hides it once more. The reader now
  cuts its pages from one projection of the thread, so Up and Down, paging, the reading mark and
  the retained position follow the order that is shown. A reference to the opening post or to
  another thread gets a "(OP)" or "(other thread)" marker behind it (with a screen reader name), and
  the quoted post is outlined while the pointer rests on its reference.
- Add **Post filters** (View, **Post filters…**): rules on post text, subject, file name or MD5 (hex
  or base64) hide a matching post, shorten it to a placeholder or highlight it, optionally together
  with its replies. **Hide post** and **Show post again** in the post menu (and as shortcuts) hide a
  single post; reply cards and quote previews of a filtered post say so. Nothing is deleted, and
  the HTML export always contains every saved post. Rules and hidden posts are kept in the new
  profile file `post-filters.json` with one backup and an explicit recovery of a damaged file. The
  lists hold 200 rules (20 to 2000) and 10000 hidden posts (1000 to 100000) by default; both limits
  are settings under **Storage and limits** (lowering one keeps existing entries and only stops
  growth). A rule's **Edit** button loads it into the form and **Save changes** replaces it in
  place; rules from a newer version stay read-only.
- Keep decoded gallery images in a memory-bounded cache and decode the neighbours in the
  background: after an image is shown, the next and the previous images (3 and 2 by default, 0 to
  20) are decoded behind it and dropped as soon as you move on; videos and GIFs are skipped, and a
  kept image is shown only while its file is unchanged since it was verified. New settings under
  **Storage and limits**: **Gallery images kept in memory (MiB)** (default 256, 0 turns it off, up
  to 4096) and the two preload counts. Archive tiles and reader attachments now share the Media
  page's preview cache instead of decoding again each time they scroll back into view.
- Verify the plugin toolchain at startup outside the build gate and in parallel with reading the
  profile and scanning the plugin inventory, and re-check every verified file's identity right
  before the toolchain is used.
- Move the rarer gallery controls into a labelled "⋯" menu. Back and forward with position, close,
  fit, 1:1, the post panel, favorite, the reader toggle and play or pause stay on the bar; zoom,
  rating, tags, export original, slideshow settings, overlay options, full screen and shortcut help
  move into the menu with the same commands, shortcuts and tooltips (it opens from the keyboard,
  Escape returns focus). The primary buttons go icon-only below 1100 dip, and below 900 dip the
  post panel floats over the picture so that at least 280 dip of the image stays visible. The
  gallery file plate shows sizes in binary units with the exact byte count in brackets
  ("63 KiB (64,624 bytes)").
- Make secondary actions on cards quiet ghost buttons (Danbooru watcher and post cards, the
  Downloads inspector's navigation actions, the reader capture toggle): the label alone at rest,
  the 3:1 outline on hover, the accent when pressed. **Check now** and **Remove** keep their
  outline.
- Keep **Enter** as the media key on a post that has media; a reader jump now ends when you take
  over and reports the position it reached. The seven new language packs went through a
  terminology review (one term per concept in each language); they stay untested previews.
- Redraw the **Plugins** page in the style of Sources: one card per plugin with chips only where a
  described source deviates (the same wording rule and texts as Sources), the plugin count in the
  header ("Plugins: n"), a 280 dip list that switches to one pane below 886 dip, and the plugin's
  details in one card. The actions menus of the category and of the selected plugin are square
  "⋯" buttons (tooltip **Actions**, same commands, keyboard access and focus order); generated
  plugin setting fields have the workspace input height, and a setting's label, **Set value**
  box and field sit 8 dip apart. A check box that sits directly in a horizontal row now centres on
  the row's line, so the Diagnostics filter row and the Reader status line line up with their
  32 dip controls.
- Fit the Reader attachment to the drawn picture: it is as wide as the thumbnail (the file line
  keeps a 140 dip minimum, wraps to two lines, then trims), and the click and focus frame, the
  play button and the star sit on the picture. The 16:9 slot is reserved only until the thumbnail
  has loaded. Archive and Media page tiles keep their uniform cell but letterbox the picture and
  anchor the type badge, star, check mark and state mark to the picture's corners.
- Make the Reader's navigation cross a pure overlay: it reserves no side lane, content uses the
  full width, and its backdrop is not hit-tested (only the four buttons take clicks). It rests at
  60 percent and fades out after 2 seconds without pointer movement; pointer movement over the
  Reader, the pointer over or within 64 dip of it, keyboard focus and a navigation shortcut bring
  it back (the fade is off under reduced motion). The extra margin after the last post is gone: it
  made a jump to the last post of a page overshoot by thousands of dip.
- Show the tagline "Open-source imageboard downloader — now with 100% more cats" in the title bar
  again, in the muted italic style between the brand and the header graph, translated in all 12
  languages. It disappears when its column is narrower than 200 dip and never pushes the graph or
  the window controls.
- Group the footer into work, set-up and observe pages: **Downloads, Archive, Media**, then
  **Rules, Sources, Plugins**, then **Diagnostics, Statistics**, then **More** and **Settings**,
  with a separator only between two groups that both show a button. The former **Tools** menu is
  **More** in all 12 languages, and the header menu lists every page, Downloads included. Sources
  and Plugins stay separate pages that point at each other: the Sources detail offers **Show
  plugin**, Plugins offers **Show in Sources**, and both page headers say what the page is for
  and where the other half lives.
- Make the work views reduce each page for real. **Barebones**, **Simple** and **Advanced** decide
  which tools stay on a page and which sit under a page-local **More** (⋯) button, with their
  label, shortcut and disabled reason; the shortcut of a tool under More keeps working. Data,
  state, errors, active filters, approvals and safety questions stay in every view, and a block
  that needs attention (a validation error, an active filter, a probe result) opens itself. This
  covers the header and app menu (a **View** group and the new **Switch work view** command, which
  has no default shortcut), the Downloads toolbar, columns and inspector, the Archive (header
  tools, filter fields under **More filters**, results bar, columns), the Media page (header tools,
  filter fields, selection bar), the large Reader (a narrow window moves tools under **More** from
  the end of the order Gallery, Read, Find, Tags, Export, Full screen, View, History, Capture, so
  the HTML export outranks the rarely used actions) and the Rules, Sources and Plugins pages. Diagnostics,
  Statistics, Settings (four of six groups stay direct in Simple), the dialogs and the Gallery (curating
  tools under **More** in Barebones) follow the view as well. Existing Barebones and Simple profiles see
  a one-time notice.
- Search with one field and a scope. Archive and the Reader's find bar share one search field with
  a scope chip (**Text**, **File name**, **SHA-256**) inside it; File name and SHA-256 open their
  existing result dialogs with the draft already searched, and SHA-256 keeps its exact 64-digit
  check inline. Ctrl/Cmd+F opens the Text scope; Alt+Down or F4 in the field opens the scope menu.
  The Media page's file name search sits at the head of its tools with the same chip, the scope
  menus carry the query between Archive, the open thread and Media (a different query in the target
  is offered with **Use it** and **Keep current**), and an empty Archive or Media offers "Search
  “x” in Media/Archive". **Save applied search…** and **Saved searches…** are one split button.
  Diagnostics, About licences, reader groups, favorites, cleanup, the ratings manager and the
  shortcut editor use the same search box, and Diagnostics gets Ctrl/Cmd+F.
- Add a settings search at the top of the Settings page (Ctrl/Cmd+F): typo tolerant, accepts
  abbreviations and word parts, ignores accents and case, knows English and German synonyms for
  each setting and always matches the English label too. The arrow keys move between hits; Enter
  opens the group, unfolds collapsed sections and marks and focuses the setting, Escape clears
  first and then leaves the field; a polite live line announces the hit reached ("n of m: label,
  group") and an empty result.
- Offer one **Export…** dialog. Format (HTML page or link list) and scope (this thread, selected
  threads, all matches) are chosen in its header, and every entry (Archive menus, keyboard,
  results toolbar, Reader, Downloads, gallery) opens it with its choice preselected; a single
  option shows as text. The originals dialog shows the same header, and after a successful export
  **Close** becomes the primary action. The entry labels read **Export…** in all 12 languages.
- Curate in one way. Media tags are **Ctrl/Cmd+Shift+T** (media page and gallery), thread tags stay
  **Ctrl/Cmd+T**, and saved shortcuts win. The favorite mark is one star drawing everywhere (outline
  star unmarked, filled star in the favorite colour marked) on Archive and Reader tiles and cards,
  the thread list and header, Media tiles and the gallery toolbar. **Manage saved ratings…** opens
  from the Media page, the Archive rating row and the gallery menu; the Diagnostics button is gone.
- Add **Add plugin…** to the Plugins page: it copies one `.cs` file into the plugin folder through
  the inventory's own per-file check (before and after the copy), never overwrites and runs
  nothing, so the file is judged as if you had placed it by hand and is built at the next start.
  A step indicator (**Installed**, **Enabled**, **Active**, or active after restart) names the one
  next action; approvals, consent, activation and migration keep their buttons and confirmations.
  Barebones and Simple keep **Add plugin…** under More.
- Word failures once. Rows, the Downloads inspector, Diagnostics and Sources share one short
  failure headline ("Refused (HTTP 403)", "Throttled (HTTP 429)", "Name resolution failed",
  "Storage error" and so on); a next step names the button that performs it, and the banner label
  is shorter. All 12 packs carry the texts.
- Add small flow aids: after the first successful **Add** of a profile one dismissible line says
  that threads are read in the Archive (**Open in Archive**, shown once); one back-label pattern
  ("Back to …") everywhere; **Media rules…** is also offered from the Reader and the Media tile
  menu; and new default shortcuts exist for **Continue reading** (Ctrl/Cmd+Shift+R), **Import
  files…** (Ctrl/Cmd+Shift+I), **Follow live** (Ctrl/Cmd+Shift+L), **Show new posts**
  (Ctrl/Cmd+Shift+N) and Diagnostics **Retry selected targets** (Shift+F5, Ctrl/Cmd+Alt+Shift+R on
  macOS).
- Settle the design system: buttons are 32 dip high like inputs (title-bar icon buttons keep 30),
  controls, sections and message surfaces share the 5 dip corner radius, pages have a 12 dip inset,
  text uses the type scale tokens (SemiBold instead of Bold), links are accent text without an
  outline, secondary actions are **ghost** buttons, and the Ursa banner is replaced by one message
  surface for error, warning, information and success. Downloads, Archive and Media share one
  empty-state layout, drawn icons replace font glyphs (close, more, star, check, chevrons; the
  Reader toolbar actions carry icons), and toasts and the shell message close with an icon button.
  A design lint test keeps new font sizes, colour literals and off-grid spacing out.
- Fix hover and contrast: one hover signal (a hover fill and a 1 px outline that is reserved at rest,
  so nothing moves), pressed and selected rows use the accent surface, and selected rows, tabs and
  footer pages carry an accent bar. Menus, drop-downs, suggestions, toasts and tooltips take the
  palette instead of the old Semi greys; the remaining Semi blue (field focus, selection, check
  and radio, slider, pagination) uses the palette accent. The Light interactive outline is darker
  (3.37:1 on hover fill), the decorative border is `#948A7D` (also in the HTML export), and the Light
  progress bar is the dark accent brown `#4A2203` (3.41:1 on its track), 4 dip high; the Dark track
  no longer equals the hover fill. The ratios are calculated from the palette and pinned by unit
  tests; a measurement of the rendered app is not recorded.
- Tidy the Rules and Settings pages: Rules is three cards (scope picker with its editor, filter
  and templates, effective preview) with the origin link beside the origin text and **Delete** red
  only while it asks for confirmation; the Settings footer actions end on the field edge and lone
  buttons sit beside their control (**Forget reading position**, **Check folder**, **About**).
- Improve screen reader use in the new surfaces: the saved searches split button opens with
  Alt+Down or F4 and is named after every refresh, the priority and favorite badges keep their state
  as hidden text, plugin step chips carry the spoken step name, and dialog footers stay inside the
  window at the minimum size in long languages.
- Add Booru watcher lifecycle rules. Shutdown retires watchers first and keeps the transports until
  the queue has drained; disabling the Danbooru plugin revokes the watcher's ports at once, and
  enabling it again builds a fresh watcher over the same original queue. While MeowChan takes the
  profile exclusively for a maintenance step, watcher checks and commands report that the profile is
  busy, a running check ends with its recorded result, a late 429 is still kept, and watchers resume
  afterwards unless recovery is required. The global pause now stops automatic checks only (a check
  already reading a page finishes it and stops before the next); **Check now** still runs once,
  without unpausing the session or bypassing a provider cooldown. A longer valid `Retry-After` is
  no longer cut to 24 hours. A configurable cooldown cap is not in yet.
- Add directional upload metering. The request payload that MeowChan's application writes counts as
  network upload per source (see the Statistics entry), and the header, tray and graph names show
  download and upload, disk write and disk read separately.
- Add reader display settings under **Settings, Reading & media** (**Display** and **Long posts and
  previews**). Changes stay in the Settings draft until **Save** succeeds and then apply to the open
  Reader at once without losing the reading position; **Discard draft** restores the saved values,
  and an invalid number blocks the save and names the field. They are the preview image width (default
  240 px, 120 to 480; the large Reader uses 1.5 times), folding long posts above (24 lines, 8 to
  400), lines in quote previews (8, 3 to 40), the quote preview delay (300 ms, 0 to 2000), reply
  cards per step (5, 1 to 50), lines per reply card (4, 1 to 20), **Posts per reader page** (50, 20
  to 200), the maximum conversation indentation depth (6, 1 to 16), **Spoilers** (Keep hidden, Show
  on hover, Always show) and **Date and time format** (System format, Weekday and seconds, Since
  opening post, Relative to now; the exact date stays in the tooltip). **Enlarge small images when
  fitting** (No limit, or up to 100 to 1600 %, default 200 %) limits only Fit in the gallery.
- Show the Reader's posts as a conversation: **View, Show as conversation** nests a reply beneath
  the most recent earlier post it quotes, but only when its other earlier quotes lie on that
  post's ancestor line (otherwise it stays at the top level); the opening post stays first, the
  separate reply counter is omitted, and deeper replies stay visible at the last indentation level.
  It reorders saved posts only, sends no request, and the switch lasts for the session.
- Sort the Media page by more than thread, newest or random: **Captured**, **File name**,
  **Size**, **References** (saved occurrences of the same bytes), **Tag count** (own media tags
  plus the thread's tags) and **Rating**, with a direction button next to the choice. Files without
  a value (no name, never saved, unrated) stay last in both directions, and a group of identical
  files counts its highest tag count or rating. The new sort names are in English in every language except German, and the sort is not
  stored in the settings.
- Prepare a lazy scrub preview for videos (positional thumbnails in 5 % slots keyed by the verified
  content, with a bounded disk budget): the code is in, but no control, setting or screen uses it
  yet.
- Keep a decoded gallery image only while its file is still the same file: the check uses the
  file's identity and not only length and time, so a same-length rewrite with restored time and a
  rename over the file are caught, and a second copy of the same bytes in another folder is
  verified before it shares the decoded image.
- Show verified local video posters in the Archive thread list, Reader attachments, Reader media
  and the Media page. A poster is shown only after the local original has been checked, no source
  thumbnail address is fetched, and a cached poster never stands in for a missing original.
- Upgrade: archive schema 5 to 12, media manifest 4 to 5 (6 for imported representations),
  work journal pruning with one backup and a `user_version` stamp, new `private-tags.json`,
  media index cache (version 4; version 3 files are still read, so there is no cold rebuild
  after the update), `booru-catalog.json`, `local-imports.json` with the `meowchan-import/`
  folder, and the new limit and animation settings.
  `private-tags.json` becomes version 2 at the first tag on a loose file; the exact version
  1 file is kept permanently as `private-tags.v1.bak`. Back up the whole profile first;
  there is no supported downgrade.

## 1.0.2 — 2026-10-31 — stability, media controls and startup recovery

- Automatically activate compatible in-place rebuilds of already established bundled
  plugins at startup, including local `dotnet run` builds and an explicitly selected
  bundle rebuilt before its first activation. Preserve exact settings,
  enabled/disabled choices and rollback history, without another confirmation/restart
  loop. Current delivered and cached bytes are reverified; pins, changed declarations,
  special rights, ambiguous targets and unproven legacy history remain guarded.
- Automatically continue restored watches after proven initial source admission waits,
  retaining real failure diagnostics and check intervals after attempted requests.
  Correlate metadata history through host-owned worker contexts and retire it safely.
- Offer explicitly reviewed NEW selections of currently delivered bundled plugins
  for unproven missing legacy candidates, with exact settings and immutable actual-
  state backup. Preserve enabled choices; never silently transfer pins, permissions
  or source ownership. Conflicting and incompatible entries remain blocked.
- Preserve the first validated v1 plugin-state envelope and its digest before startup
  mutations; do not fabricate historical backups for profiles already written as v2.

- Schedule multiple user-prioritized thread watches without preempting active files
  or starving runnable Normal work. Keep the original manual/board origin policy
  for Normal downloads and the existing bandwidth/default settings.
- Bound effective transfer concurrency at 32 while retaining larger saved settings;
  distinguish configured and effective values in the Downloads summary. Healthy
  initial media admission no longer consumes the shared network-header deadline.
- Keep callback-backed silent MP4 playback progressing through the verified handle;
  include the required avformat demuxer in Linux bundles. Hide only covered Reader
  pagination adorners so they cannot intercept gallery-close input.

- Animate GIFs through bounded verified frame decoding, preserving encoded loops,
  transparency/disposal, pause/resume, zoom and safe retirement on media changes.
  Reader posters open the actual associated gallery animation; no audio is invented.

- Keep the selected repeat mode and media-focus/layout choices across gallery
  openings and application restarts. Existing profiles retain the docked layout.
- Add Manual, Repeat and mixed image/video Slideshow modes. Slideshow is started
  explicitly; images use a configurable display period and videos advance after
  their actual end. Pausing, reading context or losing interaction stops progress.
- Reveal transport at the bottom edge and post context at the right edge, without
  reopening controls for movements in the middle of the image. Pointer focus on
  slideshow Start no longer pins controls; keyboard focus, gestures and menus stay protected.
  Hover changes no saved preference. Keep opaque text and a native docked fallback.
- Pair controls overlay and auto-hide in one option. Replace permanent boxed edge
  indicators with subtle borderless chevrons shown briefly on mouse motion.
  Hidden controls no longer consume the first navigation shortcut.
- Honor autoplay for the first/manual video even with saved Slideshow mode,
  without starting automatic next. Preserve native play/pause intent across
  seeking, volume changes and panel interaction.
- Resume timeline tracking after seek even with pointer hover or keyboard focus;
  route effective media navigation after timeline/volume input, use gallery Up/Down
  for available video volume, and disable timeline keyboard seeks without changing pointer seek.
- Keep the narrow thread reader full-height: its navigation cross overlays the
  posts instead of reserving a bottom strip. Scrollable last-post clearance keeps
  the final content readable; the scrollbar and pagination remain unobstructed.
- Preserve newer gallery volume changes when saving an unrelated Settings draft.
- Restore known thread titles from local metadata, including media-only watches;
  use a cleaned opening-post excerpt only when no real title exists.
- Make central banners and notification toasts explicitly dismissible. Dismissal
  suppresses matching repeats for the session without removing diagnostic history;
  passive toast expiry is not acknowledgement.
- Record clearer plugin-startup failure phases while retaining explicit activation
  choices. The historical Windows/Leftypol incident has not been reproduced.

- Stabilize Windows native-overlay clipping to break redundant clip/focus-adorner
  layout feedback, without claiming a general framework subscription-retention fix.
  Retire overlay controls terminally without cross-root return; reject late pointer
  and timer callbacks after stop/detach and avoid detached coordinate conversion.
  Hide the separate video overlay on image/Reader transitions so an empty owned
  window does not block image slideshow progress as a false dialog.
- Preserve observed macOS fullscreen-entry frames across exit so delayed native
  callbacks do not overwrite the restored window size; unknown user resizes still win.

See the [1.0.2 release notes](docs/releases/v1.0.2.md) for the exact-source
publication gate and evidence limits. Native output checks do not establish
physical-speaker output; the manual Windows field test follows publication.

## 1.0.1 — 2026-10-26

- Keep UI queries independent of slow download commits through background state
  projection, with bounded interactive work and coalesced preference updates.
- Reduce repeated view work through coalesced progress, incremental download rows
  and deferred refreshes for covered archive and reader views.
- Replace repeated filter staging scans with conservative temporary-storage
  accounting, preserving recovery and cancellation handling.
- Reduce gallery context and thumbnail work while retaining full original-media
  verification, including before cached video posters are used.
- Coordinate video switching and shutdown with actual native cleanup completion;
  stale operations cannot control newer playback.
- Preserve the visible reader post after delayed reply counts, guard archive
  favorites during shutdown, and honor later macOS Normal-window requests during
  fullscreen restoration.

See the [release notes and downloads](docs/releases/v1.0.1.md). Early user feedback
reports better responsiveness; the original Windows/external-USB hang has not
been formally demonstrated fixed. No universal speedup is promised.

## 1.0.0

MeowChan keeps its compact Barebones view and existing download, archive and
reader workflows while moving the desktop to Semi + Ursa and adding editable
source and image-filter plugins.

### What's new

- **Plugin sources.** The desktop runs 4chan and 8kun through isolated workers;
  the standalone library still offers explicit provider registration. Lainchan,
  Kohlchan, Wizchan, Endchan, 2ch, Leftypol, Uboachan, ZZZchan and SushiChan
  extend the delivery to eleven sources. Five local filters cover dimensions,
  file rules, SHA-256 blocklists, animation/format and PNG metadata presence.
  Fourteen editable examples are included.
  The bundled compiler builds local C# files without an installed .NET SDK or
  runtime package downloads.
- **Plugin management.** Inspect candidates, edit files, validate settings and
  run an isolated test without creating watches or archive entries. Activation,
  pending changes, pinned releases and explicit last-good selection are stored
  per profile. Image access requires the applicable rule and source grants.
- **Bounded work.** Separate metadata admission, worker limits and target-space
  checks keep unrelated downloads moving. A blocked target retains its work and
  can be checked explicitly. Image filters inspect the same original bytes that
  are subsequently committed; exclusions and technical failures remain distinct.
- **Desktop and reader refinements.** Local download search, explicit action
  counts, clearer errors, keyboard navigation, accessible field labels and
  optional last-reading-position resume accompany the theme migration. Reader
  navigation sits at the lower right without covering posts, and video captions
  stay clear of the post context. Barebones remains a complete persistent view.
- **Archive counts and video controls.** Saved media counts no longer depend on
  the current download generation. Video uses one Play/Pause control, per-media
  repeat and audio controls that explain missing tracks or output.
- **Update checks.** Settings and About expose version checking, a small update
  notice and platform download links. Checks do not install updates.

### Source integrity additions

- Provider SHA-1 is verified against the original bytes, including before image
  filters receive access. Size/MD5 exceptions never bypass SHA-1 or SHA-256.
- Rounded provider sizes use a separate raw hint and do not become exact byte
  expectations or rule limits. Saved file sizes still describe measured bytes.
- Archive schema 5 and media manifest 4 preserve these fields. Current readers
  retain support for earlier schemas/manifests; older applications cannot write
  these new formats. Existing revision keys without the new metadata stay stable.
  Library/SDK positional constructors are unchanged; providers opt in through
  `ExpectedSha1` and `ProviderSizeHint` initializers. SHA-1 uses Base64 of 20 bytes;
  local asset identity and integrity continue to use SHA-256.

### Breaking (Library/SDK)

`libmeowchan` and `MeowChan.PluginSdk` are not stable APIs in 1.x; minor releases
may contain breaking changes. Recompile library consumers against this version.
`CapturedPost.Ordinal` and its constructor parameter now use `long` to retain
the full order value supplied by a source. Consumers with explicitly typed
ordinal variables should use `long`; existing persisted integer values remain
readable. Plugin SDK references remain independent of the desktop and library;
the optional Host package supplies compiler and worker integration.

The draft-only `ArchiveSearchSpec.WorkFilterFromIndex` / `WorkFilterToIndex` and
`MediaRatingProjection` UI helpers are now internal. Consumers should use
`ArchiveWorkFilter` and rating values 1–5 / null, mapping their own UI filter
indices and sort order. Stored search and rating formats are unchanged.

New `ArchiveCatalog.CaptureAsync` observations now receive exact attachment
revision keys, even when a caller omits the key. A custom provider that downloads
without acquisition/attachment references and later captures only a matching
media ID/size/MD5 no longer gets an implicit gallery/export association. A local
historical unchanged consumer binary passed with its original Core DLL and
failed its gallery association check after the DLL was replaced; preserved
call signatures do not preserve that earlier behavior. Recompiling alone does
not migrate the old logic: freeze the complete provider metadata, compute
`MediaRevisionKeys` acquisition and post/position references, supply them in
`DownloadItem` before enqueueing, and capture the same observation afterward.
The [updated package consumer](samples/LibMeowChan.Consumer/README.md) demonstrates
this path. Genuinely old stored null-key attachments retain strict legacy
matching; new observations do not receive an unsafe legacy fallback.

### Upgrade and validation

Existing source identities, original media paths, archives and watch state are
retained. Archive and work-journal migrations preserve source ordering and keep
media variants separate. Extract the complete desktop payload, including its
`plugin-worker`, `plugin-compiler` and `plugins` directories. Authoring and
hosting instructions are in the [plugin guide](docs/plugins/README.md).

Package source, automated validation run and checksums are recorded in the release
inventory. Physical hardware, accessibility and everyday-use coverage retain
the limitations in the [release notes](docs/releases/v1.0.0.md).

## 0.9.0

The first stable release under the **MeowChan** name introduces a redesigned Avalonia desktop interface, with Barebones as the simple default for new profiles and Simple and Advanced views for more detail. The 4chan/8kun download engine and durable sessions remain; the independently usable library is renamed to `libmeowchan`.

### What's new

- **Shared local gallery.** Thread context actions, saved-media tables/tiles and inline images open one viewer with Left/Right, Fit/1:1, zoom/pan and saved-post context. Gallery navigation from search includes every found thread in current sort order across result pages, including images from nonmatching posts; saved-media navigation follows its sort/version filter. Animated images show a first frame. Preview safety limits are 64 MiB and 80 million source pixels.
- **Full-window reader and video.** Double-click a thread row/card to read captured posts; Shift+Enter or the context menu opens its gallery. Reader/gallery navigation retains the originating selection and search. Closing the gallery returns to the post associated with the currently shown medium. Saved videos have offline poster previews and play through LibVLC in the gallery. F11 toggles fullscreen across the workspace, reader and gallery.
- **Durable media follow-up.** Consecutive 404s pause 15/30/60/120/240 minutes, then at least 240. A terminal source gets a fixed 12-hour follow-up from its first durably recorded end. Restart/pause do not extend it; completion can retain missing 404 media. Other exhausted/capture/storage failures require intervention. Check-now respects media pauses; manual retry grants one outer attempt, including after completion, without a new watch or end window.
- **Compact repeat export.** Known export targets survive restart. Choose a new folder or explicitly update an inspected earlier export; Open folder opens the current successful result. Modified or reduced output preserves the previous folder after confirmation.
- **Selection and archive cleanup.** Multi-select watches and archived threads for copy, removal and portable combined HTML export. Permanent archive deletion previews the files and requires confirmation; ordinary Delete removes watches while keeping their archive. Favorites track exact saved-media revisions and verified originals can be exported without a new download.
- **Board admission.** A preview wizard filters discovered threads by literal title/opening-post text and shows reasons and individual choices before adding a batch. Removed board threads remain excluded until reactivated; filter changes affect future admissions.

- **Check now and separate media retry.** Downloads and Diagnostics can request a check on the exact watch. Checks coalesce, obey provider limits and respect media due times. The separate media retry grants one outer attempt without changing previous rule decisions; completed-thread repair does not restart source monitoring.
- **Persistent Pause/Resume.** Header and menu stop running transfers and source requests as well as queued work; completed files remain intact, and interrupted transfers restart after Resume. Pause survives desktop restarts.

- **Local archive search and navigation.** Search retained post text, titles and identities, filter by source/board/state/capture preference and sort results. Captured quote targets open on the correct post page; local backlinks and reading-position history work across threads. Missing and external references remain explicit without remote fetching.
- **Portable HTML thread export.** Archive writes a new independent folder with captured posts, its own CSS and verified matching saved originals. Retained older media revisions remain selectable by captured checksum; missing media and preserved rule exclusions are explained. Known earlier exports can be explicitly updated or kept by choosing a new folder. Updates stage a complete new result; changed/extra files, associated legacy exports and reduced output require preserving the old folder as a backup. Media-only archives export a current-original gallery.

- **New cat icon.** The selected photographic cat-head cutout now identifies the app, its windows and tray across the desktop packages.
- **Three desktop views.** Barebones is the default, with a smaller, denser window. Simple and Advanced offer more detail. Each view keeps its own density, reading size, archive layout and table column visibility, width and order. Column menus let you choose or reset the displayed columns. Switching views preserves the running session, selection and input.
- **Downloads and Settings pages.** Both remain in the same shell, with navigation at the bottom. Switching pages retains URL input, selection, details, invalid settings drafts and running downloads. Save and Cancel stay on Settings; About opens from the application menu or the cat logo in the header.
- **Compact workspace.** The footer selector lists Barebones, Simple and Advanced. Grouped table cells and initially closed details give downloads more space; import results expand within the intake panel. Menu icons adapt to their theme. Shared button states, readable primary labels, keyboard focus and aligned Settings actions follow the same Dark/Light styling.
- **Appearance and language preferences.** Dark/Light themes, normal/compact density and built-in English/German are saved in the profile. Optional data-only JSON language packs load at startup, with English fallback and visible warnings for invalid or missing packs.
- **Batch URL admission.** Add or Enter accepts comma- and newline-separated thread/board URLs through the existing provider registry. Each entry receives a result, including duplicates and failures; valid later entries still run. Failed lists remain editable, and edits made during an import are preserved.
- **Details for threads and boards.** A side panel, or an overlay in narrow windows, shows the selected URL, actual assigned folder, status and current errors. Thread details also show saved/known media and queued/active media counts. Folder, browser, copy and remove actions remain accessible.
- **Reusable URL copying.** Copy a single URL or all watched thread and board URLs as a newline-separated list. Empty Add, the tray action and the main-window paste shortcut import the clipboard once when explicitly invoked.
- **Updated desktop dialogs.** About and startup errors use the new appearance and localization while retaining profile/tray recovery paths.
- **Linux tray lifecycle fix.** Avalonia 12.1.4 includes the upstream correction for asynchronous registration continuing after tray disposal. Matching Actipro/DataGrid packages, native graphics dependencies, package locks and notices are updated.

### Upgrade and behavior notes

- Existing profiles, watched URLs and assigned download paths remain usable. Stop all older application/library writers and back up the full profile and media manifests before upgrading. New profile/media leases do not coordinate with older writers; concurrent old/new access and downgrades are unsupported. Fresh profiles use the MeowChan name, while an existing installation's selected profile/destinations remain in place. `MEOWCHAN_PROFILE` selects an absolute profile path. New profiles default to **Barebones, Dark and English**, with the view’s default density. Main-window theme/view changes save immediately; pending Settings changes require a successful **Save**. Cancel reloads confirmed settings; neither action leaves the page.
- **Double-click a thread row/card to open its reader**; Shift+Enter or the context menu opens its gallery. Archive context actions also include HTML export. Board double-click retains details. Open folder and thread details remain explicit actions. Escape closes details; Ctrl+L/Cmd+L switches to Downloads and focuses URL input, and Shift+Enter inserts a newline.
- Progress is **saved / selected media**, with rule exclusions shown separately; it is not bytes or a final thread size. Monitoring continues at 100%. “Archived” describes the source, not a local HTML archive. The import report describes the latest import, not a persistent history.
- View/theme/language preferences and normal/maximized window geometry persist. Startup fits the default window to the available work area; leaving fullscreen restores the previous window state. Density, reading size, column visibility/width/order, archive layout and reader split are saved separately for Barebones, Simple and Advanced.
- .NET 10 and the existing platform targets are retained. Library consumers must use the `libmeowchan` package/assembly and `MeowChan.*` namespaces. No binary redirect is provided. Durable retry/lifecycle state requires explicitly passing `WorkStateStore`; the default is in memory.

### Release status

Release downloads are ordered as a self-contained Windows x64 ZIP with VLC; self-contained, ad-hoc-signed macOS Apple Silicon and Intel DMGs with VLC; Linux x64 and ARM64 AppImages with VLC; then Linux bundled-VLC and system-VLC tar archives and their corresponding source archives. The macOS apps have no Developer ID signature or notarization, so first launch may need manual approval in Privacy & Security. The 13 binary and source assets have a `SHA256SUMS` companion. See the [package-to-offline guide](docs/Help.md), [0.9.0 release notes](docs/releases/v0.9.0.md) and [platform validation record](docs/platform-support.md).

### Download engine and archive

The desktop also contains live Diagnostics, a persistent Archive with an offline post/media reader, and inherited media rules.

- Media admission is bounded (256 pending reservations by default), cancellable and fair between manual and board-discovered work; revision successors share that pending budget. Board origins survive restart, and explicit URL admission promotes a tracked thread to manual priority.
- Sources shows registered adapter capabilities and actual watch observations, with shared URL admission, canonical source rules and exact-watch navigation. Registration does not indicate site availability; external providers may optionally declare capabilities without an interface-breaking change.
- Diagnostics shows current watch failures, request/cooldown state, content-storage failures and session warnings, with copy and exact-watch navigation.
- Archived posts embed verified local image thumbnails. Visible images load asynchronously with bounded concurrency; clicking opens the shared gallery. Compact posts stack images above wrapped text, while wider readers put images beside text. Unsupported, missing or mismatched attachments remain explicit.
- The local SQLite catalog retains observed posts for 4chan/8kun. Media may remain on NAS drives; slow media discovery does not block local reading. Removing monitoring preserves captured content. New watches capture posts; restored older watches start media-only until explicitly enabled.
- Rules resolve allowed extensions and maximum metadata file size through Global → Source → Board → Thread, with visible origins and durable first decisions. Existing decisions and files survive later changes. Unknown sizes remain allowed; this is not a transfer-byte cap.
- Manifests retain saved media versions, and the independent library reader verifies selected bytes. Changed size/hash during an active download schedules the latest revision afterward.

Progress now uses **saved / selected media**, with exclusions shown separately. The additive `ThreadContentRuntime`, `ArchiveReader`, rules and media-reader APIs remain independent of Avalonia. Back up the profile and media manifests before upgrading: v1 manifests are accepted, but the new v2 history is not a downgrade format for older binaries. The writer also migrates schema-1 archive catalogs atomically to schema 2 for indexed search and quotes; old schema-1-only readers cannot read the upgraded catalog. The current passive reader can still read schema 1 without migrating it.

Sources, persistent pause, local search/quote navigation and both single-thread and combined HTML export are implemented. Structured packages/import, TUI/daemon clients and dynamic plugin loading remain later work.

## 0.8.0 (released as oChan)

This update rebuilds download and session handling for 4chan and 8kun and makes `libochan` usable independently of the desktop app.

### What's new

- **More reliable source checks.** Updated 8kun media URLs, canonical URL handling and conditional thread requests reduce broken or duplicate work. Archived or missing sources remain distinguishable from unfinished media downloads.
- **Controlled HTTP requests.** Shared request pacing, bounded retries, server cooldowns and request/body deadlines handle rate limits and temporary failures. Stopping a watch cancels its requests and waits for cleanup.
- **Verified downloads.** Size and checksum checks, atomic file publication and manifests prevent partial files from appearing complete. Existing unrelated files are preserved. Rechecks retry failed media and repair missing or changed files even when the thread response is unchanged; cached verification reduces repeated disk reads.
- **One shared download queue.** Parallel-download and bandwidth limits apply across all watched threads. Duplicate jobs are suppressed, and removal waits for the affected downloads to stop. Removing a board leaves its admitted threads running; removing a thread prevents automatic rediscovery until it is explicitly added again.
- **Durable sessions and settings.** With session restore enabled, watch changes are saved as they happen. Saved sessions retain removed-thread suppression and assigned download roots. Settings are validated before use, save failures are visible, and recovery preserves damaged files. Changing the output root keeps existing watches in their assigned directories.
- **Desktop fixes.** Thread rows show source state, media progress and queued/active counts. Sorting handles numeric values correctly, context actions target the clicked row, and folder actions open the assigned destination. Clipboard import, settings dialogs and tray fallback are more reliable; Exit remains accessible without a working tray.
- **Standalone library.** `libochan` is a separate library project with local NuGet packaging, without Avalonia or desktop configuration. It provides explicit provider registration, asynchronous session control, typed snapshots and isolated state notifications. Logging is optional and silent by default. A package consumer sample verifies download and reuse through public APIs.

### Upgrade notes

- **.NET 10 replaces .NET 8.** Source builds use the SDK pinned in `global.json`; library consumers require .NET 10. Self-contained desktop packages include the runtime.
- **Download paths now separate providers:** `<root>/<source>/<board>/<thread>/<media ID>.<extension>`. The default root is now `Downloads/oChan` under the user home directory; existing settings retain their configured root. Legacy files discovered through current provider metadata are imported only when size and MD5 match, leaving the originals untouched. Old completion markers alone are not trusted; interrupted transfers restart from the beginning.
- **Profiles keep their existing locations:** `%APPDATA%/.oChan` on Windows and `$HOME/.oChan` on macOS/Linux. Older saved URL lists are accepted when session restore is enabled. Only one application instance may own a profile; use an absolute `OCHAN_PROFILE` directory for a separate instance.
- **Library integration changes are breaking.** Source integrations must adopt explicit dependencies, asynchronous session operations and snapshot notifications. Desktop configuration is no longer part of the library. See the [session API](docs/Registry.md) and [package consumer sample](samples/LibMeowChan.Consumer/README.md).

### Packages and validation

Packaging targets Windows x64, Linux x64/Arm64 and macOS Intel/Apple Silicon. Release builds use a pinned SDK and reviewed dependency locks, with expanded offline tests, graphical checks and package validation. Extract the complete archive, including its native libraries and notices.

Final native acceptance of the extracted packages is still outstanding across the target matrix. macOS bundles are unsigned and not notarized; native launch and Gatekeeper validation remain open. Linux requires glibc and an X11 or XWayland desktop. The [platform validation record](docs/platform-support.md) lists prerequisites and the evidence available for each target.
