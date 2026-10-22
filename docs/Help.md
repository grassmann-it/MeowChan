# Using MeowChan

MeowChan watches 4chan and 8kun boards or threads, saves selected original media,
and optionally captures posts for local reading and search. The library and
local archive work independently of the desktop interface. See the
[0.9.0 release notes](releases/v0.9.0.md) for downloads and upgrade details.

## From a package to an offline picture

1. Choose the package for your OS and CPU on the [0.9.0 release page](https://github.com/tfgrass/MeowChan/releases/tag/v0.9.0). Extract the **whole** archive, including native libraries and notices. Start `MeowChan.exe` on Windows, `MeowChan` from a Linux archive, or the executable Linux AppImage. On macOS, open the DMG, drag `MeowChan.app` to Applications, and launch it there. Desktop packages include .NET; source builds require the pinned .NET 10 SDK. Linux requires glibc and X11/XWayland with native graphics/font dependencies. The macOS apps are ad-hoc signed and not notarized; see [macOS first-launch guidance](macos-signing.md) for manual approval in Privacy & Security. See [platform requirements](platform-support.md).
2. Open Settings, choose your download folder and media rules, and Save. Changing the folder affects newly admitted watches; existing watches keep their assigned folders, including threads later found by an already watched board.
3. Paste a thread or board URL into Downloads and choose Add. Commas and newlines separate multiple URLs. Enter imports; Shift+Enter inserts a newline. Each URL receives its own result. Sources lists the registered adapters and their capabilities; registration does not prove the site is currently reachable.
4. Let media download. Progress means **saved / selected media**, with rule exclusions separate. A living thread remains watched even at 100%. New watches capture posts by default; older restored watches can be media-only until capture is explicitly enabled. Pause now requests a full pause of source checks and active transfers. The Pausing state remains until cancellation has quiesced; Resume retries unfinished work.
5. Open Archive. Select the thread to inspect it, double-click its card or row to open the large reader, or right-click for **Read posts**, **Open in gallery** and **Export as HTML**. Downloads thread rows also open the reader on double-click; use Shift+Enter or the context menu for the gallery; board rows retain their details action. Opening local content never initiates a download. Saved media remain usable offline; post reading requires captured posts.

The header shows download speed from received media bytes. Wider windows also show
a compact speed-history graph. This is separate from saved/selected media progress.

### Desktop view defaults

MeowChan 0.9.0 starts new profiles in **Barebones**; missing or invalid view
preferences also fall back to Barebones. Saved **Barebones**, **Simple** and
**Advanced** choices remain unchanged. Use the footer view selector to switch.

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

### Selection and shortcuts

Use mouse selection with Shift for a range and Ctrl (Command on macOS) to add or
remove individual entries. **Read posts** and Enter require exactly one selected
thread in Downloads or Archive. Shift+Enter opens the focused lead thread's gallery;
copy, removal and HTML export use the selection. Board entries open their details.
Removing a watch preserves its archive; removed board threads stay excluded from
discovery until reactivated. Disabled context actions explain why they are
unavailable; Archive also provides **Why are actions unavailable?**.

These shortcuts apply when the relevant list has focus. Ctrl means Command on macOS:

| Key | Action |
| --- | --- |
| Enter / Shift+Enter | Read the single selected thread / open the lead thread's gallery |
| Alt+Enter | Show details |
| Ctrl+C / Ctrl+E | Copy selected links / export selected threads as HTML |
| Delete | Remove selected watches |
| Shift+Delete | Permanently delete selected Archive threads and their local media (confirmation required) |
| F5 / Shift+F5 | Check eligible selected watches / retry their missing media |
| Ctrl+R | Edit the lead entry's media rules |
| Ctrl+Shift+R | Open the focused board's **Thread filters…** wizard (Board list only) |
| Ctrl+O / Ctrl+Shift+O | Open the lead entry's folder / source in the browser |
| Shift+F10 | Open the list context menu |
| Ctrl+A | Select all entries in the focused Downloads list, or all entries on the current Archive results page |
| Alt+PageUp / Alt+PageDown | Previous / next Archive results page, only with focus in the results list |

Text fields and embedded controls retain their own keys. Across the workspace,
Ctrl+L focuses URL input, Ctrl+, opens Settings, and Ctrl+F focuses the local
Downloads search or Archive search on the active page. Ctrl+1 through Ctrl+5 open
Downloads, Archive, Rules, Sources and Diagnostics in that order. Ctrl+Shift+P
uses the same session Pause / Resume action as the header button; it remains
unavailable while a pause transition is in progress. These direct page and pause
shortcuts leave native inputs, dialogs and open overlays in control and retain
unsaved page drafts. F1 shows the commands for the current context, including
selection scope and availability. Ctrl+V imports URLs only in Downloads outside
native controls; inside the URL field it pastes editable text normally. F11 toggles real window fullscreen
in Downloads, Archive, reader and gallery; it preserves the current workspace and
returns to the previous normal or maximized window. Escape dismisses the active
menu, help or dialog first, then leaves fullscreen before closing a gallery or
reader. Modal dialogs retain their own input.

In Rules, each effective media-type or size value names its own origin. **Open
rule** opens that exact Global, Source, Board or Thread scope. **Open parent
scope** moves up one scope. Unsaved drafts and conflicting edits remain available
when returning to their scope; navigation does not save or retry downloads.
Built-in defaults have no stored rule to open. Saving affects future work and
does not delete previously saved files.

## Linux packages and startup

For either `linux-x64` or `linux-arm64`, choose a package from the [0.9.0 release](https://github.com/tfgrass/MeowChan/releases/tag/v0.9.0):

- `MeowChan-0.9.0-<RID>-bundled-vlc.tar.gz`: VLC and .NET included; extract everything and run `./MeowChan`.
- `MeowChan-0.9.0-<RID>-system-vlc.tar.gz`: .NET included; install **LibVLC 3, at least 3.0.20**, with Matroska/WebM, VP8/VP9, Vorbis/Opus and audio/video output plugins through your distribution first.
- `MeowChan-0.9.0-<RID>.AppImage`: VLC and .NET included. For example on x64, run `chmod +x MeowChan-0.9.0-linux-x64.AppImage`, then `./MeowChan-0.9.0-linux-x64.AppImage`. If FUSE is unavailable, use `./MeowChan-0.9.0-linux-x64.AppImage --appimage-extract-and-run`. Use the `linux-arm64` filename on ARM64.

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

Newly captured posts leave the open reader unchanged. A **Show new posts** notice
lets you load them and jump to the first newly appended post. **Follow live** is
an explicit choice for a currently watched thread with post capture enabled; it
follows newly saved posts to the end without starting any network work. It waits
while a gallery or export dialog is open. Changing threads or removing the watch
turns following off. Repeated source polls without new posts show no notice.

Expand **Replies to #…** on a post to see its local reply count and captured reply
links inline. Replies load in pages of 20; **More replies** appends another page.
Opening a reply uses saved posts only and never fetches the source.

The reader reserves space for previews so loading images do not keep shifting posts. Refreshing the same thread
retains the current reading position. If a preview cannot finish loading, it reports “The preview could not be loaded.”
instead of remaining at Loading.

The app header and window controls remain available above the large reader
and gallery.
The gallery uses the window's remaining work area. Left/Right or its navigation buttons
move between media. Use Fit, 1:1, +/− or wheel/touchpad gestures to zoom.
Fractional touchpad deltas produce finer zoom changes. Zooming out stops at Fit;
the image is then centered and pan resets. Drag a zoomed image to pan. Double-click the image to switch Fit/1:1. Escape closes the gallery.
Show context to read the associated saved post and available local references.
A media-only item can have no captured post. In the gallery, **P** toggles saved-post
context, **0** selects Fit, **1** selects 1:1, **F** toggles the current media favorite,
and F1 or ? opens keyboard help. Space plays or pauses an active local video.
**Local reading continuation** is off by default. In Settings → Reading & media,
enable **Remember the last reading position locally** and Save. MeowChan then
keeps exactly one deliberately opened reader position for this profile. After
restarting, choose **Resume last reading** beside Archive in the app menu (or
Tools). The app keeps its normal start page until you choose that action. Only
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

Built-in image views support PNG, JPEG, GIF, WebP and BMP when the decoder accepts
the file. Animated files show a still first frame. Image previews above 64 MiB
or 80 million source pixels are refused by the gallery. Fit decoding is bounded to 16 million pixels; 1:1 may need substantially
more memory. Corrupt, missing, unsupported or unverifiable files show a reason.
The verified originals remain on disk even when preview is unavailable. These
viewer limits are separate from download rules and do not cap total archive size.

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

### Local video

Video attachments in captured posts can show an offline
still preview generated from the verified saved video. These previews are reused
across restarts without fetching remote thumbnails. If a preview cannot be made,
the original stays available to open in the gallery; playback remains a gallery
action rather than starting inside the reader. Preview generation needs a working
local VLC backend too.

The gallery includes a bounded LibVLC player for local video, including WebM.
Linux uses either the selected private bundled VLC or the supported system VLC
runtime, checked before startup as described above. Windows/macOS use official
VideoLAN native assets. All desktop variants include .NET; bundled Linux archives
and AppImages also include VLC. Individual decode failures remain per-file errors. See the [platform record](platform-support.md) for native package/playback
evidence and limits; audio, GPU and other OS coverage must be assessed there.

## Missing media and finished threads

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

## Permanently deleting archived threads

In Archive, select one or more threads and choose **Delete permanently…** from
the context menu, or press **Shift+Delete**. Review the thread count, local file
count, size and paths before confirming. Cancel leaves everything unchanged.
This removes captured posts, indexed search data, manifest-owned originals and
versions, favorites and cached video posters. Watched boards keep these threads
excluded; you can explicitly enable them again through board exclusions.

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

0.9.0 uses a new profile/media lease protocol. Older writers cannot coordinate
with these locks: concurrent old/new writers on the same data are unsupported.
No downgrade path is promised. V1 media manifests remain readable, but V2 media
history and migration of archive catalog schema 1 to schema 2 require current
readers. The passive archive reader can still read schema 1 without migration.
New durable work state records retries, source end and watch intent independently
of captured posts. Never delete its files to dismiss a failure; preserve a backup
and inspect the reported recovery error.

Library users must migrate to the `libmeowchan` package/assembly and `MeowChan.*`
namespaces; there is no binary redirect. See the [session API](Registry.md) and
[package consumer sample](../samples/LibMeowChan.Consumer/README.md).
