# Six-source live results — 2026-10-24

All six supplied sources reached real provider HTTP through their actual bundles,
HostHttp and the productive downloader. **Three selected sources passed download,
archive and offline verification; three selected originals failed integrity.**
The overall six-source live acceptance remains **FAIL**. Each live run contains
one actual executed xUnit case (FAIL, zero skips), with separate per-source results.
This was a local Linux-x64 source-built check. These are bounded observations
of selected threads, not a provider-wide health
or historical-completeness guarantee.

The UI names are 4chan, 8kun, Lainchan, Kohlchan, Wizchan and Endchan. Display-name
changes preserve IDs, routes, capabilities and technical `plugins/Examples/` paths.
The local source checkpoint is `749d660f0e826177032a2630e4f53e1431b8a49c`;
the executions retain the distinct earlier source freezes below.

## Actual per-source outcomes

All times are UTC on 2026-10-24. Counts are catalog entries / captured posts. Each listed attempt
sent exactly three GETs: catalog, thread and the single selected original;
both metadata responses and the original returned HTTP200. Catalog selection
and parsed attachment identity matched in every row.

| Source / board / thread | Actual UTC window · freeze | Catalog / posts | Original received / expected bytes | Archive and offline | Outcome |
| --- | --- | --- | --- | --- | --- |
| 4chan · g / 110148777 | 10:31:22–10:31:47 · LIVE03 | 151 / 5 | PNG 43,918 / 43,918; MD5 matched | Exact SHA-256 archived; offline media PASS | **PASS** |
| 8kun · tech / 1087224 | 10:31:47–10:32:08 · LIVE03 | 17 / 3 | PNG 96,313 / 96,313; MD5 matched | Exact SHA-256 archived; offline media PASS | **PASS** |
| Lainchan · sec / 28669 | 09:59:00–09:59:06 · LIVE01 | 90 / 7 | PNG 273,676 / 273,676 | Exact SHA-256 archived; offline media PASS | **PASS** |
| Kohlchan · cyber / 1730 | 09:59:06–09:59:55 · LIVE01 | 136 / 4 | JPEG 4,759 / 4,845 (−86) | No verified archive; offline metadata only | **Integrity FAIL** |
| Wizchan · hob / 71222 | 09:59:55–10:00:44 · LIVE01 | 95 / 6 | JPEG 89,933 / 90,011 (−78); MD5 mismatch | No verified archive; offline metadata only | **Integrity FAIL** |
| Endchan · operate / 30442 | 10:32:08–10:33:10 · LIVE03 | 160 / 3 | JPEG 33,534 / 33,552 (−18) | No verified archive; offline metadata only | **Integrity FAIL** |

Offline means a **fresh DesktopSessionHost in the same test OS process**, paused
with rejecting HTTP transport; zero offline sends. This does not establish a new
OS-process restart. Integrity-failure rows retained readable metadata but did not
obtain productive media completion, verified archive bytes or offline-media PASS.
The existing size/hash guards remain intact.

## Experimental-source consent follow-up (Freeze09)

The later **three-source opt-in check passed** on Freeze09: one executed xUnit
case, zero skips, on local Linux-x64. This separately verifies the selected
Kohlchan, Wizchan and Endchan originals with explicit permission; it leaves the
historical strict failures and six-source result above unchanged. 4chan, 8kun
and Lainchan were not rerun in this follow-up.

For each source, strict default checking first produced **NeedsIntervention**
with no accepted attachment binding. The real host permission was then enabled
for that profile/plugin owner/source, followed by one manual missing-media retry.
The second original response had exactly the same bytes as the first. Provider
metadata stayed unchanged; the concrete attachment revision retained separate
actual values and an explicit-permission acceptance record.

| Source / board / thread · post / position | Provider → actual bytes | Accepted at UTC · outcome |
| --- | --- | --- |
| Kohlchan · cyber / 1730 · 1730 / 0 | 4,845 → 4,759 | 18:16:04 · **PASS** |
| Wizchan · hob / 71222 · 71222 / 0 | 90,011 → 89,933 | 18:16:12 · **PASS** |
| Endchan · operate / 30442 · 30443 / 0 | 33,552 → 33,534 | 18:16:19 · **PASS** |

After permission was revoked, the verified local media remained readable in the
Gallery and in HTML export. The offline check reopened a fresh paused
`DesktopSessionHost` in the same process with an HTTP transport that rejected
all calls. The follow-up covered the selected attachments on Linux-x64.
