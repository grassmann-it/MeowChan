# Collection workflow — current preview

**Status:** unreleased working-copy preview. This page describes only features currently exposed in the user interface. It is not release readiness documentation, a complete import/export solution, a Grabber replacement, or a native platform acceptance record.

## Offered now

MeowChan keeps saved Chan media local. You can view and navigate it in Archive and Gallery and mark individual media as favorites. Threads can also be marked as thread favorites. A favorites-related Archive overview can search existing favorites by current thread, selected threads, filtered results, or the whole archive; check them locally; view them; remove them; and export the displayed originals. Local viewing and checking alone does not start a download.

There is still no Booru search or collection workspace, universal collection browser, or import/apply flow.

## Selecting and favoriting in Gallery

These are the default gallery bindings; saved shortcut customizations take precedence. M/U/F also work while gallery controls are hidden:

| Key | Action |
| --- | --- |
| **M** | Explicitly add the current medium to favorites |
| **U** | Explicitly remove the favorite mark from the current medium |
| **F** | Toggle the favorite mark (compatibility binding) |
| **↑ / ↓** | Increase or decrease video volume, only with usable audio output |

The upper-right indicator shows **★** for a saved favorite and **☆** for an item that is not a favorite. **…** means a save is pending. The indicator remains visible while gallery controls are hidden. It is display-only and is not clickable. While the persisted state of a newly selected medium is still unknown, MeowChan does not claim that it is unfavorited.

The favorites overview resolves the chosen scope through saved local manifests. Missing or unverifiable files remain favorites but are reported unavailable and are not exported. The original-file export freezes the displayed selection, reports missing files before copying, and copies only locally verified files. It starts no network retrieval. The favorite marks themselves are not transferred to another profile by this operation.

## Inspecting a transfer package — inventory only

In the Archive header, **Inspect transfer package…** opens a local ZIP file of your choice. MeowChan validates it with the production package reader and shows a bounded inventory: threads, posts, unique payloads, byte size, and the status of contained originals, previews, and retained bindings.

This feature is deliberately preview-only:

- Nothing is imported into, applied to, merged into, or deleted from the archive or profile.
- Archive, profile, stores, and the source package remain unchanged; there is no Apply command.
- Private transfer metadata is still rejected.
- No source provider or network is used.
- Inspection writes a temporary staging copy in its own work directory, then attempts to remove its staging files and directories. Inspection and finalization wait for that cleanup, which can block completion. Retained temporary data or an uncertain cleanup state is reported. Archive data is not changed by this process.
- A 404 supplemental import or existing-library merge is not included.

This surface also does not create a transfer package. Internal archive and curated package producers are currently technical core components without an export dialog.

## Video volume, mute, and repeat

**↑/↓** changes volume only for a video with usable audio output. Volume stays in the 0–100 range, is saved, and does not switch between Playing and Paused. A positive user volume change unmutes; volume 0 alone does not make an independent mute/unmute decision.

Mute/unmute and repeat are controls in the video area. By default they have no additional dedicated key bindings. A manual mute or unmute choice survives previous/next and image/video changes within the same open gallery. Reopening a gallery returns to the saved **Start videos muted** preference rather than the last manual mute choice.

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

English and German remain the supported built-ins. **Español (Untested)**, **Français (Untested)**, and **Русский (Untested)** are bundled as selectable previews; no separate pack download or profile installation is required. Merely loading them does not change a saved language choice.

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

## Not offered in this preview

The broader collection direction is still work in progress. The following are **not** exposed user features:

- **Booru:** no Booru site search, source admission, collection intake, or Booru library is offered. A bounded internal Danbooru query grammar exists; it is not a control, reads no private tags, and is not a provider, API, or network adapter.
- **Private tags:** bounded internal tag models exist, but there is no tag UI, persistent private tag collection, tag search, or tag transfer. Token chips in Rules are rule components, not private tags.
- **Curated export:** the internal selection and projection core is not exposed. There is no user-facing “favorites plus small previews” exporter and no private-metadata round trip.
- **Native import and 404 merge:** internal preparation and comparison work exists, but there is no user import or app flow that imports a package and supplementally merges existing or 404 threads.
- **Foreign collections:** no Grabber, Hydrus, gallery-dl, or other foreign-collection compatibility is claimed.

Existing HTML and original-file exports remain separate output features. They are not a complete application round trip, profile backup, transfer-package creator, or import promise.
