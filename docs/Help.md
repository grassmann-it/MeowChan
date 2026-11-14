# Using MeowChan

MeowChan watches imageboard boards or threads, saves selected original media,
and optionally captures posts for local reading and search. This guide follows the
**unreleased MeowChan 1.1.0 development line**. For the published 1.0.2 behavior,
use the [1.0.2 guide](https://github.com/grassmann-it/MeowChan/blob/v1.0.2/docs/Help.md)
and [release notes](releases/v1.0.2.md). Features below describe the integrated
development checkout, not a claim that 1.1.0 packages have passed release acceptance.

For the separate collection-workflow and transfer preview, see
[English](user-guide/collection-workflow-preview.en.md) /
[Deutsch](user-guide/collection-workflow-preview.md). Inspecting a transfer package
is not the same as [importing your own files](#importing-your-own-files).

The supplied payload contains **24 plugins**: **19 source-plugin entries**
(including two blocked placeholders) and **5 image filters**. It includes
**21 editable example/filter source files**; official plugins are prebuilt,
with their source code available in the repository, not the application package.
See the [packaged inventory](plugins/README.md#packaged-inventory) for the split;
the entry count is not a count of usable or enabled sources. Open **Sources & Plugins** (German: **Quellen & Plugins**)
from the app menu or footer (under **More** in compact views). Choose **All plugins**
and select a plugin to see its purpose, status and actions. See
[the shared page](#sources-and-plugins). The [plugin guide](plugins/README.md)
and [first-session recipe](plugins/using-plugins.md#first-session) explain the
supplied examples and how to add a trusted source file.

1.0 includes [search filters and saved searches](#archive-filters-and-saved-searches),
[reader groups](#reader-groups), [post bookmarks and notes](#post-bookmarks-and-notes),
[media ratings](#media-ratings), [rule templates](#rule-templates),
[shortcut customization](#customizing-shortcuts) and
[archive cleanup](#clean-up-small-archive-threads).

## Repository location and update checks

MeowChan is now hosted at [grassmann-it/MeowChan](https://github.com/grassmann-it/MeowChan).
If an older build's update check fails after the repository move, open the
[release page](https://github.com/grassmann-it/MeowChan/releases) directly. Builds
that still use the previous API address cannot follow its repository-transfer
redirect; updating the source does not change already installed applications.

When MeowChan finds a new version, a quiet notice **MeowChan … is available** appears
once per start with **Release notes**, **Later** and **Skip this version**. The same
version is not announced again during the same start, so **Later** holds until the
next start; a skipped version is not announced automatically again. The notice
stays away when you have turned information popups off. When automatic update checks are off, it
appears only after your own **Check for updates**.

Independently of the notice, the footer shows **Version … available** for as long as
a newer version is known, and the tray has the same entry. MeowChan downloads and
installs nothing: the release notes open in your browser. **Check for updates** in
the menu asks at any time and also reveals a version you skipped earlier.

For testing, `MEOWCHAN_UPDATE_SIMULATE=<version>` makes
every check answer as if that version were the latest release: nothing is requested
and nothing is written to `update-state.json`; the next start without the variable
behaves as if it never happened. `MEOWCHAN_DISABLE_UPDATE_CHECK=1` still wins.

## From a package to an offline picture

1. For the published 1.0.2 build, choose the package for your OS and CPU on the [1.0.2 release page](https://github.com/grassmann-it/MeowChan/releases/tag/v1.0.2). Extract the **whole** archive, including native libraries and notices. Start `MeowChan.exe` on Windows, `MeowChan` from a Linux archive, or the executable Linux AppImage. On macOS, open the DMG, drag `MeowChan.app` to Applications, and launch it there. Desktop packages include .NET; source builds require the pinned .NET 10 SDK. Linux requires glibc and X11/XWayland with native graphics/font dependencies. The macOS apps are ad-hoc signed and not notarized; see [macOS first-launch guidance](macos-signing.md) for manual approval in Privacy & Security. See [platform requirements](platform-support.md). The 1.1.0 development line has no published package yet.
2. Open Settings, choose your download folder and media rules, and Save. Changing the folder affects newly admitted watches; existing watches keep their assigned folders, including threads later found by an already watched board.
3. Paste a thread or board URL into Downloads and choose Add. Commas and newlines separate multiple URLs. Enter imports; Shift+Enter inserts a newline. Each URL receives its own result. Sources & Plugins lists the registered adapters and their capabilities; registration does not prove the site is currently reachable.
4. Let media download. Progress means **saved / selected media**, with rule exclusions separate. A living thread remains watched even at 100%. New watches capture posts by default; older restored watches can be media-only until capture is explicitly enabled. Pause now requests a full pause of source checks and active transfers. The Pausing state remains until cancellation has quiesced; Resume retries unfinished work.
5. Open Archive. Select the thread to inspect it, double-click its card or row to open the large reader, or right-click for **Read posts**, **Open in gallery** and **Export as HTML**. Downloads thread rows also open the reader on double-click; use Shift+Enter or the context menu for the gallery; board rows retain their details action. Opening local content never initiates a download. Saved media remain usable offline; post reading requires captured posts.

The header uses words beside the rates: **Down / Up** for network traffic and
**Read / Write** for disk traffic (German: **Download / Upload**, **Lesen / Schreiben**),
without Network/Disk headings, arrows or single-letter direction labels. (The
optional header icons described under [Statistics and disk traffic](#statistics-and-disk-traffic)
replace the words with symbols and arrows.) It can
show two graphs, each with two separate series: Down and Read are solid with a
filled area; Up and Write are dashed. The categories are not summed. **Auto**
keeps both categories available in every work view where the header layout fits; click the header rates or graphs to open
**Status → Statistics**. These are MeowChan application-payload and media-I/O measurements,
not system-wide traffic, and are separate from saved/selected media progress.

### Desktop view defaults

A new profile (an absent or empty profile folder) starts in **Simple**, so
Archive and Media are footer buttons from the first start. An existing profile
whose settings have no view mode, or whose view preference is invalid, falls
back to **Barebones**. A valid saved **Barebones**, **Simple** or **Advanced**
choice, including one recovered from a valid settings backup, remains unchanged.
Use the footer view selector to switch.

The footer groups destinations as **Work** (Downloads, Archive, Media, Booru), **Set up**
(Rules, Sources & Plugins) and **Observe** (Status), followed by
**More** (German: **Mehr**) and **Settings**. The current work view and available
width decide which destinations stay directly in the footer. **More** contains
only destinations moved out of the footer; it does not duplicate Downloads or
Settings. Advanced shows all main destinations directly when there is enough room;
a narrow window moves the extra destinations into More. The app menu uses the same
page order and groups. Booru is offered only while the source is composed and
**Show Booru** is on; it is under More in Barebones and directly available from
Simple when there is room. In German the destination order is **Downloads, Archiv,
Medien, Booru | Regeln, Quellen & Plugins | Status**, followed by **Mehr** and
**Einstellungen**. The display switch is described under
[Booru page and display switch](#booru-page-and-display-switch).

**Continue reading** (German: **Weiterlesen**) is a book icon immediately left
of Pause in the header, with a labelled entry in the app menu. The icon appears
when the feature is enabled and a saved reading position is known; it is hidden
while the reader or gallery is visible. When no icon is shown on Downloads or
the archive list, use the app-menu entry to check the reading-position state.

### Integrated work-view behavior

The integrated work-view behavior covers **WV1 Downloads**, **WV2 Archive**,
**WV3 Reader**, **WV4 Gallery**, **WV5 Media**, the **Booru** page, and the **WV6** setup pages **Rules**,
**Sources & Plugins**, plus **WV7 Diagnostics and Statistics** within **Status**, **WV8 Settings** and
**WV9 dialogs and menus**. Barebones (German: **Minimal**), Simple (**Einfach**) and
Advanced (**Erweitert**) change how much is shown directly, not whether the data
can be reached. The work view decides which settings groups and buttons are shown
directly; the rest is under **More** (**Mehr**), **More filters** (**Weitere Filter**),
**More settings** (**Weitere Einstellungen**) or **More options** (**Weitere Optionen**),
as appropriate. Shortcuts continue to work in every work view. Data, applied
filters, validation or integrity warnings, and other safety state stay reachable
on the page.

- **Downloads:** the URL intake, thread identity and progress remain available;
  list tools such as search, columns, copied URLs and details can move into **More**.
- **Archive:** search, applied filters and result state remain visible. Reduced
  filter fields use **More filters**; result actions such as layout, favorites,
  columns, HTML export, reader groups, saved searches and bookmarks remain
  reachable from the page.
- **Reader:** the way back, state messages, tabs and saved-media data stay
  available. Reader tools and media-tab actions can move into the same **More**
  menu without hiding capture, live-follow, errors or active state.
- **Gallery (WV4):** rating, media tags and slideshow controls are under More in
  Barebones and on the toolbar from Simple on; playback mode and shortcut help
  join them in Advanced. A chosen or running slideshow keeps its toggle on the
  toolbar even in Barebones. Back, favorite state, position, video transport,
  errors and integrity warnings are never reduced; the post-panel toggle stays
  directly available. Shortcuts work in every view.
- **Media:** search, filter state and the **Order** bar stay available. Refresh,
  shuffling, export and less common filters move to **More** or **More filters**;
  active filters and selections remain visible.
- **Booru:** search input, search mode, Search/Cancel, results, the watcher list
  and safety notices stay available in every work view. **Load original** and
  watcher check/pause/remove stay direct; result browser/copy actions move to
  More in Barebones. **Restart from now** and catalog usage are under More in
  Barebones and Simple and directly available in Advanced. Hiding Booru with its
  separate display switch is not a work-view reduction or a watcher pause.
- **Rules, Sources & Plugins:** the edited scope, fields and Save/Discard
  remain available in Rules. The shared Sources & Plugins page keeps source
  identity, integrity consent, observed watches and errors available in its source
  details; plugin repair, activation and draft safety stay available in its plugin
  details. Less common actions have a labelled **More** route. **Plugin details**
  (German: **Plugin-Details**) opens the second level; **Back to source**
  (**Zurück zur Quelle**) returns to the source without changing pages.
  Older **Show plugin** (German: **Plugin anzeigen**) and **Show in Sources** (German:
  **In Quellen anzeigen**) routes refer to this same shared page, not two footer destinations.
- **Status (WV7 Diagnostics and Statistics):** Diagnostics starts with its filters
  collapsed in Barebones; a non-default filter opens the group. **Check now**,
  retrying the selection and showing it in Downloads stay directly available.
  Copy moves under More in Barebones; retrying all filtered entries stays under
  More in Barebones and Simple. Statistics keeps current values, history and
  source rows visible in every view; **Reset statistics** is under More in
  Barebones and Simple, directly available in Advanced, and always asks first.
- **Settings (WV8):** Barebones keeps the core settings direct and the six extra
  groups collapsed. Simple opens interface extras, download extras, reading/media
  and behavior; storage limits and the shortcut table remain collapsed. Advanced
  opens all six groups. A changed or invalid field opens its group; the settings
  search covers every group and opens the setting it jumps to. Save/Discard,
  errors and the free-space reserve are never hidden inside those groups.
- **Dialogs and menus (WV9):** board-admission text rules start collapsed only in
  Barebones; export update/backup options start collapsed in Barebones and Simple.
  Active rules or an update in progress open the relevant section. Previews,
  confirmations, integrity consent and explicit manager dialogs are not reduced.
  The tray menu and macOS menu bar remain complete in every view.

Use **Switch work view** (German: **Arbeitsansicht wechseln**) in the app menu to
cycle Barebones → Simple → Advanced → Barebones; it has no default shortcut, but
can be rebound. Existing profiles affected by the reduction can show a one-time
notice with **Switch to Advanced** (**Zu Erweitert wechseln**) and **Got it**
(**Verstanden**). This does not silently change the saved view.

In the gallery, **Tags** and **Edit media tags…** open the quick tag panel in the
slot of the post panel instead of a dialog; it follows the post panel's work-view
row, placement and controls hold. See [Gallery tag panel](#gallery-tag-panel).
The quick-tag popover for other pages and marking several media for one change
are not available yet.

For a read-only comparison of the defaults, open Settings → **Work views**;
see [Work-view overview and switch notice](#work-view-overview-and-switch-notice).

### Language packs

In Settings, choose **Language** and Save. Alongside English and German, the app
bundles **Español, Français, Русский, 简体中文, 日本語, 한국어, Português (Brasil),
Čeština, Italiano and Polski**; every one is marked **Untested**. No separate pack
download is needed. These are AI-authored preview translations, not qualified
linguistic or native-UI reviews; missing translations fall back to English.
Catalog coverage is not complete-UI localization: some legacy and vendor controls
still use English. Linux users need suitable installed CJK fonts for the CJK packs.
See the [bundled-pack limitations](../Desktop/Localization/Packs/README.md#known-english-surfaces).

### Add results and rejected URLs

After adding a batch in Downloads, each URL has its own result. If accepted or
already-present entries are mixed with rejected ones, **Problems only (n)**
(German: **Nur Probleme (n)**) hides the non-problem rows in the report; it changes
neither the watches nor the download queue. If every row is a problem, there is
nothing to filter and this checkbox is not shown.

Whenever any URLs were rejected, **Put rejected back into the field** (German:
**Abgelehnte ins Feld**) returns those URLs to the Add input, once each, so you can
correct them and choose Add again. Invalid, unsupported, failed and skipped
entries count as rejected; **Already present** does not. If you have edited the
input since submission, that text is kept and only missing rejected URLs are
appended. This action itself adds nothing.

The interface calls ongoing observation **watch** (German: **beobachten**).
**Show details** (**Details anzeigen**) opens details in Downloads, Archive and
Plugins; it is not a command to start watching or downloading.

### Clipboard URL monitor

**Scan clipboard for URLs** in Settings → **Session and window** is off by default. Enable it
and Save to opt into passive checks, approximately every two seconds while the
app is open, restored and ready. Blocking dialogs or imports defer scanning.
Manual Add, tray import and paste actions remain available without this opt-in.
When the setting was already on at startup, the first eligible read is only a
baseline: unchanged startup text is not offered, even if sources finish loading
later. Copy different text to start ordinary detection. Explicitly saving
OFF → ON instead evaluates the current clipboard value.
A hidden or tray-running app may continue checking. Suggestions are not shown
while the main window is hidden or minimized; some reader/gallery views also
suppress them. The monitor never forces the app to the foreground.

Supported board/thread URLs that are not already watched appear in a passive
banner, not an automatically opened popup. By default, texts larger than 64 KiB
(measured as UTF-8) and more than the first 32 HTTP(S) URL candidates are not
searched, but both limits are configurable in Settings → **Storage and limits**
(**Clipboard: text searched (KiB)** and **Clipboard: URLs checked per read**).
The allowed ranges are 4–4096 KiB and 1–500 URLs. Invalid or unsupported
candidates can use the URL budget. The platform clipboard read may already have
materialized the full text before the size check. A skipped text or truncated
URL batch has an explanatory notice. Detection alone adds no watches and starts
no downloads. Click **Add URLs (Enter)**, or focus that button and press
Enter/Space, to submit the offer through the normal Add flow. Enter in the
preview or another field does not accept the suggestion. **Dismiss**, or Escape
inside the suggestion panel, rejects it. The confirmed list is the displayed
frozen batch, without a second clipboard read.

The monitor does not keep a persistent clipboard history. This is not a blanket
claim that no URL can occur in diagnostics: if optional performance diagnostics
are active, classified URL targets may be retained in that bounded in-memory
diagnostic run even before confirmation. This is not a blanket no-logging
guarantee. See the [detailed clipboard and download limits guide (German)](user-guide/clipboard-and-download-limits.md).

### Close and minimize to tray

Settings provides independent **Close to tray** and **Minimize to tray** checkboxes
(German: **Beim Schließen in den Infobereich** and **In den Infobereich minimieren**).
Close controls X; Minimize controls the minimize button. Both start off in new
profiles. With an available tray, the enabled action hides the window and removes
its taskbar entry. Click the tray icon or choose Open to restore it. Exit ends the
application even when Close to tray is enabled.

On Linux, hiding waits for a registered StatusNotifier host and this process's
registered icon. A missing or unresponsive tray keeps taskbar access. Losing the
host while hidden restores the window automatically. Existing profiles migrate
the old Minimize to tray setting to Close to tray and leave the new Minimize to
tray preference off, preserving the previous close behavior.

### Board admission and exclusions

The board-admission wizard is enabled by default. Before adding a pasted batch,
it previews catalog threads and lets you filter literal, case-insensitive text in
the title, opening post or both, then adjust individual checkboxes. Includes match
any include rule; any exclude wins, with explicit per-thread choices overriding
text rules. Preview shows reasons and counts. It uses catalog metadata, without
fetching every thread or downloading media. Cancelling cancels the whole pasted
batch before any watch is added.

Removed board threads remain durably excluded until explicitly reactivated.
Editing a board's filters affects future admissions: existing watches continue
and are not silently removed. The wizard labels existing manual/discovered
watches. **Clear this board's text rules** (German: **Textregeln dieses Boards
entfernen**) only changes the active board's draft, retaining individual
exceptions, removed-thread exclusions and explicit reactivations. Other boards
keep their rules. The preview updates locally without another catalog request,
and the dialog stays open for review and tab switching.

Choose **Apply all boards and monitor** to commit the prepared choices together.
Every board's preview must load successfully and every text rule must contain
search text; an empty catalog is valid. If a preview fails, retry its catalog or
cancel. Retrying preserves prepared rules and individual choices. There is no
partial admission from the dialog; errors during subsequent admission are still
reported separately per URL. Cancel or Escape discards all dialog changes,
including the setting for bypassing future previews, before any watch is added.
Bypassing future previews remains a separate explicit setting. Policies/exclusions
persist independently of Save URLs on exit.

### Danbooru search and watchers

Danbooru is a public, read-only post-search source, not a board source. Open
the **Booru** main page from the footer (or **More**), or use **Danbooru search
and watchers…** in the app menu to reach the same page. Its plugin must be enabled
and available under **Sources & Plugins → All plugins**. Merely opening the page sends no new search;
previously saved active watchers still follow their schedule.

#### Booru page and display switch

**English:** Booru is a main page after Media in the Work group, not a separate
window or a Status segment, and has no page-specific Back/Close button. The
footer, app menu and context-sensitive F1 help follow that main-page order;
width overflow and the current work view determine the visible footer buttons.
The default is Ctrl+6 (Command+6 on macOS); Sources & Plugins keeps Ctrl+5 and
Status keeps Ctrl+7. Only Ctrl+8 is unassigned by default. A saved Plugins binding
to Primary+6 takes precedence over the new Booru default and opens **All plugins**
on the shared Sources & Plugins page instead; F1 and tooltips show effective
bindings, not an extra default that overrides your saved choice. F1 can still list
a hidden or unavailable page binding; it does not make that destination available.

**Show Booru** in the footer and app menu is a saved, profile-wide display switch,
on by default. The switch is offered when Booru services are composed; without
that composition the switch and Booru destination are absent. Turning it off
hides the Booru destinations in the footer, More and app menu, as well as
Media → More → **Open Booru**; its page shortcut does not open the hidden page.
If Booru is selected, the shell returns to Downloads. Turning it back on makes the page
reachable again and keeps the unsent draft while the same source composition
remains. This is display only, not source authorization, plugin activation or a
watcher pause. Sources, watchers and downloads keep running under their existing
pause, cooldown and availability rules. Showing Booru does not automatically
resume a paused watcher, submit a search or bypass those rules.

**Deutsch:** **Booru** ist eine Hauptseite hinter Medien in der Gruppe Arbeit,
kein separates Fenster und kein Status-Bereich; sie hat keinen eigenen
Zurück-/Schließen-Knopf. Fußleiste, Appmenü und kontextbezogene F1-Hilfe folgen
dieser Seitenreihenfolge. Unter Minimal liegt Booru in **Mehr**, ab Einfach bei
hinreichender Breite direkt in der Fußleiste. Standard ist Strg+6 (⌘+6 auf macOS);
Quellen & Plugins bleibt auf Strg+5 und Status auf Strg+7. Nur Strg/⌘+8 ist
standardmäßig frei. Eine gespeicherte Plugins-Belegung auf Primary+6 hat Vorrang
vor dem neuen Booru-Standard und öffnet **Alle Plugins** auf Quellen & Plugins.
F1 und Tooltips zeigen die wirksamen Belegungen. F1 kann auch ein ausgeblendetes
oder nicht verfügbares Seitenkürzel weiter auflisten; das schaltet die Seite nicht frei.

**Booru anzeigen** in Fußleiste und Appmenü ist ein gespeicherter, profilweiter
Anzeigeschalter, standardmäßig an. Ohne eingebundene Booru-Dienste fehlen Schalter
und Seitenziel. Ausschalten blendet die Booru-Ziele in Fußleiste, Mehr und Appmenü
sowie Medien → Mehr → **Booru öffnen** aus; das Seitenkürzel öffnet keine ausgeblendete
Seite. Von der gewählten Booru-Seite geht es zu Downloads zurück. Wiederanzeigen
macht sie erneut erreichbar und bewahrt den ungesendeten Entwurf, solange dieselbe
Quelleneinbindung besteht. Das ist nur Anzeige, keine Quellenautorisierung,
Plugin-Aktivierung oder Suchbeobachtungspause. Quellen, Suchbeobachtungen und
Downloads laufen nach ihren bisherigen Pausen-, Cooldown- und Verfügbarkeitsregeln
weiter. Anzeigen setzt keine pausierte Suchbeobachtung automatisch fort, sendet
keine Suche und umgeht diese Regeln nicht.

#### Search and one-shot originals

Choose **Tag search**, enter up to two tags and one optional `rating:` term, and
press **Search**. Positive and negative (`-tag`) occurrences both count toward the
two-tag limit. MeowChan normalizes separators to spaces but adds no hidden terms
or automatic `g,s` filter. A search is limited to at most 5 pages of 20 posts;
use **Previous page** and **Next page**, or refine the query. The displayed sent
query belongs to those results, not to an unsubmitted edit in the input.

Choose **Post link or ID** and **Look up post** for a single Danbooru post.
Looking it up shows provider metadata and previews; it does not create a watcher
or load the original. **Load original** explicitly hands an available original
to the normal download queue. Post addresses submitted through Downloads → Add
perform that lookup and original admission as a one-shot in one action. A search
address or site address only opens and prefills the Booru page. In a batch with
several search addresses, only the last one prefills the page; the others are
reported as unused.

`safebooru.donmai.us` links refer to Danbooru, not another shipped provider. A
Safebooru search visibly prefills `rating:g` when no rating term was supplied;
you can remove it before **Search**. This is an explicit address-derived term,
not a hidden filter on ordinary Danbooru searches. A post link refers to the
same post regardless of the alias or attached search context.

A card's **Open post in browser** and **Copy link** actions use its canonical
provider post page. A watcher card offers the same two actions for the newest
post that watcher has seen. These deliberate browser actions leave the local app
and can access the provider. A post loaded from a search records that search as
where it was found; a post loaded by link or number records no search provenance.
Preview and sample variants never silently replace a missing, restricted or
withdrawn original. The displayed reason explains why **Load original** is
unavailable. Provider size/hash verification still applies. Already queued or
saved posts are not admitted as a second active download; additional search,
one-shot and watcher findings retain their provenance.

#### Saved watchers

- **Create.** Saves the explicit query. The first successful check establishes a
  baseline **from now**; it does not download the historical result list.
  Automatic checks normally run once an hour while the app and source are ready.
- **Check now.** Requests one bounded check independent of that interval. On a
  paused watcher it leaves the watcher paused and schedules nothing further.
  One check can require more than one HTTP request because of paging and bounded
  retries, including on a paused watcher. Removed watchers and provider cooldowns
  send nothing.
- **Pause and Resume.** Pause stops automatic checks for this watcher; originals
  already admitted follow the ordinary queue rules. Resume enables the schedule
  again without bypassing a cooldown. While the global session pause is on,
  automatic watcher checks do not start. An automatic check already running only
  finishes the page it is reading. **Check now** still runs one manual check,
  without unpausing the session or bypassing provider cooldowns. After Resume,
  automatic checks continue below the saved cursor; individual pauses and cooldowns
  are kept.
- **More new posts than one check covers.** Each check reads no more than 5 pages
  of 20 posts. If its budget runs out, a saved post-ID cursor continues below the
  oldest post read on a later check. A partial check is not reported as complete.
  If the remaining new posts can no longer be reached, the watcher says so and
  offers **Restart from now**. That accepts the gap: it ends the watcher without
  deleting local data and creates a new one for the same search with a fresh
  baseline. Posts in the gap are not downloaded; files already downloaded stay.
- **Remove.** Ends that watcher, but keeps downloaded media, provider metadata,
  findings and your own tags. Creating a watcher later creates a new observation.
- **Plugin disabled or quitting.** While the Danbooru plugin is disabled or not
  running, new searches, lookups, **Load original** and watcher commands are
  refused with a notice that the plugin is not available, and no watcher check
  runs. A check still in flight when the plugin is disabled is discarded and
  establishes no baseline. Originals already accepted into the download queue
  stay there under the ordinary queue rules; saved watchers and the local
  catalog remain. Enabling the plugin again restarts watcher scheduling from the
  saved catalog. Once shutdown begins, watchers stop first and new Danbooru
  requests are refused.

When an automatic check finishes after adding originals to the download queue,
MeowChan shows one information notification titled **Danbooru watcher**. It names
the watcher and the number of originals added. This follows the information-popup
setting; it is not a notification for every provider hit.

#### Local links to Media

**English:** A saved result can offer **Show in Media**; a watcher's latest post
can offer the same action when its original is saved locally. It applies the
Danbooru source and post-ID filters and focuses the matching local media tile.
This does not fetch the post or download its original. A stale saved link or a
missing/filtered-out local tile is reported rather than replaced with another
post. In Media → **More**, **Open Booru** is available when Danbooru is ready and
composed and **Show Booru** is on; it opens the Booru page without starting a search.
It uses the same main-page route as the footer and app menu, not a second window.

**Deutsch:** Ein gespeichertes Ergebnis kann **In Medien zeigen** anbieten; beim
letzten Post einer Suchbeobachtung erscheint die Aktion ebenfalls, wenn sein
Original lokal gespeichert ist. Sie setzt den Danbooru-Quellenfilter und den
Post-ID-Filter und fokussiert die passende lokale Medienkachel. Dabei wird weder
der Post abgerufen noch sein Original heruntergeladen. Ein veralteter Speicherlink
oder eine fehlende/ausgefilterte lokale Kachel wird gemeldet, nicht durch einen
anderen Post ersetzt. Unter Medien → **Mehr** öffnet **Booru öffnen** die Booru-Seite,
wenn Danbooru bereit und eingebunden sowie **Booru anzeigen** eingeschaltet ist,
ohne eine Suche zu starten. Das ist dieselbe Hauptseite wie über Fußleiste und
Appmenü, kein zweites Fenster.

#### Waiting, local data and offline use

Metadata sends start at least one second apart. HTTP 429 causes a provider-wide
pause of at least 60 seconds. The Retry-After maximum defaults to **24 hours** and
is configurable from **1 to 168 hours** in Settings as **Danbooru Retry-After
cooldown maximum (hours)**. A valid longer provider wait is capped at that setting;
missing, invalid, expired or shorter values keep the minimum. Date arithmetic
also saturates at the technical maximum representable date; this does not remove
the configured cap.

Lowering the setting can shorten an existing in-memory provider pause when the
host next checks it; it does not promise an immediate watcher check. It neither
resumes a paused watcher nor automatically replays a failed search or post lookup.
A 429 ends the current metadata job without retry. Later eligible work uses a new
job and still obeys pacing and pause state; it does not change the submitted query
or add a rating filter.

HTTP 502/503 permits at most one retry, no earlier than two seconds after the
failed response. A longer valid Retry-After is bounded by the live configured cap,
and the retry must fall strictly before the job deadline. Otherwise the job fails
with a budget notice rather than waiting beyond its lifetime. For a second 502/503
response returned to the plugin, a valid Retry-After beyond the live cap is bounded
too; it does not authorize another host retry. A metadata operation has at most 20 sends
including retries, a 120-second lifetime including waits and a 15-second deadline
per response. Metadata response limits are 2 MiB on the wire and 4 MiB decoded.
These are fixed transport safeguards, not a promise that a search always returns
100 posts.

The local Danbooru catalog of posts, findings and watchers has a configurable
write limit under Settings → **Storage and limits**. It defaults to 32 MiB and
can be set from 8 to 128 MiB. From 75 %, the watcher area of the Booru page
warns and names the setting. Lowering it does not delete data or prevent reading
an otherwise valid existing catalog; growth can be refused until you raise the
setting. Storage failures are distinct from provider cooldowns and must not be
dismissed by deleting the catalog.

After a successful original download, use **Media**, the gallery and favorites
offline. Provider, post ID/link, score, rating, source tags/categories and findings
remain available after restart. A new search or watcher check needs a connection;
a failed remote request does not erase the local library. Source tags stay separate
from your own tags and provider ratings from your local 1–5 rating.

This release line has no provider login, uploads, remote voting/tag edits,
unbounded history download, pools or HTML-scraping fallback. Only Danbooru is in
this post-search workflow. See the [Danbooru guide (German)](user-guide/danbooru.md)
for the same lifecycle in German.

### Sources and plugins

**English:** **Sources & Plugins** is one page. In **Show**, choose **Sources**,
**Image filters** or **All plugins**. Sources shows both registered sources and
source plugins that are not active yet. Select a source for its capabilities,
observed watches, integrity permission and the plugin behind it. The plugin block
shows its activation steps and next action; a plugin shared by several sources
applies to them together. **Plugin details** opens the second level for settings,
repair, testing and technical information; **Back to source** returns to the source.
In narrow windows, the list and details appear one after the other. Registration
or a plugin's session status does not prove website availability.

There is no separate Plugins footer page. The compatibility command **Show all
plugins** and previously saved plugin-navigation shortcuts open **All plugins**
on the same page. **Add plugin…** still asks you for a trusted `.cs` source file;
adding a file is not permission to activate it or bypass a required review or
restart. Image-filter access is still granted per rule scope, not by opening
this page.

**Deutsch:** **Quellen & Plugins** ist eine gemeinsame Seite. Wähle unter
**Anzeigen** zwischen **Quellen**, **Bildfilter** und **Alle Plugins**. Unter Quellen
siehst du registrierte Quellen und noch nicht aktive Quellenplugins. Wähle eine
Quelle für Fähigkeiten, Beobachtungen, Integritätserlaubnis und das zugehörige
Plugin. Der Plugin-Block zeigt Aktivierungsschritte und die nächste Aktion. Versorgt
ein Plugin mehrere Quellen, gilt seine Einstellung für sie zusammen.
**Plugin-Details** öffnet die zweite Ebene für Einstellungen, Reparatur, Tests und
technische Angaben; **Zurück zur Quelle** führt dich zur Quelle zurück. In schmalen
Fenstern folgen Liste und Details aufeinander. Registrierung und Sitzungsstatus
belegen nicht, dass die Website erreichbar ist.

Eine eigene Fußleistenseite Plugins gibt es nicht mehr. Der Kompatibilitätsbefehl
**Alle Plugins anzeigen** und früher gespeicherte Plugin-Navigationskürzel öffnen
**Alle Plugins** auf derselben Seite. Mit **Plugin hinzufügen…** wählst du weiterhin
eine vertrauenswürdige `.cs`-Quelldatei aus. Das Hinzufügen aktiviert nichts und
ersetzt weder Prüfung noch nötigen Neustart. Bildfilterzugriff erteilst du nach
wie vor je Regelbereich, nicht durch Öffnen der Seite.

### Additional sources and image filters

The eight supplied Experimental sources start disabled on first registration;
existing profiles keep their saved activation and permission choices. Adding a
matching URL offers activation. For Kohlchan, Wizchan, Endchan and Leftypol,
**Activate and allow differences** also explicitly permits provider size/MD5
mismatches for that profile and source. 2ch, Uboachan, ZZZchan and SushiChan have
strict checks and no mismatch permission. Manual **Enable plugin** grants no
exception. Review or revoke the permission in the source details of **Sources & Plugins**; retry earlier failed
media explicitly with **Retry missing media**. See the
[activation and integrity guide](plugins/using-plugins.md#experimental-sources-and-media-checks).

In **Rules**, choose an **Image filter**, set its parameters, grant the required
access for that scope and Save. The five supplied filters cover dimensions,
file-size/original-name rules, SHA-256 blocklists, animation/actual-format checks
and PNG metadata fields. One filter applies per effective rule to locally staged
files before archive admission; changing it does not re-filter saved originals.
See [filter settings and grants](plugins/filters-and-settings.md).

### Selection and shortcuts

Use mouse selection with Shift for a range and Ctrl (Command on macOS) to add or
remove individual entries. **Read posts** and Enter require exactly one selected
thread in Downloads or Archive. Shift+Enter opens the focused lead thread's gallery;
copy, removal and HTML export use the selection. Board entries open their details.
Removing a watch preserves its archive; removed board threads stay excluded from
discovery until reactivated. Disabled context actions explain why they are
unavailable; Archive also provides **Why are actions unavailable?**.

These shortcuts apply when the relevant list has focus. These are Windows/Linux defaults; Ctrl normally means Command on macOS.
The platform-specific exceptions are listed below:

| Key | Action |
| --- | --- |
| Enter / Shift+Enter | Read the single selected thread / open the lead thread's gallery |
| Alt+Enter | Show details |
| Ctrl+C / Ctrl+E | Copy selected links / export selected threads as HTML |
| Delete | Remove selected watches |
| Shift+Delete | Permanently delete selected Archive threads and their local media (confirmation required) |
| F5 / Shift+F5 | Check eligible selected watches / retry their missing media |
| Ctrl+R | Open the lead entry's **Media rules…** |
| Ctrl+T / Ctrl+Shift+T | Open the lead thread's **Tags…** in Archive or the reader / **Edit media tags…** on Media, or the tag panel in the gallery |
| Ctrl+D | Favorite or unfavorite the thread in Archive or the reader |
| Ctrl+Shift+F | Open a board's **Thread filters…**; in the Archive, **Filters and sorting** |
| Ctrl+O / Ctrl+Shift+O | Open the lead entry's folder / open its source in the browser |
| Shift+F10 or the Menu key | Open the list's context menu |
| Ctrl+A | Select all entries in the focused Downloads list, all entries on the current Archive results page, or all media |
| Alt+PageUp / Alt+PageDown | Previous / next results page, only with focus in the results list |

On the Media page, T (or **Show in thread** in the tile's menu) opens the selected
tile's post in the Archive; in the gallery, T does the same for the shown file.
After a jump from the Media page the Archive shows **Back to media**; that entry and
Alt+Left (Command+Left on macOS) return to the same Media view with its filters,
order, selection and scroll position. A tile that is no longer shown there is named
in a note, not replaced by another one. A file without a post offers no jump.

In the gallery, **S** starts or pauses the slideshow. In the gallery and on the Media page,
**I** opens the media details, **R** the rating, **Ctrl+E** the export of the
original and **Ctrl+O** the file in its folder. In the large reader, **F3** and
**Ctrl+G** find the next hit, **Shift+F3** and **Ctrl+Shift+G** the previous one.

Text fields and embedded controls retain their own keys. Across the workspace,
Ctrl+L focuses the URL input, Ctrl+, opens Settings and Ctrl+F focuses the active
page's local search field. The default page shortcuts are:

| Shortcut | Page |
| --- | --- |
| Ctrl+1 | Downloads |
| Ctrl+2 | Archive |
| Ctrl+3 | Media |
| Ctrl+6 | Booru |
| Ctrl+4 | Rules |
| Ctrl+5 | Sources & Plugins |
| Ctrl+7 | Status |

Use Command instead of Ctrl on macOS. Ctrl+8 (Command+8) is unassigned by default.
Sources & Plugins keeps slot 5, Booru uses slot 6 and Status keeps slot 7. The
removed Plugins page does not shift the digits: a saved `navigation.plugins`
binding to Primary+6 keeps priority over the new Booru default and opens All plugins
on Sources & Plugins. Diagnostics and Statistics have no default digit shortcuts;
previously saved bindings still open their respective Status segment. The table
follows the main-page order, not numeric sorting. The footer, the menus and the
F1 help use the same order. Footer and menu destinations follow availability; F1
can still list an effective binding for a hidden page, which remains unavailable.
Every footer button names its effective shortcut in its tooltip. Hiding Booru
does not give its default key back to Plugins or reassign the other pages.
Ctrl+Shift+P pauses and resumes like the header button; it remains
unavailable while a pause transition is in progress. These direct page and pause
shortcuts leave native inputs, dialogs and open overlays in control and retain
unsaved drafts. F1 shows the commands for the current context, including selection
scope and availability. Ctrl+V imports URLs only in Downloads outside native
controls; inside the URL field it pastes text normally. F11 toggles real window
fullscreen in Downloads, Archive, reader and gallery. Escape dismisses the active
menu, help or dialog first, then leaves fullscreen, and only then closes a gallery or
reader. Modal dialogs retain their own input.

If letter keys trigger actions by mistake, for example with speech input, turn off
**Standard single-key shortcuts (letters, digits, + and −)** under **Keyboard
shortcuts** in Settings. Shortcuts with Ctrl or ⌘, the F-keys, Enter, Space and the
arrow keys stay, and so do your own bindings.

### Customizing shortcuts

In 1.0 Settings → **Keyboard shortcuts** (German: **Tastenkürzel**), search for an
action and select it. Choose **Record additional shortcut**, press the desired
combination, then **Apply recording**. This adds a gesture to the Settings draft;
use **Remove** on an existing gesture when replacing it. **Clear binding** leaves
the action unbound; **Restore default** restores its supplied keys. Finish with
Settings **Save** to activate and keep the changes for this profile in every view.
**Discard draft** abandons unapplied changes.

Review any conflict before saving. **Take over conflicting gesture** deliberately
removes that gesture from conflicting actions in the draft. **Reset all known
actions** restores defaults for the known actions in the draft. Escape cancels
recording and Tab leaves the capture field. Text editing, Tab and Escape retain
their normal roles; the operating system may intercept a combination. A custom
binding still needs the action's normal context and selection. Menus, tooltips
and F1 help show the active bindings; the table above lists defaults.

### Shortcuts and the menu bar on macOS

On macOS, MeowChan has a native menu bar: the application menu with **About**,
**Settings** and **Quit**, plus **File**, **Edit** (containing **Find**), **View**,
**Go**, **Window** and **Help**. **View** lists the main pages in footer order. The
menu bar shows only shortcuts with ⌘, Control or Option; single keys belong to the
page that owns them and do not appear there. Commands that mean something different
per page (find, back, paging) are a single entry: it triggers the gesture, and focus
decides what happens.

Some defaults differ from Windows and Linux because macOS expects them that way:
⌘⌫ (the delete key above Return) removes from the watch list, ⌘⇧⌫ deletes
permanently, ⌘I shows details, ⌘⌥R checks now, ⌘⌥⇧R retries missing media, ⌘⌥← and
⌘⌥→ page, ⌘← goes back, ⌘G and ⌘⇧G find next and previous, ⌘M minimizes and ⌃⌘F
toggles fullscreen. The keyboard help opens with ⌘⇧? or ⇧? as well as F1. The
Windows and Linux keys (⌦, F5, F11 …) stay as additional bindings, so no action is
reachable only through Fn; F-keys are shown as “F5 (Fn)”. Shortcuts are displayed in
Apple notation.

### Enter and Escape in dialogs and search fields

In a search field, Escape first clears the text without closing the window, dialog
or search bar around it. A second Escape in the empty field leaves the field or
closes the search. Enter does not belong to the field: it triggers the action the
field belongs to, such as search, find next or add.

In the dialogs described below, Enter invokes the available primary action and
Escape cancels or leaves the current edit. Multiline editors keep their own keys. In the first-run setup window, Enter inside an input accepts and
Escape chooses **Later**; both apply only while the window is idle, and Escape never
accepts anything. In **Saved searches**, Enter in the name field confirms creating
or renaming while that button is available; Escape cancels the edit first and closes
the dialog only after that. In the export dialog, Enter in the destination field
starts the export; in the SHA-256 search, Enter in the hash field starts the search
as soon as the button is available. These shortcuts never bypass a disabled action or confirm a destructive operation
without its required acknowledgement.

### Back and Close at the right edge

Where a row has a way out, it stands alone at the right edge of that row, after a
gap and apart from the row's own actions, and comes last in the Tab order. This
holds for the Downloads search bar (**Close**) and details panel (**Hide details**,
German: **Details ausblenden**), the F1 help panel, the Archive reader's header
(**Back to thread list**, **Back to gallery**, **Back to media** and **Close
reader**; German: **Zurück zur Threadliste**, **Zurück zur Galerie**, **Zurück zu
Medien**, **Reader schließen**), the reader's find bar (**Close find**, **Suche
schließen**) and tag row (**Close tags**, **Tags schließen**), and the Archive
preview (**Close preview**, **Vorschau schließen**). Several of these are a drawn
cross; each keeps its name as tooltip and screen-reader name. The gallery keeps its
own placement of **Back to {target}** at the top right. Esc closes as before.
Dialogs and other rows are not moved by this layout yet.

### Rule templates

The Rules page opens on an overview of saved rules by scope, unsaved drafts and the
read-only built-in defaults (German: **Programmstandards**). **Create rule** (German:
**Regel erstellen**) guides you through Scope, Settings and Review; **Edit**
(**Bearbeiten**) opens the settings of a saved rule directly. Drafts stay on the
overview when you switch pages. After a successful **Save rule** (German: **Regel
speichern**) the page returns to the overview, shows “Rule for … saved. Applies to
new decisions.” and puts keyboard focus on the saved rule's row, or on the new row
for a new rule. If saving is refused or fails, for example because of an invalid
value, a conflicting change or a store error, you stay in the editor and see the
reason. **Discard draft** (German: **Entwurf verwerfen**) is unchanged.

In Rules, each effective media-type or size value names its own origin. **Open
rule** opens that exact Global, Source, Board or Thread scope. **Open parent
scope** moves up one scope. Unsaved drafts and conflicting edits remain available
when returning to their scope; navigation does not save or retry downloads.
Built-in defaults have no stored rule to open. Saving rules does not automatically
reconsider recorded media selections or exclusions, or delete previously saved files.

Maximum file sizes and rule values use binary units: **KiB, MiB and GiB** (1 MiB =
1,048,576 bytes). Provider sizes guide preselection; an unknown size is initially
eligible. Downloaded bytes must also stay within the effective limit for that
medium. Allowing provider size or MD5 differences for a source does not relax this
limit. Previously saved rule limits keep their stored byte value; changing the
unit label does not silently resize them.

In 1.0, open **Rule templates** in Rules. **Create from draft** opens a template
editor: name it, choose whether to include media selection, size limit and/or
image filter, then **Save template**. To reuse it, select the target rule scope
and template, choose **Preview for this scope**, inspect the before/after values,
then **Copy into draft**. Included groups replace their entire current input,
including unfinished text; other groups retain their input. Save the rule
explicitly to make the copied values active. A disabled target rule stays disabled.

**Undo template copy** restores the previous input while available; further edits
or rule/plugin changes can make it unavailable. **Discard draft** returns to the
saved rule. Editing or deleting a template does not update rules that already
received a copy. Plugin grants and runtime settings are excluded; an unavailable
or incompatible filter must be resolved before copying its template.

### Download scheduling

The default remains **1 MiB/s** and **5 parallel downloads**. The bandwidth setting
is in bytes per second after the displayed MiB/s conversion; it is not a promise
that a remote source can supply that speed. More concurrent requests do not bypass
source admission, server cooldowns, disk work or media verification.

Settings → **Media transfer resource limit** configures the safety cap
(**1–256**, default **32**). Save to apply it immediately. Effective parallelism
is the smaller of **Parallel downloads** and this cap; raise both to allow more
than 32 transfers. Higher values use more resources, and source limits and
bandwidth still apply. The Downloads summary and its tooltip distinguish
configured and effective values; the saved parallel-download setting is not
rewritten. Reducing the limit lets existing transfers drain rather than restarting
them. This cap counts active/reserved media-transfer slots and indirectly limits
transfer/buffer resource use; it is not a measured or hard byte/RAM budget, a
file-size limit or a disk-space quota. It does not limit watched threads or count
queued files. The pending queue remains bounded; it is not a count of all media
still available in watched threads.

Select one or more thread watches and use **High priority** in the context menu to
favor their next runnable files. Multiple High watches share turns; ready Normal
work still gets a turn after at most three contended High reservations. This is
whole-file scheduling, not a guaranteed byte-rate split. Running files are not
preempted, paused work stays paused, and source limits still apply. **Normal
priority** removes the boost. Priority is session-local and belongs to the exact
watch, not to any later watch with the same URL.

### Storage and limits

Settings → **Storage and limits** contains configurable catalog, collection,
cache and import limits. Sizes use binary units (1 KiB = 1024 bytes, 1 MiB =
1024 KiB, 1 GiB = 1024 MiB). **Save** applies the draft. These limits are not a
quota for all media on disk; download file-size rules and free-space reserve are
separate.

Lowering a collection or catalog write limit does not delete stored data or make
an otherwise valid existing store unreadable. New entries or growth beyond the
limit are refused. Corrupt or newer stores can still block changes independently
of the limit; do not delete their files to get past an error.

| Setting | Default | Allowed |
| --- | --- | --- |
| Free-space reserve (GiB) | 1 | 0 to 65536 |
| Danbooru catalog limit (MiB) | 32 | 8 to 128 |
| Own tag limit | 1024 | 256 to 20000 |
| Own tag file limit (MiB) | 32 | 8 to 128 |
| Favorites and ratings: entries per list | 50000 | 10000 to 500000 |
| Favorites and ratings: file size (MiB) | 8 | 1 to 128 |
| Post bookmarks | 10000 | 1000 to 100000 |
| Saved searches | 500 | 50 to 5000 |
| Reader groups | 500 | 50 to 5000 |
| Post filter rules | 200 | 20 to 2000 |
| Individually hidden posts | 10000 | 1000 to 100000 |
| Archive tag filters per list | 32 | 8 to 128 |
| URL text export size (MiB) | 1 | 1 to 64 |
| Slash-tag suggestions | 8 | 3 to 20 |
| Video scrub preview cache size (MiB) | 128 | 8 to 4096 |
| Sources page: watch rows per source | 8 | 3 to 200 |
| Ask before “Export all matches” above (files) | 500 | 10 to 100000 |
| Ask before “Export all matches” above (GiB) | 2 | 1 to 1024 |
| Video preview cache size (MiB) | 512 | 16 to 8192 |
| Video preview cache: files | 2048 | 32 to 50000 |
| Gallery image cache (MiB) | 256 | 0 to 4096 |
| Gallery images decoded ahead | 3 | 0 to 20 |
| Gallery images decoded behind | 2 | 0 to 20 |
| Sound check cache: size (MiB) | 64 | 16 to 128 |
| Sound check cache: videos | 400000 | 50000 to 1000000 |
| Media previews kept in memory | 384 | 32 to 8192 |
| Media index cache limit (MiB) | 256 | 16 to 1024 |
| Media index cache: entries | 1000000 | 100000 to 5000000 |
| Media index cache: threads | 200000 | 20000 to 1000000 |
| Clipboard: URLs checked per read | 32 | 1 to 500 |
| Clipboard: text searched (KiB) | 64 | 4 to 4096 |
| Import: files per batch | 2000 | 1 to 20000 |
| Import: size per batch (GiB) | 50 | 1 to 16384 |
| Import: largest single file (MiB) | 4096 | 1 to 1048576 |
| Import: files in total | 50000 | 1 to 500000 |
| Import: number of batches | 100000 | 1000 to 1000000 |
| Import: tags per batch | 32 | 1 to 256 |
| Import: catalog file size (MiB) | 32 | 1 to 128 |
| Tag suggestions shown | 9 | 3 to 18 |
| Recently used tags remembered | 50 | 0 to 500 |
| Tag undo steps | 20 | 1 to 100 |
| Neighbouring media checked for tags | 2000 | 100 to 20000 |
| File name words used for tags | 8 | 0 to 32 |
| Source tags per medium | 24 | 0 to 200 |
| Media marked for tagging | 500 | 10 to 5000 |

At 75 % utilization, the relevant status line warns for favorites, thread
favorites, ratings, the Danbooru catalog or the saved media index. The message
names the setting that can be raised.

**English (additional limits):** **Archive tag filters per list** bounds each
include/exclude list independently; lowering it does not truncate stored filters.
**URL text export size (MiB)** limits new URL text output, not HTML or original-file
exports: it is the writer budget, not a larger file format. The URL text format
still has a fixed **1 MiB** ceiling, **4096 lines** and **8192 UTF-16 code units per
line**; the current archive plan exports exactly one thread page URL. Raising the
writer budget to 64 MiB does not enable a 64-MiB URL export. Separate identity and
target-path checks still apply. See the [settings and export limits reference
(German)](user-guide/settings-export-and-limits.md).
**Slash-tag suggestions** bounds detected slash-tag suggestions, not the number of
tags you can store or the separate quick-tag chip count. **Video scrub preview
cache size (MiB)** budgets derived timeline previews on disk, not original videos.
The table gives their defaults and minimum/maximum values from Config.

**Deutsch (zusätzliche Grenzen):** **Archiv-Tagfilter pro Liste** begrenzt jede
Einschluss-/Ausschlussliste einzeln (Standard 32, 8 bis 128); ein niedrigerer Wert
kürzt gespeicherte Filter nicht. **Größe des URL-Textexports (MiB)** begrenzt neue
URL-Textausgaben (Standard 1 MiB, 1 bis 64 MiB), nicht HTML- oder Originaldatei-
Exporte; zusätzliche Schutzgrenzen je Eintrag und Format bleiben bestehen.
Der Wert ist das Writerbudget, keine Erweiterung des Dateiformats: Das URL-Textformat
bleibt auf **1 MiB**, **4096 Zeilen** und **8192 UTF-16-Codeeinheiten je Zeile**
begrenzt. Der heutige Archivplan exportiert genau eine Thread-Seiten-URL.
64 MiB Writerbudget ermöglichen keinen 64-MiB-URL-Export; Identitäts- und
Zielpfadprüfungen gelten weiterhin. Details findest du in der [Referenz zu
Einstellungen und Exportgrenzen](user-guide/settings-export-and-limits.md).
**Slash-Tagvorschläge** begrenzt erkannte Slash-Tagvorschläge (Standard 8, 3 bis 20),
nicht deine gespeicherten Tags oder die separate Zahl der Schnell-Taggen-Chips.
**Größe des Video-Scrub-Vorschau-Caches (MiB)** begrenzt abgeleitete Zeitleisten-
Vorschauen auf dem Datenträger (Standard 128 MiB, 8 bis 4096 MiB), nicht Originalvideos.
Alle Größen sind binär: 1 KiB = 1024 Byte, 1 MiB = 1024 KiB.

The last seven rows form the **Quick tagging** group (German: **Schnell-Taggen**).
They size the [gallery tag panel](#gallery-tag-panel): chips, remembered recent
tags, undo steps, the neighbouring media and file name words used to rank
suggestions, and offered source tags. A value of 0 switches off recent tags, file
name words or source tags where the range allows it. **Save** applies a new value
to panels you open afterwards; an already open panel keeps the limits it opened
with. The one exception is **Recently used tags remembered**: a lower saved value,
including 0, trims the remembered list at once, keeping the newest entries. **Media marked for tagging** is stored for the later marking feature and has
no effect yet; its tag-store write limit stays fixed and separate.

Cache and confirmation limits behave differently from collection limits. A full
video-preview cache evicts old **derived previews**, never saved originals; the
in-memory thumbnail cache drops least-recently-used previews. If a media index is
too large to save, Media rebuilds it after a restart. The **Export all matches…**
thresholds ask before a large copy; confirming does not silently truncate its
selection, but missing files, errors or cancellation can still prevent copies.

The sound-check cache remembers whether a video has sound for the Media filter.
Both its byte and entry limits apply. When it cannot keep every derived answer,
the rest is checked again when needed; lowering it does not delete video files.

Some fixed technical limits protect individual records and parsers: bookmark
notes hold at most 8000 Unicode codepoints; reader groups have at most 1000
threads each and 20000 memberships in total. Bookmark, saved-search and
reader-group files are bounded at 32, 8 and 16 MiB respectively. The post-filter
file has a fixed 32 MiB ceiling. Favorites, thread favorites and ratings are read up to 500000 entries and 128 MiB. The
Danbooru, own-tag and import catalogs are read up to 128 MiB even when the
configured write limit is lower; the saved media index is read up to 1 GiB.
A video poster is at most 4 MiB and is generated for videos up to 512 MiB.
A selected original export (favorites or selection) is bounded at 50000 entries.
The gallery's image/GIF decoder limits remain separate, as described below.

### Settings and session messages

Settings shows one category at a time, with its navigation beside the content.
In narrow windows, open **Sections**, select a category and choose **Go to section**.
Switching categories keeps all drafts, including invalid text. **Save** applies
changes across every category; **Discard draft** reverts all unapplied edits.
Saving with an invalid field opens its category so you can correct it.

The search at the top (Ctrl+F, Command+F on macOS) finds settings by name, hint or
key, including English terms in other languages. Results update as you type.
Up/Down selects a hit; Enter or a click jumps to it and opens its group. A screen
reader can receive the current hit's position and name, or the empty-result notice.

Under **Notifications** (German: **Benachrichtigungen**), independently enable or
disable **Success and information popups**, **Warning popups** and **Error popups**,
then Save. These control in-app popups. Warnings and errors remain at the affected
action and in **Status → Diagnostics → Messages this session**. Select an entry to inspect
or copy its details. **Clear session messages** clears that list; it does not fix
the underlying problem. The list ends with the app session. Field validation and
confirmation dialogs still apply when popups are disabled.

**Unreleased source-build behavior:** the central banner and each popup have an
explicit **×**. Closing one acknowledges that occurrence for the current app
session, suppresses matching queued/repeated displays and leaves its diagnostic
entry/count intact. Another source or failure still has its own message. Clearing
Status → Diagnostics does not re-enable acknowledged messages. Passive popup expiry does
not acknowledge a failure. Unstructured provider errors still distinguish their
exact diagnostic text; changed error details can represent a separate occurrence.

### Resetting and exporting settings

**English:** Use **Reset** beside one of the seven editable section headings
(Interface, Reading & media, Downloads, Storage and limits, Updates,
Notifications, Session and window), or **Reset all editable settings** at the top. Confirm
with **Reset draft** or choose **Cancel**. Reset changes only the unsaved draft;
it does not save settings, delete local data or revoke/grant source permissions.
You still need **Save** to apply the defaults; **Discard draft** returns to your
saved settings instead. Shortcut bindings have their own reset in the shortcut
editor and are not changed by these section/all-settings reset actions.

Under **More settings actions**, choose **Export saved settings…** and a destination.
The suggested filename is `meowchan-settings-yyyy-MM-dd.json`. The export uses the
saved settings, even if another category has an invalid or unsaved draft. It does
not save, discard or change that draft. The JSON includes `SchemaVersion` and
`ExportedBy` but omits window geometry, first-run/notice state, integrity consent,
session pause, the last export/frame directories and last-used Status segment.
Download paths and shortcut bindings are included: review the file before sharing
it. This is not an archive, plugin, credential or whole-profile backup, and there
is no settings-import UI yet. Canceling the picker leaves the draft alone; an
export error is shown on the settings page without saving the draft.

**Deutsch:** Nutze **Zurücksetzen** neben einer der sieben bearbeitbaren
Abschnittsüberschriften (Oberfläche, Lesen & Medien, Downloads, Speicher und Grenzen,
Updates, Benachrichtigungen, Sitzung und Fenster) oder oben **Alle bearbeitbaren Einstellungen
zurücksetzen**. Bestätige mit **Entwurf zurücksetzen** oder wähle **Abbrechen**.
Zurücksetzen ändert nur den ungespeicherten Entwurf. Es speichert keine Einstellungen,
löscht keine lokalen Daten und ändert keine Quellenerlaubnis. Erst **Speichern**
übernimmt die Standards; **Entwurf verwerfen** kehrt dagegen zu deinen gespeicherten
Werten zurück. Tastenkürzel setzt du separat im Kürzel-Editor zurück; die
Abschnitts-/Gesamtaktionen ändern sie nicht.

Wähle unter **Weitere Einstellungsaktionen** den Eintrag **Gespeicherte Einstellungen
exportieren…** und ein Ziel. Der Dateivorschlag lautet
`meowchan-settings-yyyy-MM-dd.json`. Exportiert werden die gespeicherten Einstellungen,
auch bei einem ungültigen oder ungespeicherten Entwurf in einer anderen Kategorie.
Der Entwurf wird weder gespeichert noch verworfen oder verändert. Die JSON-Datei
enthält `SchemaVersion` und `ExportedBy`, aber keine Fenstergeometrie,
Ersteinrichtungs-/Hinweiszustände, Integritätserlaubnisse, Sitzungspause, zuletzt
benutzten Export-/Einzelbildordner oder zuletzt geöffneten Status-Bereich.
Downloadpfade und Tastenkürzel sind enthalten: Prüfe die Datei vor dem Weitergeben.
Sie ersetzt keine Archiv-, Plugin-, Zugangsdaten- oder vollständige Profilsicherung.
Eine Oberfläche zum Importieren von Einstellungen gibt es noch nicht. Brichst du
die Dateiauswahl ab oder schlägt der Export fehl, bleibt dein Entwurf unverändert;
ein Exportfehler wird auf der Einstellungsseite angezeigt.

### Work-view overview and switch notice

**English:** Open **Settings → Work views** for a read-only comparison of
Barebones, Simple and Advanced. Filter by **Page** and **Find an element**; the
active column is marked. Cells distinguish **Direct**, **Under More**, **Collapsed
group**, **Hidden; via …** and **Always present**. Safety/required rows explain
why they cannot be hidden by a view. Column defaults distinguish **On**, **Off
(selectable)** and **Not offered**. These are policy defaults, not a live visibility
inventory: saved column choices, active filters and errors may override them.
The overview does not edit your view or draft and is not a custom-view editor.

After an applied view switch, a **Work view changed** toast summarizes changed
defaults and column defaults. **Undo** restores the previous view, not unrelated
settings or data; a newer switch invalidates the older notice's actions. The
notice requires information popups and an available session. Under **Settings →
Notifications**, **Work-view switch notice (seconds)** (`WorkViewSwitchNoticeSeconds`)
defaults to 8 seconds, allows 0 to 60, and 0 disables the toast including Undo/Details.
The duration stays in the draft until **Save**; **Discard draft** restores the saved
value, and resetting Notifications loads the default into the draft.

**Details** opens **Settings → Work views** through the normal protected Settings
route, without saving or discarding your existing settings draft. While the session
is restoring, closing or busy, the route cannot open. The overview shows only the
policy rows changed by that switch, across all pages; its active column follows the
currently applied view, not an unsaved view choice or a forced return to the old
view. Keyboard focus moves to **Find an element** (or the table if the field is
unavailable). Page/text filters narrow the changed rows further. **Clear filters**
removes the difference restriction too; entering Work views normally also clears
the previous difference, page and text filters for the full comparison. The one-time
notice for older profiles is separate from this switch toast.

| Setting | Default | Allowed |
| --- | --- | --- |
| Work-view switch notice (seconds) | 8 | 0 to 60 |

**Shrink the window when switching to Minimal** is on by default under Interface.
Turn it off and Save to keep the current size when switching to Barebones/Minimal.
Changing the unsaved view selection alone does not resize the window.

**Deutsch:** Öffne **Einstellungen → Arbeitsansichten** für den schreibgeschützten
Vergleich von Minimal, Einfach und Erweitert. Filtere nach **Seite** und **Element
suchen**; die aktive Spalte ist markiert. Zellen unterscheiden **Direkt**, **Unter
Mehr**, **Gruppe eingeklappt**, **Ausgeblendet; über …** und **Immer vorhanden**.
Sicherheits-/Pflichtzeilen erklären, warum die Ansicht sie nicht ausblenden darf.
Spaltenvorgaben unterscheiden **An**, **Aus (wählbar)** und **Nicht angeboten**.
Das sind Vorgaben, keine Liste der gerade sichtbaren Controls: Gespeicherte
Spaltenwahlen, aktive Filter und Fehler können davon abweichen. Du änderst hier
weder Ansicht noch Entwurf; eine eigene Ansicht kannst du damit nicht erstellen.

Nach einem angewandten Ansichtswechsel fasst **Arbeitsansicht gewechselt** geänderte
Vorgaben und Spaltenvorgaben zusammen. **Rückgängig** stellt die vorige Ansicht
wieder her, nicht andere Einstellungen oder Daten. Ein neuer Wechsel macht die
Aktionen des alten Hinweises ungültig. Der Hinweis braucht Informations-Popups und
eine verfügbare Sitzung. Unter **Einstellungen → Benachrichtigungen** findest du
**Hinweis beim Ansichtswechsel (Sekunden)** (`WorkViewSwitchNoticeSeconds`):
Standard 8 Sekunden und den Bereich 0 bis 60; 0 schaltet den Toast einschließlich
Rückgängig/Details aus. Die Dauer bleibt bis **Speichern** im Entwurf. Mit **Entwurf
verwerfen** holst du den gespeicherten Wert zurück; Zurücksetzen der
Benachrichtigungen lädt den Standard in den Entwurf.

**Details** öffnet **Einstellungen → Arbeitsansichten** über den normalen geschützten
Einstellungsweg, ohne deinen vorhandenen Einstellungsentwurf zu speichern oder zu
verwerfen. Während Wiederherstellung, Schließen oder einer laufenden geschützten
Aktion öffnet sich dieser Weg nicht. Die Übersicht zeigt nur die durch diesen
Wechsel geänderten Policy-Zeilen über alle Seiten. Die aktive Spalte folgt der
aktuell angewandten Ansicht, nicht einer ungespeicherten Ansichtswahl; sie schaltet
nicht zur alten Ansicht zurück. Der Tastaturfokus geht zu **Element suchen** (oder
zur Tabelle, falls das Feld nicht verfügbar ist). Seiten-/Textfilter schränken die
geänderten Zeilen weiter ein. **Filter zurücksetzen** entfernt auch die
Differenzeinschränkung. Öffnest du Arbeitsansichten normal, werden die vorherigen
Differenz-, Seiten- und Textfilter für den vollständigen Vergleich gelöscht. Der
Einmalhinweis für ältere Profile ist vom Wechsel-Toast unabhängig.

**Fenster beim Wechsel zu Minimal verkleinern** ist unter Oberfläche standardmäßig
an. Schalte es aus und speichere, wenn du beim Wechsel die Fenstergröße behalten
möchtest. Die ungespeicherte Ansichtsauswahl allein ändert die Fenstergröße nicht.

### Status page

**English:** **Status** combines **Statistics** and **Diagnostics** on one page.
The shared status line counts active transfers, waiting transfers and current
problems; while sampling is active it also shows download and disk-write rates.
Without fresh sampling it omits the rates, rather than presenting stale numbers.
The Diagnostics tab names the current problem count. The footer button and
Ctrl+7 (Command+7 on macOS) open the last-used segment; the header graph and tray
open Statistics. A source's Diagnostics action opens Status → Diagnostics with
that source selected. Recovery failure opens Diagnostics; Statistics becomes
available after session recovery. You can assign each segment a shortcut, and
previously saved segment shortcuts keep working. Booru is a separate main page,
not a Status segment: it uses slot 6, Sources & Plugins keeps slot 5 and Status
keeps slot 7. Slot 8 is unassigned by default. For Booru visibility and the saved
Plugins shortcut override, see [Booru page and display switch](#booru-page-and-display-switch).

**Deutsch:** **Status** vereint **Statistik** und **Diagnose** auf einer Seite.
Die gemeinsame Statuszeile zählt aktive und wartende Übertragungen und aktuelle
Probleme. Solange gemessen wird, zeigt sie auch Download- und Schreibrate; ohne
frische Messung lässt sie die Raten weg. Der Diagnose-Reiter nennt die Zahl der
aktuellen Probleme. Der Fußleistenknopf und Strg+7 (⌘+7 auf macOS) öffnen den zuletzt
benutzten Bereich; Kopfleisten-Graph und Tray öffnen Statistik. Die Diagnose-Aktion
einer Quelle öffnet Status → Diagnose mit dieser Quelle als Filter. Nach einer
fehlgeschlagenen Wiederherstellung öffnet sich Diagnose; Statistik ist nach der
Sitzungswiederherstellung verfügbar. Du kannst jedem Bereich ein Kürzel zuweisen;
früher gespeicherte Bereichskürzel funktionieren weiter. Booru ist eine eigene
Hauptseite, kein Status-Bereich: Booru belegt Strg/⌘+6, Quellen & Plugins bleibt
auf Strg/⌘+5 und Status auf Strg/⌘+7. Nur Strg/⌘+8 ist standardmäßig frei. Der
[Booru-Anzeigeschalter und der gespeicherte Plugins-Kürzelvorrang](#booru-page-and-display-switch)
sind davon getrennt; der Schalter pausiert keine Suchbeobachtungen.

### Statistics and disk traffic

Open **Status → Statistics** from **More** or the app
menu, or by clicking the header graph. Ctrl+7 (Command+7 on macOS) opens Status
on its last-used segment; Ctrl+8 is no longer assigned by default. It shows measured Network I/O and Disk I/O,
history and rows per source. These are MeowChan's own application-payload and
media-I/O counters, not operating-system totals. **Network I/O (`NetworkIo`)** keeps
Down and Up as separate directions; **Disk I/O (`DiskIo`)** keeps Read and Write
separate as well. Each category has its own scale, and retries or failed transfers
can contribute bytes without a saved file. The header uses the same directional
categories; **Auto** keeps both available in every work view where the layout fits.
These counters are separate from saved/selected media progress. The header uses a word/rate matrix: **Down** then **Up** for network traffic,
**Read** then **Write** for disk traffic;
the directions are never combined. Read is the solid, filled main disk series;
Write is the dashed second series, not a combined disk total. **Reset statistics**
is under More in Barebones and Simple and directly available in Advanced. It asks
for confirmation before clearing the counters, not media.

Rates use at most three digits with adaptive precision, for example **127 KiB/s**,
**1.27 MiB/s**, or **12.7 MiB/s** (decimal commas in languages that use them).
Units stay binary; values move to the next unit before the number needs four digits,
so a rate that would round to 1000 reads one unit higher (1000 B/s becomes **0.98 KiB/s**).
Below 1 KiB/s whole bytes are shown. This compact rate formatting also applies to the
statistics page and tray; it does not change file-size totals or the underlying
measurements.

**Use icons for header I/O labels** (German: **Symbole für I/O-Werte in der
Kopfleiste**) in **Settings → Session and window → Statistics** (German:
**Sitzung und Fenster → Statistik**) is optional and off by default. It replaces
the direction words with network/disk symbols and arrows in the corresponding
graph colours; Read keeps the solid line and Write the dashed one. Hovering a direction shows
its full name, current rate, and whether its series is solid or dashed. Clicking
still opens Status → Statistics; the button keeps its full screen-reader description.
This option also works when the graphs are hidden. Turning it off restores the words.

Settings → **Session and window → Statistics** controls the sampling and persistence:

| Setting | Default | Allowed |
| --- | --- | --- |
| Statistics update interval (milliseconds) | 1000 | 500 to 10000 |
| Graph length (readings) | 120 | 30 to 3600 |
| Update interval in the tray (milliseconds) | 5000 | 2000 to 60000 |
| Days of statistics kept | 30 | 1 to 365 |
| Statistics saving interval (minutes) | 5 | 1 to 60 |
| Most sources in the statistics | 64 | 8 to 256 |

At the defaults, 120 readings at one second cover two minutes. When the window is
hidden, sampling needs active downloads and the statistics tooltip option; hidden
and idle does not run the sampling timer. The tray's statistics tooltip and its
two menu lines can each be disabled. Like the header, the tray reports MeowChan's
own I/O counters rather than operating-system totals. The menu lines open Statistics.

**Keep statistics on this computer** is on by default. It saves daily totals in
`io-stats.json` in the profile, at the configured interval when changed and at
exit. With it off, totals last for the session and an existing file remains
untouched. Retention includes today; older days are pruned on load and at midnight.
Further sources beyond the row limit are grouped as **Other sources**.
**Header graph → Auto** keeps both **Network I/O (`NetworkIo`)** and **Disk I/O
(`DiskIo`)** available in every work view where the layout fits. Down and Up remain
separate network directions; Read and Write remain separate disk directions. An
explicit layout can select Network, Disk or text without summing the categories or
claiming system-wide traffic. The visible header keeps **Down / Up** and **Read / Write** in solid-series / dashed-series order, with no Network or Disk headings, even when the graph layout
changes. The Statistics page keeps its directional histories regardless of this
setting.
These controls do not alter
provider limits, download rules or the saved-media progress count.

### Animations

The **Animations** setting decides whether short fades, hover transitions, chip and
overlay changes and the motion of notifications are played. **Follow system** uses
your operating system's reduced-motion setting, **On** always plays them, **Off**
shows everything immediately. Progress bars and loading indicators keep running in
every case. The environment variables `MEOWCHAN_MOTION=off` or `on` and
`MEOWCHAN_REDUCED_MOTION=1` or `0` take precedence over the setting.

### Accessibility

For screen readers, page titles are level-1 headings and section titles level-2
headings, so you can jump from heading to heading.

A hidden spoiler is announced only as **Spoiler, hidden**; its real text is read
only after you reveal it. Tiles on the Media page carry named values rather than
only a symbol: the media kind (**GIF**, **Video**, **File**), **Not saved**,
**No post** or **No media tags yet**; a rating is announced as “term: value”.
Dialog error strips expose live announcements to accessibility clients.

Keyboard focus is drawn as two rings, a colored one outside and one in the
background color inside, to help keep it distinguishable in both themes.
Selected chips, tiles and the chosen category in Settings are also recognizable
by shape, not only by color: chips get an outline, tiles a drawn check mark, the
category a bar.

These semantics do not establish screen-reader compatibility on every platform;
Windows, macOS and Linux accessibility acceptance remain separate checks.

## Linux packages and startup

For either `linux-x64` or `linux-arm64`, choose a package from the [1.0.2 release](https://github.com/grassmann-it/MeowChan/releases/tag/v1.0.2):

Release download names carry numeric prefixes. The archive suffixes below apply to either CPU; AppImage commands use the actual public x64 filename.

- `MeowChan-1.0.2-<RID>-bundled-vlc.tar.gz` suffix: VLC and .NET included; extract everything and run `./MeowChan`.
- `MeowChan-1.0.2-<RID>-system-vlc.tar.gz` suffix: .NET included; install **LibVLC 3, at least 3.0.20**, with Matroska/WebM, VP8/VP9, Vorbis/Opus and audio/video output plugins through your distribution first.
- `.AppImage`: VLC and .NET included. On x64, run `chmod +x 04-MeowChan-1.0.2-linux-x64.AppImage`, then `./04-MeowChan-1.0.2-linux-x64.AppImage`. If FUSE is unavailable, use `./04-MeowChan-1.0.2-linux-x64.AppImage --appimage-extract-and-run`. On ARM64, use `05-MeowChan-1.0.2-linux-arm64.AppImage` instead.

Bundled archives and AppImages avoid a separate VLC installation. Linux still needs a compatible glibc desktop with X11/XWayland and native graphics/font dependencies. Before any session starts, the application checks the selected VLC backend. Missing, unsupported or unusable system VLC opens a dismissible installation hint; closing it exits with code **2**. Install a supported VLC 3 runtime and plugins, then start again. On Debian-derived distributions, relevant package families include `libvlc5`, `libvlccore9`, `vlc-plugin-base` and `vlc-plugin-video-output`; distribution versions must meet the minimum. Development-package symlinks are not required.

A damaged bundled runtime or invalid package metadata shows a package repair hint instead: re-extract the complete archive or obtain a fresh AppImage. Bundled packages never silently switch to system VLC. Corrupt, unreadable or unsupported media files remain individual preview/playback errors and leave their originals intact.

Desktop packages contain the application, runtime, icon and legal notices. This guide, screenshots, release notes and developer documentation stay on GitHub; they are not extra files to look for inside the download.

## Images, search and navigation

The [interface screenshots](screenshots/README.md) show the 0.9.0 workspace,
including Barebones and Advanced, the reader and gallery. Capture checkpoints,
provenance and limits are recorded with the images.

Choose **Read posts** to open the large thread reader across the work area, with
readable posts and embedded media. Up/Down moves exactly one post. Left/Right
moves through the frozen current thread list/search in its sort order, across
pages, without wrapping. A single thread opened without that scope has no global
thread sequence. Input fields keep their native arrow-key behavior.

The lower-right navigation cross is an **auto-hiding overlay**. It reserves no
side lane, does not shorten the viewport and adds no extra scrollable tail after
the final post. It fades out while the pointer is idle, and reappears on pointer
movement over the reader or briefly after a navigation shortcut. It stays visible
while the pointer is over or near it, or it has keyboard focus. Pagination and
the scrollbar remain usable; hiding the cross does not change the content layout.

In selected post content, **Enter or Space** opens the first available image or video
in attachment order. Text selections, editors, buttons, links and dialogs keep their
native keys. Explicit Reader or application shortcut bindings take precedence.
The configurable **Open in gallery** command remains separate.

Newly captured posts leave the open reader unchanged. A **Show new posts** notice
lets you load them and jump to the first newly appended post. **Follow live** is
an explicit choice for a currently watched thread with post capture enabled; it
follows newly saved posts to the end without starting any network work. It waits
while a gallery or export dialog is open. Changing threads or removing the watch
turns following off. Repeated source polls without new posts show no notice.

Settings → **Reading & media → Post width** selects **Fit to content** (the
default), **Centered column** or **Full width**. Fit makes each card only as wide
as its text and pictures need, up to the reading width; Centered column uses the
reading-width column, and Full width uses the available reader width. Saving the
choice applies it to the open reader.

The reader reserves space for previews so loading images do not keep shifting posts. Refreshing the same thread
retains the current reading position. If a preview cannot finish loading, it reports “The preview could not be loaded.”
instead of remaining at Loading.

Use **View** in the reader toolbar to switch on **Hide media** or **Only posts with media** for this session. The
switches apply to every thread in the reader. A deliberate jump to a hidden post (for example through a quote, find,
bookmark or reading position) shows it temporarily; **Hide again** hides it once more. **Show as conversation** places
replies beneath the most recent eligible earlier quoted post, with indentation; the opening post stays first and the separate reply
counter is omitted in this mode. A reply is nested only when its earlier non-opening-post quotes belong to one
ancestor chain; quotes across unrelated branches stay at the root level. Quotes of the opening post or later posts
do not choose a parent. The conversation switch is session-only. **Maximum conversation indentation depth** in
Settings controls indentation, not which posts are kept: deeper replies stay visible at the last indentation level.
This only reorders locally captured posts for display; it is not a provider conversation function and sends no requests.

The app header and window controls remain available above the large reader
and gallery.
The gallery uses the window's remaining work area. Left/Right or its navigation buttons
move between media. Use Fit, 1:1, +/− or wheel/touchpad gestures to zoom.
Fractional touchpad deltas produce finer zoom changes. Zooming out stops at Fit;
the image is then centered and pan resets. Drag a zoomed image to pan. Double-click the image to switch Fit/1:1. Escape closes the gallery.
The gallery keeps decoded images in memory and prepares the next three and previous two images in the background, so
moving either direction can show them immediately. The cache is 256 MiB by default; set it to 0 to keep nothing.
**Gallery image cache**, **Gallery images decoded ahead** and **Gallery images decoded behind** are under Settings →
**Storage and limits**; their allowed ranges are 0–4096 MiB, 0–20 and 0–20.
Use the gallery's optional **media focus** view to fold away tools and details
and give the current medium more room. Navigation remains available; restore the
controls when you want to use the gallery tools or read the saved-post context.
This changes the gallery layout independently of F11 window fullscreen. Close the
gallery with Esc or **Back to {target}** (German: **Zurück zu {Ziel}**) alone at the
top right, with the favorite star directly to its left; the button names the
return destination. The toolbar keeps Previous, position, Next, Fit, 1:1, the post
panel and Tools available. Rating, media tags and slideshow controls move from
**⋯ (More gallery actions)** to the toolbar in Simple; Advanced also brings
playback mode and shortcut help onto the bar. Zoom steps, original export, saved
ratings, frame saving, video rules, overlays and fullscreen stay under More in
every view. Their shortcuts still work directly. In a narrow window the
toolbar uses icons and the post panel floats over the image.

**Unreleased source-build controls:** Settings → Reading & media → **Gallery &
playback** offers **Manual**, **Repeat current video**, and **Slideshow**. The same
mode is available in the gallery. Repeat applies to each video you select until
changed; its choice and media focus are remembered for the profile. Slideshow
must be started explicitly, even after reopening the gallery or restarting the
app. Images remain for the chosen duration (1–300 seconds, initially 5). Under
**Videos in the slideshow**, choose one pass, a minimum duration (initially 10
seconds, 1–600) or a repeat count (initially 2, 1–50). The pass that crosses the
minimum duration finishes before advancing; with reduced motion videos play once.
You can always move on manually. The last item finishes and stops. Explicit Pause
stops progression until you resume. Seeking, dialogs and temporary loss of active
interaction hold automatic advancement while the interaction is unsafe; they do
not pause the native video. Reading a hover-opened post stops slideshow progression
until you explicitly start it again. Decode or integrity failures stop progress
rather than silently skipping files. Video autoplay also applies to the first or
manually selected video with saved Slideshow mode, without starting automatic next.
Seeking and volume changes preserve whether the video was playing or paused.

**Auto-hide controls overlay** is one paired option, independent of media focus.
It reveals controls from the bottom edge and collapses them after the pointer
leaves their panel. The former separate auto-hide command remains a compatibility
alias for the same choice. Moving in the middle of the picture does not reopen
panels: it only briefly shows subtle bottom/right chevrons. These hints have no
box or border and disappear after mouse inactivity. Navigation shortcuts work
on the first press even while controls are hidden. A pointer click on
Start does not pin the controls; keyboard focus, active gestures and menus do.
Keyboard access remains available. **Hide controls after** sets the idle delay
for the gallery overlay and reader navigation cross (initially 2000 ms, 500–30000).
The post panel and shortcut help close with their top-right × or Esc; Esc closes
only the topmost layer first. **Post overlay** reveals at the right edge,
stays open while you read inside it and collapses after leaving; hover does not
save a new preference. It pauses slideshow without automatic resume when closed.
An explicitly opened/pinned context retains its normal toggle behavior. Its
background is translucent and its text stays opaque. Unsupported native-video
transparency uses the docked layout and explains the fallback. Existing profiles
start docked; later choices are remembered. F11 remains a separate window action.

Show context to read the associated saved post and available local references.
A media-only item can have no captured post. In the gallery, **P** toggles saved-post
context, **0** selects Fit, **1** selects 1:1, **F** toggles the current media favorite,
and F1 or ? opens keyboard help. **M** picks the current medium (**Add favorite**);
**U** unpicks it (**Remove favorite**). These set the mark explicitly rather than
toggling it. The upper-right **★ / ☆** indicator shows favorite / not favorite;
**…** indicates a pending save. These are default bindings; customized shortcuts
still apply. Space plays or pauses an active local video. **Up/Down** raises/lowers
available video volume without changing Playing/Paused; in the reader those keys
still navigate posts. Manual mute/unmute survives media changes within the same
open gallery. A user volume change to a positive value unmutes; reopening the
gallery uses the saved **Start videos muted** preference, not the last manual mute choice.

**Local reading continuation** is off by default. In Settings → Reading & media,
enable **Remember the last reading position locally** and Save. MeowChan then
keeps exactly one deliberately opened reader position for this profile. After
restarting, choose the **Continue reading** book icon left of Pause in the header,
or the labelled **Continue reading** entry in the app menu. The app keeps its
normal start page until you choose that action. Only
locally captured posts are read; no source request or download is started.

**Forget reading position** removes the entry immediately and keeps the setting
on. It starts remembering again only with your next deliberate reader action.
Saving the setting as off also deletes the entry. Missing local content is
explained without silently choosing another thread; if a saved post disappeared,
the first captured post is explicitly identified as a replacement position.
Settings uses **Discard draft** to revert unapplied edits; forgetting is a separate,
immediate action.

Settings controls default context visibility, shortcut hints, video autoplay,
initial mute and volume.

When you open from reader post A, navigate to a medium associated with saved post
D and close the gallery, return to the large reader at D. If the current medium
has no saved post association, keep the previous reader anchor; do not infer a
post from its filename. Opening from a search/media list keeps a separate return
path: closing restores its query, filters, page, scroll and focus.

Archive offers separate **List** and **Table** thread layouts, plus
saved-media rows/tiles. Its chosen thread layout and draggable list/reader split
are saved. Wide windows show list and reader beside each other; narrower windows
switch between them, with a back action to return to the list. Table needs more
horizontal room before showing both panes. Double-click or
Enter on a focused media row/tile opens that exact item, using the current media
sort and version filter. Text fields and action buttons retain their own keyboard
actions. Historical versions are labelled; they need not match the latest post.

Search operates on retained local titles, post text and identities. Source/board
filters work even without search text. **Media from matching threads** freezes the found
threads in their current sort order across **all result pages**, then opens their
available images, including images from posts that did not match the text.
Double-clicking one result thread opens only that thread. Changing filters or
new archive arrivals does not silently replace an already opened sequence;
close and reopen to take a new result snapshot.

On **Media**, the **Order** selector (German: **Sortierung**) offers **By thread**,
**Newest first**, **Random**, **Captured**, **File name**, **Size**, **References**,
**Tag count** and **Rating**. Every order except **Random** has a direction toggle
between its default and reversed order. **Random** has no direction toggle;
**Shuffle again** (German: **Neu mischen**) changes its stable shuffle seed.
The sort criterion and direction are saved for the next start; **Random** is not
remembered, and the next start uses **By thread** instead.

Search and Filters sit on the left of the Media toolbar, Order on the right.
**Export all matches…** (German: **Alle Treffer exportieren…**) and **Manage saved
ratings…** (**Gespeicherte Bewertungen verwalten…**) stay under **More** (**Mehr**).
In narrow windows Refresh and Shuffle again also move under More; Order stays
visible. **Show captured but missing files** (**Erfasste, aber fehlende Dateien
zeigen**) is in Filters, with the active chip **Including missing files**
(**Mit fehlenden Dateien**).

### Reading posts as cards

The reader shows every post as a card; the opening post sits directly on the page.
A card's header is a single line with subject, name, date and number. Clicking the
number (**No. …**) copies `>>number` for quoting. Thumbnails lie flat under a file
line with name and size.

In the post text, lines starting with `>` are green. Clicking `>>number` jumps to
that post, and the target flashes once. A quote of the opening post is followed by
“(OP)”; a quote into another thread is followed by “(other thread)”. Pointing at a
quote outlines its target when that post is visible on the current page. Quotes of
posts that were not captured are struck through. By default, spoilers stay hidden until you click them or press Enter or Space;
that reveals every spoiler of that post, and they stay revealed. The **Spoilers** setting also offers **Show on hover**
and **Always show**; a click in hover mode keeps the spoiler revealed. Code is shown in a
monospace font, external links are underlined. The portable HTML export remains a
separate offline rendering of saved posts.

Click an external link, or select it in the focused post text and press Enter,
to review its full address with the ASCII (punycode) host highlighted. The
confirmation initially focuses **Cancel**, so repeated Enter does not open the
browser by accident; choose **Open** deliberately. Only valid HTTP(S) addresses
without embedded user credentials become links. Addresses longer than 2048 UTF-16
code units stay plain text. **Do not ask again this session** skips subsequent
confirmations only for this app session, not after a restart. Opening a link
leaves local reading and can contact that website.

When the pointer rests briefly on a quote, or a quote button has focus and you press
Space, a preview of the quoted post appears. The default pointer delay is 300 ms; the
preview shows at most eight lines or 420 characters by default. Its line limit and
pointer delay are configurable in Settings. Escape closes it, and Space on the
quote button closes it again; Enter
on the quote button jumps there instead. The preview shows only posts of this thread
that are captured locally; a quote into another thread or of an uncaptured post
shows none.

By default, a post with more than 24 lines is folded to 18 lines and fades out at the bottom.
**Fold long posts above (lines)** changes the threshold; the folded excerpt shows three quarters of that value, rounded.
**Show more (… lines)** unfolds it, **Show less** folds it again; MeowChan remembers
this per post for as long as the thread is open. The thread find unfolds a post with
a hit by itself.

The **Replies: …** counter in the header opens the replies as indented cards. By
default it shows five at a time; **Show more replies** loads another batch. Clicking the counter again closes
them, and reopening reuses what was already loaded. A post without replies shows no
counter. Replies always come from the saved posts; the source is never queried for
them. Five cards per step is the default; **Reply cards per step** changes the batch size,
and **Lines per reply card** limits each excerpt (four lines by default). The character
budgets scale with the line limits: quote previews allow about 52.5 characters per line,
reply cards 65. These are excerpts, not limits on the stored post text.

**View → Post filters…** can match post text, subject, file name or MD5 and then hide, replace with a placeholder or
highlight matching posts. You can also hide one post from its post menu, optionally with its replies, and show it again.
These filters never delete posts, and HTML export always includes every saved post.
Post-filter profile transfer and backup are host APIs only. They are not wired to a
product-facing command or the transfer-package workflow yet.

When you return to a thread through **Continue reading** or load new posts with
**Show new posts**, a thin line with **… new posts since last time** marks the
first new post. Once you have scrolled past the line, **Back to last read** in the
reader toolbar takes you back to it. A thread you open from the list and a refresh
through **Follow live** start without a line.

### Reader display settings

Settings → **Reading & media** contains **Display** and **Long posts and previews**.
Changes stay in the Settings draft until **Save** succeeds; then they apply to the
open reader and are kept for this profile. **Discard draft** restores the saved
values. Changing categories or the interface language keeps the draft. Invalid
numeric input prevents saving and identifies the field to correct; a failed save
does not apply the draft. Applying a saved change keeps your place in the open
reader: it does not jump to another post, take keyboard focus or switch the
thread's tab.

| Setting | Default | Allowed |
| --- | --- | --- |
| Preview image width (px) | 240 | 120 to 480 |
| Fold long posts above (lines) | 24 | 8 to 400 |
| Lines in quote previews | 8 | 3 to 40 |
| Quote preview delay (ms) | 300 | 0 to 2000 |
| Reply cards per step | 5 | 1 to 50 |
| Lines per reply card | 4 | 1 to 20 |
| Posts per reader page | 50 | 20 to 200 |
| Maximum conversation indentation depth | 6 | 1 to 16 |

Preview widths default to 240 pixels in the embedded reader and 360 pixels in the
large reader, which uses 1.5 times the configured width. **Posts per reader page**
changes the reader's paging, not the archive thread-list page size. It keeps the
current reading position in view; conversation subtrees can span pages.

**Date and time format** offers **System format** (default), **Weekday and seconds**,
**Since opening post** and **Relative to now**. The exact local date remains in the
tooltip. If the opening post has no timestamp, **Since opening post** falls back to
System format; a post with no timestamp has no date text. Relative text is calculated
when the page is rendered, not refreshed by a timer. Quote previews use the same
format; their tooltip shows the full local date. A preview of a post from another
thread has no known opening post here, so **Since opening post** shows System
format for it.

**Spoilers** offers **Keep hidden** (default), **Show on hover** and **Always show**.
The conversation switch itself stays under **View** and is not a saved preference.
Post-filter rule and individually hidden-post limits are under **Storage and limits**,
not here; lowering them keeps existing entries and only prevents growth beyond the limit.

### Gallery Fit enlargement

Under Settings → **Reading & media → Gallery & playback**, **Enlarge small images
when fitting** offers **No limit** (default) or **Up to a limit**. With no limit,
Fit may enlarge a small image to fill the available viewport. With a limit, Fit
stops at the configured percentage; manual zoom is not capped by this setting.

| Setting | Default | Allowed |
| --- | --- | --- |
| Maximum enlargement (%) | 200 | 100 to 1600 |

The percentage field is available only with **Up to a limit**. Choosing **No limit**
does not erase a valid percentage: **Save** keeps it for when you enable the limit
again, but does not use it to constrain Fit while **No limit** is selected. An invalid
hidden percentage does not block an unrelated save; the last saved valid value is
kept instead. **Discard draft** restores the saved choice and percentage.

### Archive filters and saved searches

In the Archive, open **Filters and sorting**. Source and board fields offer
bounded local suggestions and also accept values you type. Accepting a suggestion
changes the input only. Use **Add** for each source, board or source state, or clear
unfinished input before searching. Add several alternatives when needed: a thread
may match any value within one category, and must match every restricted category.
An empty category is unrestricted. **Board source (or all sources)** distinguishes
a board in one source from the same board name across all sources; accepting a
source-qualified board suggestion uses that source. A board outside the selected
sources cannot match until you adjust the source or board choices.

For own-tag filters, open **Add tag…** (German: **Tag hinzufügen…**) and type to
find an existing own tag. Enter includes it; Shift+Enter or a leading `-` excludes
it. **Add as** (**Hinzufügen als**) also offers **Include** (**Einschließen**) and
**Exclude** (**Ausschließen**) without a modifier key. The small toggle on a draft
chip switches between include and exclude; × removes it. Choose **All included
tags** or **At least one included tag** for the inclusion match; an excluded tag
rules a thread out. Text typed but not added is flagged when you search: add the
tag or clear the text first. Adding or toggling a draft tag waits for **Search**;
these are search filters, not changes to the tags assigned to a thread.

**First registered locally** filters the day a thread first entered the local
archive catalog, including threads without captured posts. Enter **From** and/or
**Through** as `YYYY-MM-DD`, or use the calendar. The end day is included; either
end can remain empty. This is separate from the post's publication date or latest
capture. The displayed time zone is captured from the device when entering a date;
**Use this device’s time zone** explicitly changes it, including for a saved search.

Choose **Search** to apply the draft. The tags beside the results describe the
last successfully applied filters, even with the editors collapsed. Removing an
applied tag searches again without that one restriction, from page 1, retaining
the applied sort. Other unsearched edits stay in the editors. While loading or
after a failed search, the old results and tags stay together; **Retry** repeats
the failed request. Result paging and [all-results HTML export](#export-every-matching-archive-thread)
use the applied search. Ctrl+A selects only the current results page.

Choose **Save applied search…**, enter a name and **Save search**. This saves the
applied text, filters, date zone and sort for this profile. Unsearched input,
selected rows and reading positions are excluded. Open **Saved searches…**, select
a definition and **Apply search** to run it against the current local archive,
starting on page 1. Confirm replacement if you have an unsearched draft. The same
definition may find different threads later. **Rename**, **Replace with applied
search…** and **Delete search…** manage definitions; deleting one keeps the archive.

In the name field, Enter confirms creating or renaming. When no search is saved
yet, the dialog says so and points to **Save applied search…**; when searches exist
but none is selected, it explains that you need to select one first. How many
searches you can save is set by the **Saved searches** setting.

### Reader groups

In 1.0, open **Reader groups** (German: **Readergruppen**) from the app menu or
Archive actions. Choose **Create group**, enter a name, find local threads by
title or source/board/thread, select candidates and **Add threads**. **Move thread up**
and **Move thread down** set the reading order; **Remove thread** changes membership. Choose
**Save group** to keep the name, members and order in this profile. Save edits
before **Read group**; otherwise the dialog asks whether to discard them.

The reader follows the saved member order with its previous/next thread actions.
Missing local posts are explained; **Read available** explicitly continues with
readable members. Missing members stay in the group until you remove them.
Closing the group reader returns to its manager. Groups organise local reading
and start or change no watches, captures or downloads. Deleting a group removes
its membership list. For the separate optional last reading position, see
[local reading continuation](#images-search-and-navigation).

### Post bookmarks and notes

In 1.0, open a saved reader post's context menu and choose **Add / edit post
bookmark…**. Enter an optional plain-text note and **Save bookmark**; an empty note
also creates a bookmark. There is one bookmark and one note per post. Notes allow
up to 8,000 Unicode codepoints; Enter inserts a line break. Closing or changing the
selection asks before discarding changed text.

Open **Post bookmarks** (German: **Post-Lesezeichen**) from the app menu or Archive,
select an entry and choose **Open exact local post**. It opens that post's current
local version; the bookmark does not keep a copy of its earlier text. Closing its
reader returns to the overview. If the exact post is missing, the reference and
your note remain editable, without a replacement post or a source request.
**Remove bookmark…** removes the reference and its note after confirmation, keeping
the archived post. [Permanent archive deletion](#permanently-deleting-archived-threads)
keeps bookmarks and notes but can make their posts unavailable. Forgetting the
last reading position does not remove bookmarks.

Built-in image views support PNG, JPEG, GIF, WebP and BMP when the decoder accepts
the file. In 1.0.2, **GIFs animate in the gallery**;
**Space** (or the effective Play/Pause binding) and the GIF control pause/resume them.
Encoded finite/infinite loops are respected; ending a GIF does not silently select
another medium. Reader/thumbnail previews remain posters like video previews:
click, or Enter/Space in the selected post, opens the actual associated animation.
The explicitly started slideshow still treats a GIF as an image for its selected
image duration; Repeat-video preferences do not rewrite a GIF's encoded loop count.
Other animated image formats retain their still-first-frame fallback. GIF animation
uses verified originals only, at most 64 MiB, 16 million source pixels and 1024 frames;
over-limit or damaged GIFs remain unavailable with details in Diagnostics rather
than being presented as successful still-frame playback. Image previews above 64 MiB
or 80 million source pixels are refused by the gallery. Fit decoding is bounded to 16 million pixels; 1:1 may need substantially
more memory. Corrupt, missing, unsupported or unverifiable files show a reason.
The verified originals remain on disk even when preview is unavailable. These
viewer limits are separate from download rules and do not cap total archive size.

### Own tags and the shared media menu

Right-click a media tile, attachment or the shown gallery file to get the same
core menu: gallery, original export, show in folder, favorite, rating, media tags
and details, with source/post actions when that item has them. Disabled actions
explain their requirements instead of inventing a post for an unbound file.
Selection commands are marked separately from actions on the clicked file.

Your own tags can be attached to threads and to individual media, including
older loose media without a captured post. They stay local. A thread's tags and
a medium's tags are distinct; a matching tag filter may include media through
their thread, but **Without media tags** counts only tags assigned to the media
itself. Danbooru source tags do not count as your own tags. Click the untagged
count on Media, or use that filter, to start tagging those items.

In Archive, right-click a thread and choose **Tags…**, or use Ctrl+T (Command+T
on macOS), to open its own-tag editor beside the posts. The inline editor follows
the open thread until you close it. **Edit media tags…** (German: **Medientags
bearbeiten…**, Ctrl+Shift+T / Command+Shift+T) opens the medium's tag dialog on
Media; in the gallery it opens the [tag panel](#gallery-tag-panel), whose
**Advanced…** button leads to the same dialog. That dialog has a search field, selection counts for
group tagging, and **Undo** for the last dialog edit. Tag-name suggestions put your
own tags first and Danbooru source tags in a separate group. Choosing a source tag as an
own tag creates a local tag; it does not edit Danbooru. Clicking a tag chip on a
media tile filters the view by that tag.

Tagging an older loose file records that local occurrence/revision. Equal file
bytes alone do not merge tags between unrelated files. Back up the profile's
`private-tags.json` with its media; the one-time v1 backup described below is not
a replacement for a complete backup.

### Gallery tag panel

**Tags** on the gallery toolbar (under **More** in Barebones), **Edit media tags…**
in the gallery's context menu and Ctrl+Shift+T (Command+Shift+T on macOS) open the
tag panel for the shown medium; a running slideshow pauses. The panel takes the
slot of the post panel and stays open while you page through the gallery. The same
command, **Esc** or its close button closes it; **P** switches to the post panel.
Outside the gallery, Media still opens the full tag dialog.

**Safe flow.** Nothing is assigned automatically. Type a name and press Enter to
give your own tag to the shown medium; a new name creates a local own tag. With an
empty field the panel offers suggestion chips; hover a chip, or read its accessible
name, for why it is offered: often used, recently used, from this thread, on nearby
media, same file elsewhere, from the file name, from the thread title or source tag.
Choosing a chip, or the number on it, gives or takes off that tag at once. Each
change is written to the tag store immediately, only for the medium it was made on.
**On this media file** lists what the medium carries; select a tag and press Delete
to remove it. A medium that cannot be tagged, for example without a bound version
or while the tag index is unavailable, shows the reason with a disabled field
instead of writing to the previous medium.

**Keys.** The numbered chips 1 to 9 toggle with Alt+1 to Alt+9 (Command+1 to
Command+9 on macOS) wherever the focus is in the panel; bare number or letter keys
work only while focus is in the suggestion strip, so names such as `2girls` stay
typeable. Page Down and Page Up show the next or previous medium and keep the
field focused; the counter reads “Medium 4 of 27” or “Medium 4 of at least 27” while
more threads are still being resolved. Ctrl+Enter (Command+Enter) adds the typed
tag and moves to the next medium. Undo is the **Undo** button or Ctrl+Z
(Command+Z); with text in the field Ctrl+Z edits that text instead. These
shortcuts are rebindable. Marking with Shift+Page Down and Shift+Page Up does
nothing yet.

**Undo.** The panel keeps a stack of changes up to the **Tag undo steps** setting
and drops older steps. The stack stays while you page, so an undo can reach a change
made on an earlier medium; it reverses exactly that step on that medium and the
status line names it. A tag created by a step stays in your own tag list after its
undo. Closing the panel ends the stack. Favorites and ratings are separate gallery
actions: tagging never changes them, and a favorite is not a tag.

**Medium, thread and source tags are different things.**
- **Medium tags** are your own tags on exactly the shown file; the main list of the
  panel changes only these.
- **Thread tags** mark the thread, not its media files. Ctrl+T (Command+T) opens the
  **Thread tags** section of the shown medium's thread with its own field; click a
  tag to remove it, and Ctrl+T again collapses the section. They do not appear under
  **On this media file**.
- **Source tags** come from a source such as Danbooru. They are not your own tags;
  choosing one creates a local own tag and gives it to this medium, and Danbooru is
  not edited. **Source tags per medium** limits how many are offered.

**Advanced…** opens the full tag dialog for the same medium (occurrences, sorting
and long lists). When it closes, the panel reloads and shows what the dialog
changed. The quick-tag popover outside the gallery and marking several media for
one change are not available yet.

### Several files in a thread's media tab

In a thread's media tab you select several files as on the Media page: Ctrl/Cmd+click
adds a file, Shift+click or Shift with the arrow keys selects a range, Ctrl/Cmd+A
selects all, Escape clears the selection. A status line says how many files are
selected.

With two or more files selected, **Tag together…**, **Selection as gallery**,
**Export originals…** and **Clear selection** act on all of them. Every other
command still acts on exactly the file you clicked, and the gallery opens at it.
The context menu acts on the focused file; only its selection actions act on all
selected files.

### Favorites and original-file export

Use the star on reader attachments, saved-media items or the gallery to mark a
saved original. Favorites persist in the profile and identify that exact media
revision; a later replacement does not inherit the mark. **Favorites only** filters
thread media. **Show favorites** opens a searchable overview scoped to the current
thread, selected threads, matching threads or all favorites. Missing originals
remain identified as unavailable.

Choose **Export original…** for one item, or **Export favorite originals…** for the
favorites in the chosen scope. Choose a local destination folder and start the
copy. It uses verified saved originals and fetches nothing remotely. Files are
organized by source, board and thread; existing files are not overwritten. The
result reports copied, already-existing, missing, failed and cancelled items.
This copies media files; use HTML export below when you want posts and a browser
reading interface too.

### Media ratings

In 1.0, choose **Edit rating…** on a saved reader attachment, selected Archive
medium or the gallery. Choose a whole value from 1 to 5: clicking saves it;
with the keyboard, arrows choose and Enter saves. Escape closes without saving
a pending keyboard choice. **Remove rating** returns the item to **Unrated**.
The rating persists for that exact saved revision and is independent of its
[favorite mark](#favorites-and-original-file-export); a replacement revision does
not inherit it.

In a thread's Archive media view, **Rating filter** offers **All ratings**,
**Unrated** or **At least 1** through **At least 5**. A rating change can remove an
item from that filtered view. This filters media within the thread, separately
from the thread search above. Ratings do not alter saved files or download rules.
**Manage saved ratings…** (German: **Gespeicherte Bewertungen verwalten…**) is in
the Media page's More menu, the Archive rating row and the gallery's More menu,
not Diagnostics. It lets you inspect stored entries and remove an individual
rating, including one whose original is no longer available.

### Saving a video frame

In the gallery, **Save frame as image** (German: **Einzelbild als Bilddatei speichern**,
Shift+S) saves the displayed video frame as a PNG. On first use you choose a
folder for saved video frames. MeowChan remembers this dedicated folder, not the
archive export folder; if it no longer exists, or matches the remembered export
folder, you are asked again. You may explicitly select the export folder for
this save, but it is not remembered as the frames folder. Cancel saves nothing.
Existing files are never overwritten; a free numbered name is used instead.
The file is named after the video and the playback position. The command is
available only while a video is shown and ready, and it sits under **More** in
every work view. A notice **Frame saved** (German: **Einzelbild gespeichert**)
names the file; if nothing could be saved, a warning says so.
There is no separate Settings control for changing the frames folder yet.

Deutsch: Das Standbild eines Videos speichert die Galerie als PNG in einem beim
ersten Mal gewählten Ordner. Der eigene Standbildordner wird gemerkt; der
Exportordner wird nie stillschweigend übernommen. Ist der gemerkte Ordner nicht
mehr vorhanden oder inzwischen der Exportordner, wird erneut gefragt. Ein
bewusst gewählter Exportordner gilt nur für diesen Speichervorgang. Abbrechen
speichert nichts; vorhandene Dateien werden nie überschrieben. Der Dateiname
nennt das Video und die Abspielposition. Der Befehl ist nur verfügbar, solange
ein Video angezeigt wird und bereit ist, und steht in jeder Arbeitsansicht unter
**Mehr**. Eine Meldung nennt die gespeicherte Datei; scheitert das Speichern,
warnt eine Meldung. Eine eigene Einstellung zum Wechseln des Standbildordners
gibt es noch nicht.

### Local video

Video attachments in captured posts can show an offline
still preview generated from the verified saved video. These previews are reused
across restarts without fetching remote thumbnails. If a preview cannot be made,
the original stays available to open in the gallery; playback remains a gallery
action rather than starting inside the reader. Preview generation needs a working
local VLC backend too.

The gallery includes a bounded LibVLC player for local video, including WebM.
Use its compact transport strip for **Play/Pause**, seeking, mute, volume and
repeat. In 1.0.2 the chosen repeat mode is retained as described above. At narrow widths the controls use
two rows. Audio controls show why they are unavailable when there is no track or
usable output. In 1.0.2, Left/Right on the gallery timeline or
volume control uses the effective previous/next-media bindings; Up/Down adjusts
available video volume. Timeline keyboard seeking is disabled; mouse seeking and
volume remain available and preserve Playing/Paused. Other native controls and
Reader arrows keep their own roles.
Linux uses either the selected private bundled VLC or the supported system VLC
runtime, checked before startup as described above. Windows/macOS use official
VideoLAN native assets. All desktop variants include .NET; bundled Linux archives
and AppImages also include VLC. Individual decode failures remain per-file errors. See the [platform record](platform-support.md) for native package/playback
evidence and limits; audio, GPU and other OS coverage must be assessed there.

## Missing media and finished threads

**Unreleased source builds:** Download rows reuse known locally saved thread
titles on restart without waiting for the source website. When a thread truly
has no title, a short cleaned opening-post excerpt is shown; without available
content, its source/board/thread identity is used. Capture-off watches retain
observed real titles without enabling full-post capture. Legacy ambiguous text
such as a genuine title named “Unknown” is not deleted based on its spelling.

Full Pause applies globally to source checks, media transfers and manual retries.
It waits for active cancellation before reporting a settled pause. Verified files
remain saved; unfinished transfers are retained for Resume and restart from the
beginning. Requested pause cancellation is not a failure or new retry attempt.
Pause does not reset failure history or extend a terminal deadline; byte-range
resume is not implemented.

A media 404 pauses automatic retry for **15, 30, 60, 120, then 240 minutes** after
successive failures; subsequent pauses remain at least 240 minutes. The displayed
time is the earliest eligibility, not a guaranteed start: pause, queue capacity,
source limits and server cooldowns can delay it. Desktop work state survives a
restart. Ordinary **Check now** respects these pauses.

**Retry missing media** grants one outer attempt per selected missing item, not a
fresh automatic retry series. Repeated clicks coalesce. Pause, source limits and
`Retry-After` still apply. A successful verified save or genuinely new revision
resets failure history; a changed URL alone does not.

Once the source reports archived or its thread endpoint reports not found,
MeowChan enters a follow-up period. Automatic missing-404 attempts stop **12 hours
after the first durably recorded source end**. Restart, pause and manual retry do
not extend this period. Active transfers can finish. A thread can finish with
missing 404 media, which remain explained in Archive. Other exhausted failures,
post-capture or storage errors require intervention rather than claiming success.
A crash before the first end timestamp is committed cannot recover that lost time.

A manual media retry after completion repairs known media without restarting
source monitoring. Explicitly adding the thread again starts a new watch generation.
Removing monitoring retains captured posts and saved files. Disabling **Save URLs
on exit** prevents automatic watch restoration from both the URL list and retained
work state; it does not erase the archive.

## Importing your own files

You can bring your own files into your media library. Choose **Import files…** from
the menu or drop files and folders onto the window. In the dialog you collect the
selection with **Add files…** and **Add folder…**; **Include subfolders** and **Also
other files (PDF, ZIP, audio)** widen it. Other files are kept without a preview.

MeowChan first looks through the selection and checks every file by SHA-256. The
list shows for each file whether it is new, already there, selected twice, not
supported, an other file that is switched off, too large, not readable or skipped (a
link, or already inside the download folder). When not all media could be read, the
dialog says that the duplicate check is incomplete. **Stop check** only stops the
looking through.

Give the batch a name (up to 200 characters) and, if you like, own tags, then start
the import. New files are copied into the `meowchan-import/` folder in your download
folder; files you already have are only linked. Your originals are never moved or
changed. **Stop after current file** ends the run; files already copied stay
imported. Afterwards, **Show batch** takes you to the Media page.

Imported files are ordinary local media: they appear on the Media page under
**Own imports**, in the gallery and among the favorites, and can be tagged and
rated like everything else. The import checks the required space and free-space
reserve before copying; changes in available space or other write failures can
still interrupt a run. When a selection hits a limit, the dialog names the
setting under **Storage and limits** that you can raise.

For backups, keep the profile (including `local-imports.json`), the
`meowchan-import/` folder, and any existing media to which the catalog links.

## Clean up small archive threads

In 1.0, open **Archive** → **Clean up archive…** (German:
**Archiv** → **Archiv aufräumen…**). This opens a preview of the whole archive,
including threads outside the current results page. Nothing is selected
automatically.

1. Review the preview and the reason beside each thread. Candidates have **0, 1
   or 2 actually saved media versions**, including older versions and variants.
   Threads with more than two are kept. **Unknown** or unconfirmed storage is
   never treated as zero and cannot be selected.
2. Tick the candidates you want to remove, or choose **Select shown candidates**.
   Search and filters help narrow the list; the selection total includes choices
   across all filters. An incomplete or changed preview requires **Check again**
   before you can continue.
3. Choose **Review deletion…**. Check the selected threads, captured posts,
   saved versions and size. Use **Back to selection** to change your choices.
4. Acknowledge that deletion is permanent and choose **Permanently delete
   locally**. There is **no recycle bin**. Closing the preview before this
   confirmation leaves the archive unchanged.

Thread and media favorites, ratings (including older versions), post bookmarks
or notes, reader groups and saved reading positions protect their threads.
Threads open in a reader, gallery or export are protected too, as are unfinished
download/watch jobs, including paused work or work needing attention. The preview
explains protected or uncertain entries instead of selecting them.

Cleanup removes selected local archive content. Download and watch jobs remain
unchanged; cleanup does not pause or stop them. Existing rules for adding or
rediscovering threads continue to apply. Exports and unrelated files remain.

Read the result after deletion: it reports removed threads/files and selected
threads that remain. A cancellation or failure can leave a partial result, with
some files already removed. Use **Check again** to obtain a fresh preview before
another selection; changed content or protection may prevent further deletion.

## Permanently deleting archived threads

In Archive, select one or more threads and choose **Delete permanently…** from
the context menu, or press **Shift+Delete**. Review the thread count, local file
count, size and paths before confirming. Cancel leaves everything unchanged.
This removes captured posts, indexed search data, manifest-owned originals and
versions, favorites and cached video posters. Watched boards keep these threads
excluded; you can explicitly enable them again through board exclusions.

In 1.0, reader-group memberships and post bookmarks with your notes remain;
deleted posts become locally unavailable through them. Remove unwanted entries
under **Reader groups** or **Post bookmarks**. Ratings for deleted media are
cleaned up; if that fails, the deletion result offers **Retry rating cleanup**.

Exports and unrelated files remain available. Directories shared with unselected
archived threads or with unverifiable ownership block deletion. Changed files or
other failures are reported; affected archive records remain for another attempt.
Ordinary **Delete** only removes watches and keeps the archive and media.

## Portable HTML export

Right-click an Archive thread or use **HTML…** in its reader. The compact dialog
binds the chosen thread; choose a destination and whether to include saved media.
Exports contain captured posts, CSS and verified matching saved originals, or a
media gallery for media-only threads. Missing files, historical mismatches and
rule exclusions are reported. Exporting does not fetch remote content.

For a known earlier export, choose **New folder** (the default) or explicitly
**Update existing export**. A new folder keeps the earlier copy. Update rebuilds
the selected export and exchanges the completed folder; it does not merge custom
files. Changed/additional files, an associated legacy export or a reduced new
export require confirmation that the previous folder will be retained as a backup.
Read the result for its actual backup path. Allow space for both old and new data.
An unavailable previous target cannot be updated; choose a new folder or associate
a moved export. Legacy candidates need explicit association, not merely a similar
name. No system-wide search runs automatically.

**Open previous export** opens a verified earlier result. **Open folder** stays
disabled until the current export succeeds and then opens exactly that result.
Open `index.html` in the result folder in a browser. Copy the **entire folder** to
another machine for portable offline reading. Keep its metadata file for future
association and updates. A failure to open the system folder does not invalidate
an otherwise successful export. Exports are not a full profile backup.

Exported pages have a **Theme** switch (**System**, **Light**, **Dark**) in their
header. It works without scripts and returns to System after a reload. **About**
shows source/thread details, counts, timestamps, the exporting MeowChan version
and credits. Its credit panel contains the Simba-and-Mila logo pair and the photo
credit; this is separate from any single-cat artwork in the desktop header. These
HTML controls are independent of the desktop theme.

The export frame follows the app language and is translated for English, German,
Spanish, French, Russian, Czech, Italian, Japanese, Korean, Polish, Brazilian
Portuguese and Simplified Chinese. Unsupported languages use English. Captured
post text, source names and other saved content are not translated.

### Combined export of selected threads

Export a multi-selection as one offline folder: `index.html` is the landing page,
individual thread pages live under `threads/`, and verified media are pooled under
`media/`. Each member shows its captured read time; the export does not claim a
single simultaneous snapshot across threads. Only saved local data are used.

The dialog recognizes an earlier export of the same ordered selection. Choose a
new folder or explicitly update an inspected result. Changing the selection
requires a new folder. Updates check identity/generation and observed content;
modified/extra files or reduced posts/media require a retained backup. Copy the
whole folder for portable reading. This combined export is part of 0.9.0.

### Export every matching archive thread

Open **HTML…** beside the Archive results and choose **Export all N results as
HTML…**. This includes all pages of the applied search, including its source,
board, content and work filters. The dialog resolves the actual ordered targets
and shows their fixed count. Close or press Escape during resolution to cancel.

Later searches, sorting, new matches and work-state changes do not change that
dialog's targets. After **Export**, each target's current local content is read
separately. A deleted target fails the export; it is not replaced by another match.
Missing media retain the usual omission rules. Ctrl/Cmd+A and selection exports
still apply only to the current results page.

## Upgrading and backups

Close every older application and library writer before upgrading. Back up the
complete profile and media directories, including manifests and any network-share
media. Preserve export folders separately. A fresh profile is
`%APPDATA%/.MeowChan` on Windows or `$HOME/.MeowChan` on macOS/Linux, with
`Downloads/MeowChan` as the default media root. An existing installation's profile
is reused when it is the only profile; its configured or implicit media destination
is preserved. If both profile directories exist, select the intended absolute
path with `MEOWCHAN_PROFILE`. The older environment-variable alias is accepted;
if both are set their resolved paths must agree.

The profile/media lease protocol introduced in 0.9.0 cannot coordinate with
older writers. Do not run 0.9.0 and 1.0 writers against the same data concurrently.
1.0 writes **archive schema 5 and media manifest 4** and reads older records,
including bindings without the new optional evidence. There is no supported
downgrade of data changed by 1.0. If you need the
[0.9.0 fallback](https://github.com/grassmann-it/MeowChan/releases/tag/v0.9.0), use
an untouched profile or restore a pre-upgrade backup to a separate location;
keep the 1.0 data for investigating the problem.

**Plugin-state transition introduced in 1.0.2:** the current source can write a v2
plugin-state envelope that 1.0.0/1.0.1 do not read. Before replacing an existing
validated v1 state—even during startup observation—it retains the original bytes
as `plugin-state/state.pre-v2.json` plus a `.sha256` digest. This is an immutable
plugin-state file, **not a full-profile backup or automatic recovery mechanism**.
Do not delete conflicting backup files merely to make an upgrade continue.
A profile already written as v2 without an earlier backup cannot reconstruct its
old v1 contents. For downgrade, close the application and restore a matching full
pre-upgrade profile backup to a separate location; never substitute plugin state
into an unrelated or actively running profile.

New durable work state records retries, source end and watch intent independently
of captured posts. Never delete its files to dismiss a failure; preserve a backup
and inspect the reported recovery error.

### Own-tag v1 backup and 1.1.0 local data

On the first write that actually upgrades an existing own-tag v1 document to v2
by committing local-media data, MeowChan keeps its exact previous bytes permanently
as `private-tags.v1.bak` in the profile. Merely reading v1 does not rewrite it.
The ordinary `private-tags.json.bak` is replaced by later tag changes; the v1 file
is never rotated, overwritten or deleted. A profile already created as v2 cannot
manufacture an old v1 copy. Keep `private-tags.v1.bak` with your profile backups.
This is **not a full-profile backup** and does not make downgrading a modified
profile supported.

Before trying 1.1.0, retain a complete separate 1.0.2 profile/media backup. Preserve
the Booru catalog, private tags, import catalog and imported media with the rest
of the profile and media roots. To go back, close all writers and restore a
matching pre-upgrade backup to a separate location, not pieces of an active
1.1.0 profile.

### In-place bundled-plugin rebuilds

A normal in-place rebuild of an already selected, proven bundled plugin (including
`dotnet run`) is adopted at startup when its current delivered identity is verified,
its description is unchanged and the exact saved configuration remains valid.
You do not need another repair confirmation followed by a second application start
merely because the executable hash changed. Enabled/disabled choices and rollback
history remain intact. A previously confirmed same-identity in-place repair does
not force the confirmation loop again when another compatible rebuild occurs.
A reviewed new selection with complete saved configuration and genuine prior
bundle proof can also follow a compatible rebuild before its first activation;
its old selected build is not falsely recorded as previously runtime-activated.

This does not silently unpin code, transfer special permissions, select a different
installation/candidate, accept changed declarations or invent missing legacy
provenance. Those cases remain separate reviewed operations. Manual repair/new
selection in the running session is still saved for the next start; general live
plugin replacement is not currently implemented.

### Changed bundled-plugin capabilities or settings

If an update changes a bundled plugin's capabilities or settings (for example
Danbooru), open **Sources & Plugins → Review bundled repairs**. Complete the review in
that same dialog; do not delete the profile or try **NEW selection** for an
already identified update.

1. Review the old and new capabilities. Technical identities and exact review
   data are available under **Technical details**.
2. Correct any settings directly in the dialog. Existing compatible values are
   prefilled. Removed fields require an explicit acknowledgement; their old
   values remain in the retained old plugin version.
3. Confirm the capability/settings changes. If existing special permissions
   are listed, confirm those separately for this exact new build. Editing a
   setting in the dialog, or using the acknowledgement for removed values,
   clears the change confirmation, even if you put the old value back; tick it
   again after your last edit. The separate permission confirmation is not
   cleared by edits.
4. Choose **Save reviewed plugin for next start**, then restart the application.
   Saving does not activate the updated plugin in the running session.

The dialog validates the settings and rechecks the exact displayed update before
saving. If the files, permissions or profile changed meanwhile, reload and review
again. Existing enabled/disabled choices, source ownership and rollback data are
not silently replaced. A conflicting owner, pinned version, ambiguous candidate
or missing delivery proof cannot be overridden by this confirmation.

### Missing legacy plugin candidates

If an older profile reports **Candidate is missing from the startup inventory**,
open **Sources & Plugins → Review bundled repairs**. For supported, unambiguous entries,
**NEW selection** offers a currently delivered bundled build. Inspect its exact
identity and the complete settings, explicitly check the acknowledgement, confirm,
and restart the application. Enabled/disabled choices are preserved. This does
not establish where the old candidate came from and does not transfer pins,
permission grants or source ownership. Pinned, rights-bearing, conflicting or
schema-incompatible entries remain blocked rather than silently reset.

Before the first confirmed new selection, the actual current plugin state is
retained in `plugin-state/state.pre-reselection.json` with its digest and a binding
in the profile. Later rows do not refresh that first backup. This also works for
already-v2 profiles; it does not manufacture a missing historical pre-v2 backup.
Neither plugin-state backup replaces a full-profile/media backup for downgrade.

Library users must migrate to the `libmeowchan` package/assembly and `MeowChan.*`
namespaces; there is no binary redirect. See the [session API](Registry.md) and
[package consumer sample](../samples/LibMeowChan.Consumer/README.md).
