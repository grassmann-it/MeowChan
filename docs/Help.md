# Using MeowChan

MeowChan watches 4chan and 8kun boards or threads, saves selected original media,
and optionally captures posts for local reading and search. The library and
local archive work independently of the desktop interface. Version 0.9.0 is in
preparation; see the [release notes](releases/v0.9.0.md) for acceptance limits.

## From a package to an offline picture

1. Choose the archive for your OS and CPU on the [releases page](https://github.com/tfgrass/MeowChan/releases). Extract the **whole** archive, including native libraries and notices. Start `MeowChan.exe` on Windows, `MeowChan` on Linux, or `MeowChan.app` on macOS. Desktop packages include .NET; source builds require the pinned .NET 10 SDK. Linux requires glibc and X11/XWayland with native graphics/font dependencies. macOS packages are unsigned and not notarized; native package validation remains open. See [platform requirements](platform-support.md).
2. Open Settings, choose your download folder and media rules, and Save. Changing the folder affects newly admitted watches; existing watches keep their assigned folders, including threads later found by an already watched board.
3. Paste a thread or board URL into Downloads and choose Add. Commas and newlines separate multiple URLs. Enter imports; Shift+Enter inserts a newline. Each URL receives its own result. Sources lists the registered adapters and their capabilities; registration does not prove the site is currently reachable.
4. Let media download. Progress means **saved / selected media**, with rule exclusions separate. A living thread remains watched even at 100%. New watches capture posts by default; older restored watches can be media-only until capture is explicitly enabled. Pause stops new checks/transfers while already running work finishes.
5. Open Archive. Select the thread to inspect it, double-click its card or row to open its gallery, or right-click for **Read posts**, **Open in gallery** and **Export as HTML**. Downloads thread rows also open their gallery on double-click; board rows retain their details action. Opening local content never initiates a download. Saved media remain usable offline; post reading requires captured posts.

## Images, search and navigation

The gallery uses the window's full work area. Left/Right or its navigation buttons
move between images. Use Fit, 1:1, +/− or the mouse wheel to zoom; drag a zoomed
image to pan. Double-click the image to switch Fit/1:1. Escape closes the gallery.
Show context to read the associated saved post and available local references.
A media-only item can have no captured post.

Archive offers thread cards/table and saved-media rows/tiles. Double-click or
Enter on a focused media row/tile opens that exact item, using the current media
sort and version filter. Text fields and action buttons retain their own keyboard
actions. Historical versions are labelled; they need not match the latest post.

Search operates on retained local titles, post text and identities. Source/board
filters work even without search text. **Images of results** freezes the found
threads in their current sort order across **all result pages**, then opens their
available images, including images from posts that did not match the text.
Double-clicking one result thread opens only that thread. Changing filters or
new archive arrivals does not silently replace an already opened sequence;
close and reopen to take a new result snapshot.

Built-in image views support PNG, JPEG, GIF, WebP and BMP when the decoder accepts
the file. Animated files show a still first frame; the gallery is not a video or
audio player. Files above 64 MiB or 80 million source pixels are refused by the
gallery. Fit decoding is bounded to 16 million pixels; 1:1 may need substantially
more memory. Corrupt, missing, unsupported or unverifiable files show a reason.
The verified originals remain on disk even when preview is unavailable. These
viewer limits are separate from download rules and do not cap total archive size.

## Missing media and finished threads

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

## Portable HTML export

Right-click an Archive thread or use Export in its reader. The compact dialog
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
