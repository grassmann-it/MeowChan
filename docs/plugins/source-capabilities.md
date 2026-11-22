# Source capability matrix

Status of 2026-11-21, MeowChan 1.1.1 source line. This page is the **required
template for every new source**: before a source is announced, add one row and
fill each column with *yes*, *no* or *not verified* plus the date and the
evidence. A blank cell means "not checked", never "yes".

The matrix describes what MeowChan implements and documents for each source. It is
not a provider-wide health or completeness guarantee; live observations are bounded
snapshots (see [live results 2026-10-24](live-sources-2026-10-24.md)).

## Columns

- **Search by tags**: MeowChan can send a tag search to the source.
- **Link import**: a pasted post, thread or board address is recognised and routed to the source.
- **Profile import**: an account or profile page of the source can be imported as a whole.
- **Originals**: original media can be saved through the normal download queue.
- **Login**: MeowChan can sign in to the source.
- **Direction**: read-only means MeowChan never posts, edits or votes at the source.

## Matrix

| Source | Search by tags | Link import | Profile import | Originals | Login | Direction | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Danbooru (official plugin) | Yes: at most two tags plus one optional `rating:` term; 20 results per page, at most 5 pages | Yes: post links and post IDs; no board or thread routes | No | Yes, on request ("Download original"); nothing is loaded on selecting a result | No (public requests only) | Read-only | [Danbooru guide](../user-guide/danbooru.md), [official plugins](../../plugins/Official/README.md); `libmeowchan/Booru/DanbooruTagQuery.cs` |
| 4chan (official plugin) | No (board and thread watching, no tag search) | Yes: board and thread links | No | Yes, through the download queue | No | Read-only | [official plugins](../../plugins/Official/README.md); live result PASS 2026-10-24 for one selected thread |
| 8kun (official plugin) | No | Yes: board and thread links | No | Yes, through the download queue | No | Read-only | [official plugins](../../plugins/Official/README.md); live result PASS 2026-10-24 for one selected thread |
| Vichan family (Lainchan, Leftypol, SushiChan, Uboachan, Wizchan) | No | Yes: board and thread links of the listed sites | No | Yes, through the download queue; Wizchan: original size mismatch in the 2026-10-24 live check | No | Read-only | [packaged inventory](README.md#packaged-inventory), [live results](live-sources-2026-10-24.md) |
| Jschan family (ZZZchan) | No | Yes: board and thread links | No | Yes, through the download queue | No | Read-only | [packaged inventory](README.md#packaged-inventory) |
| Other board example sources (Kohlchan, Endchan, 2ch, 7chan, BBW-Chan, CapybaraChan, 8chan.moe, Fchan) | No | Yes: board and thread links | No | Yes, through the download queue; Kohlchan and Endchan: original size mismatch in the 2026-10-24 live check | No | Read-only | [packaged inventory](README.md#packaged-inventory), [live results](live-sources-2026-10-24.md) |
| Anon Imageboards, Guro.cx | No | No: blocked placeholders, no routes registered | No | No | No | None | [packaged inventory](README.md#packaged-inventory) |
| Local media folders | No (use the own-tag filter of the Media page) | No (added with **Add folder…**, not by link) | No | Not applicable: files are read in place and never copied, written or moved | Not applicable | Read-only | [Read-only media folders](../Help.md#read-only-media-folders) |

## How to read a row

- *Read-only* is a property of MeowChan's access, not a statement about the provider.
- A *yes* for Originals means the original goes through the normal download queue
  with its size and hash guards; it does not mean every post offers an original.
- Rows for sources of the same engine family share a row only while their
  capabilities and live evidence are identical; split the row as soon as they differ.
- Do not copy a capability from another source or from the engine family without
  checking it for the new source.

## Adding a source

Copy a row, set every column and put the date and evidence into the last column.
A column that cannot be answered yet is written as *not verified (date)*.
A booru post-search source also declares its endpoints, tag syntax, paging limits,
access mode and request spacing in its source profile; see the
[booru source contract](booru-source-contract.md).
