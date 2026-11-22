# Collection workflow — current preview

**Status:** unreleased working-copy preview. This page describes only features currently exposed in the user interface. It is not release readiness documentation, a complete import/export solution, a Grabber replacement, or a native platform acceptance record.

## Offered now

MeowChan keeps saved Chan media local. You can view and navigate it in Archive and Gallery and mark individual media as favorites. Threads can also be marked as thread favorites. A favorites-related Archive overview can search existing favorites by current thread, selected threads, filtered results, or the whole archive; check them locally; view them; remove them; and export the displayed originals. Local viewing and checking alone does not start a download.

## Searching Danbooru

Open **More → Booru** in the main navigation, enter tags, and choose **Search**. You can also open it from **Media → More → Open Booru**. The Danbooru plugin must be enabled and available under **Plugins → All plugins**. If Booru is hidden, turn on **Show Booru actions** under **Settings → Interface → Footer**.

Search returns public Danbooru posts. You can preview the results and explicitly queue selected originals. See [Danbooru search and watchers](../Help.md#danbooru-search-and-watchers) for the controls and local download workflow.

## Searching saved filenames in Archive

In the Archive action bar and Reader toolbar, **Search captured filenames** opens a dedicated dialog. This metadata search is separate from thread text search. Enter text and choose **Search**; use **Previous page** / **Next page** to browse.

- The query is a literal substring of the stored `original_name` claim. Letter case is ignored; input is not trimmed, spaces are preserved, and wildcards plus path or query languages are not interpreted. An empty input explicitly requests every captured name. At most 512 UTF-16 code units and no NUL are accepted.
- Each result page reads its total count and rows in the same catalog transaction. Moving to another page is a new request and can therefore observe a newer committed catalog state.
- Rows show source, board, thread, post, attachment position, variant, attachment/catalog revision, and deletion observations. Deleted observations remain searchable. No file or path is opened; a hit proves neither current media bytes nor current file existence.
- **Return selected metadata** reads the target thread after selection with a fresh passive reader and validates source, board, thread, catalog thread revision, post including deletion state, plus attachment position, variant, original filename, attachment revision, and attachment deletion state. Only after this revalidation is the target post loaded and shown. On a mismatch, the destination is instead reported unavailable locally. There is no first-post fallback, gallery/path/URL reconstruction, file verification, network retrieval, current-filesystem search, global-library search, or similarity search.

## Finding text in a saved thread

In the Reader toolbar, **Find in saved thread** opens text search for the open saved thread: subject and body text, including posts outside the currently visible Reader page. When post, text, or hit limits are reached, the interface reports incomplete results. Empty input does not search; at most 512 UTF-16 code units and no NUL are accepted. Within the search bar, **Enter** moves to the next hit, **Shift+Enter** to the previous hit, and **Escape** closes it. Navigation selects the post; the exact hit is shown in the search bar, even when it exceeds the body display limit.

## Selecting and favoriting in Gallery

These are the default gallery bindings; saved shortcut customizations take precedence. M/U/F also work while gallery controls are hidden:

| Key | Action |
| --- | --- |
| **M** | Explicitly add the current medium to favorites |
| **U** | Explicitly remove the favorite mark from the current medium |
| **F** | Toggle the favorite mark; on a saved favorite it asks before it removes the mark |
| **↑ / ↓** | Increase or decrease video volume, only with usable audio output |

The favorite badge at the upper left, beside **Back to …**, shows **★** for a saved favorite and **☆** for an item that is not a favorite. **…** means a save is pending. The badge remains visible while gallery controls are hidden, and a click on it toggles the mark. While the persisted state of a newly selected medium is still unknown, MeowChan does not claim that it is unfavorited.

The favorites overview resolves the chosen scope through saved local manifests. Missing or unverifiable files remain favorites but are reported unavailable and are not exported. The original-file export freezes the displayed selection, reports missing files before copying, and copies only locally verified files. It starts no network retrieval. The favorite marks themselves are not transferred to another profile by this operation.

## Inspecting and applying a transfer package

In the Archive header, open **Import archive…**, then use **Choose archive…** in the dialog to select an existing local ZIP file. MeowChan validates it with the production package reader and shows a bounded inventory: threads, posts, unique payloads, byte size, and the status of contained originals, previews, and retained bindings. A supported package can then be explicitly imported using the displayed plan.

The Apply path is deliberately bounded:

- Inspection is read-only. Apply is a separate explicit user action against the displayed generation.
- Merging supplements existing threads and reports media conflicts. The importer rejects unsupported packages and stale plans.
- No source provider or network is used.
- Inspection writes a temporary staging copy in its own work directory, then attempts to remove its staging files and directories. Inspection and finalization wait for that cleanup, which can block completion. Retained temporary data or an uncertain cleanup state is reported.
- Apply reconciles receipts and recovery states. Cancellation or a lost response is not misreported as a guaranteed non-apply; retrying the same operation is idempotent.

**Export archive…** in the Archive header creates transfer packages for selected threads or the whole archive. Choose **Metadata only**, **Originals**, or **Everything** for the media scope. The original-file export from the favorites overview is a separate action.

## Video volume, mute, and repeat

**↑/↓** changes volume only for a video with usable audio output. Volume stays in the 0–100 range, is saved, and does not switch between Playing and Paused. A positive user volume change unmutes; volume 0 alone does not make an independent mute/unmute decision.

Mute/unmute and repeat are controls in the video area. By default **Shift+M** mutes or unmutes and **L** switches repeat on or off. A manual mute or unmute choice survives previous/next and image/video changes within the same open gallery. Reopening a gallery returns to the saved **Start videos muted** preference rather than the last manual mute choice.

**Repeat current video** is a saved gallery playback choice. While active, it applies to every selected video until changed and survives an application restart. A slideshow must still be started explicitly.

## Optional clipboard monitoring

**Scan clipboard for URLs** is off by default. Enable it and Save before monitoring begins. Monitoring runs only while MeowChan is open, restored, and ready.

Supported thread/board URLs that are not already watched appear as a passive suggestion. Detection alone adds no watch and starts no download. **Add URLs (Enter)** submits exactly the displayed, frozen batch through the normal Add flow. **Escape** or **Dismiss** rejects it. Enter in the text preview or another unrelated area is not consent. MeowChan does not force itself into the foreground and keeps no clipboard history.

## Requested and effective transfer parallelism

**Media transfer resource limit** bounds simultaneously open media-file transfers and their memory use. It does not bound the number of watched threads or queued files.

| Value | Current default / range |
| --- | --- |
| Requested **Parallel downloads** | Default **5** |
| **Media transfer resource limit** | Default **32**, allowed range **1–256** |
| Effective parallelism | The lower of requested parallelism and the safety limit |

Because requested parallelism starts at 5, the initial effective value is 5, not 32. To permit more than 32 transfers, raise both values. Saving applies the change immediately; lowering the limit does not abort or restart active transfers—they may drain while no new slot above the effective limit is opened.

Source limits, bandwidth, media verification, and disk capacity still apply. Downloads and its tooltip distinguish the configured/requested value from the effective value.

## Languages

English and German are built in. Ten [bundled language previews](../../Desktop/Localization/Packs/README.md) can be selected in Settings without a separate download.

“Untested” means no qualified linguistic review or native UI acceptance. Missing or deliberately untranslated surfaces fall back to English, and some helpers plus native controls can remain English. This is not Windows, macOS, or other platform proof.

## Early inert startup preview

When session restoration is enabled and a clean, complete shutdown produced a display cache, the next start can show a clearly labelled, text-only view of the previous session before restoration finishes. It shows the capture time, total counts, and limited rows containing source, board, title, and media counts.

The startup preview is deliberately inert:

- It contains no URLs, paths, links, thumbnails, or real session objects.
- It creates no watches and initiates no network work.
- Actions and the normal workspace remain unavailable until actual restoration is in charge.
- It disappears permanently after the first complete authoritative data frame.
- A missing, damaged, oversized, or unknown cache is ignored; normal restoration continues.

There is no preview after a first start, crash, or other unclean exit without a suitable cache. This is not a latency guarantee and does not replace restoration.

## Other local collection tools

- **SHA-256:** With a saved thread that has one media inventory, select the **SHA-256** search scope in Archive or Reader and enter an exact hash. It searches recorded hashes in that inventory.
- **Own tags:** Add tags to threads or media and filter the Media page by your own tags. See [own tags](../Help.md#own-tags-and-the-shared-media-menu) for the local tagging tools.
- **Local files:** Choose **Import files…** to [import your own files](../Help.md#importing-your-own-files) into the media library.

A favorites-only transfer package and third-party collection import are outside the current preview.
