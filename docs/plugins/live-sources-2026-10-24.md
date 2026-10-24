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

All times are UTC on 2026-10-24, rounded to seconds here; raw receipts retain
full precision. Counts are catalog entries / captured posts. Each listed attempt
sent exactly three GETs: catalog, thread and the single selected original;
both metadata responses and the original returned HTTP200. Catalog selection
and parsed attachment identity matched in every row.

| Source / board / thread | Actual UTC window · freeze | Catalog / posts | Original received / expected bytes | Archive and offline | Outcome / raw receipt |
| --- | --- | --- | --- | --- | --- |
| 4chan · g / 110148777 | 10:31:22–10:31:47 · LIVE03 | 151 / 5 | PNG 43,918 / 43,918; MD5 matched | Exact SHA-256 archived; offline media PASS | **PASS** · [receipt](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live03/live-results/4chan.json) |
| 8kun · tech / 1087224 | 10:31:47–10:32:08 · LIVE03 | 17 / 3 | PNG 96,313 / 96,313; MD5 matched | Exact SHA-256 archived; offline media PASS | **PASS** · [receipt](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live03/live-results/8kun.json) |
| Lainchan · sec / 28669 | 09:59:00–09:59:06 · LIVE01 | 90 / 7 | PNG 273,676 / 273,676 | Exact SHA-256 archived; offline media PASS | **PASS** · [receipt](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live-results/lainchan.json) |
| Kohlchan · cyber / 1730 | 09:59:06–09:59:55 · LIVE01 | 136 / 4 | JPEG 4,759 / 4,845 (−86) | No verified archive; offline metadata only | **Integrity FAIL** · [receipt](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live-results/kohlchan.json) |
| Wizchan · hob / 71222 | 09:59:55–10:00:44 · LIVE01 | 95 / 6 | JPEG 89,933 / 90,011 (−78); MD5 mismatch | No verified archive; offline metadata only | **Integrity FAIL** · [receipt](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live-results/wizchan.json) |
| Endchan · operate / 30442 | 10:32:08–10:33:10 · LIVE03 | 160 / 3 | JPEG 33,534 / 33,552 (−18) | No verified archive; offline metadata only | **Integrity FAIL** · [receipt](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live03/live-results/endchan.json) |

For the three PASS rows, network and archived SHA-256 are identical:

| Source | Network and archive SHA-256 |
| --- | --- |
| 4chan | `fa4e113a64dce980119037b8cc60f407d6b8224d061ac9e80347397f54c5b94f` |
| 8kun | `ee7b669dfc4f1599ca06cd43a17a46681ca7f08d4434b6b1b94c4ed72902bacd` |
| Lainchan | `29466323b10bcbc824e74a6c24f104d4c9e7aff9cbe13e23d9da8140c9faf4e6` |

Offline means a **fresh DesktopSessionHost in the same test OS process**, paused
with rejecting HTTP transport; zero offline sends. This does not establish a new
OS-process restart. Integrity-failure rows retained readable metadata but did not
obtain productive media completion, verified archive bytes or offline-media PASS.
The existing size/hash guards remain intact.

## Binding and bounded execution

The linked sibling execution files are local evidence outside this repository;
they are not included in Git delivery. See the [execution overview](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/FINAL-REPORT.md)
and [final evidence binding](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/final-evidence-binding.json)
for the separated live, preparation and GUI results.

The host used `DesktopSessionHost` with `providers:null`, real HostHttp transport,
an exact-URL audit gate and isolated profiles. Per source and attempt, the budget
was one catalog GET and one thread GET (each ≤8 MiB), then one original (≤2 MiB).
No retries, HEAD/Range probes, redirect chains or silent replacement sources were
used. The separate authorized LIVE03 subset repeated 4chan, 8kun and Endchan;
Lainchan's PASS and Kohlchan/Wizchan's failures retain LIVE01 provenance.

| Freeze | Source digest | Actual result / closure |
| --- | --- | --- |
| LIVE01 | `26896008a8b9f4b9ce964905177347d10d94b8d1718808e643d62a5ec0531164` | [Six-source summary](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live-results/summary.json), [closure](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live01-first-run-status.json): 13 GETs (10 metadata, 3 originals), six profiles removed |
| LIVE03 | `3dd876ea24f068941906b353c1df53e76cc4ea65ef34081b5c373ac583308ddc` | [Three-source summary](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live03/live-results/summary.json), [test binding](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live03/checks/live/binding.json), [closure](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live03/closure-checkpoint.json): 9 GETs (6 metadata, 3 originals), three profiles removed |

The [selection manifest](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live-source-manifest.json) binds the expected
original sizes and selected endpoints. Each per-source receipt binds
SourceId/PluginId, selected board/thread/media,
bundle-manifest SHA-256, delivered and active assembly SHA-256, ExecutionRevision,
actual HTTP endpoints/status/bytes/hashes, UTC, acquisition and cleanup results.
Delivered and active assembly hashes match for all six listed outcomes.
Exported test/worker PIDs are LIVE01 Lainchan 53226/54260 and LIVE03 4chan
87087/87468, 8kun 87087/88080. Kohlchan/Wizchan worker PIDs and Endchan's worker
PID were not exported; they remain unknown. Closures report no remaining source
runtime processes, no cleanup errors and all exported PIDs ended.

## Preserved partial attempts and diagnosis

The [first-run summary](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/live-results/summary.json) preserves 4chan's
09:57:26–09:58:13 catalog HTTP429 with no later send, and 8kun's
09:58:13–09:59:00 catalog200 without a thread send before timeout. The later
LIVE03 successes do not establish the cause of the earlier 429/admission timeout.
Endchan's 10:00:44–10:01:33 first attempt parsed metadata but sent no original:
a narrow original-filename filter was removed from the test before LIVE03,
which reached the original GET. The first result did not record that filter as
the cause, so the exact earlier no-send cause remains unproved. It does not
establish a provider media failure. LIVE03 exposed the actual size mismatch.

Separately authorized [identity diagnostics](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w20/identity-diagnosis/results/summary.json)
made one original GET each for Kohlchan and Wizchan, outside the first-run budget.
With `Accept-Encoding: identity` and decompression disabled, both returned the
same sizes and hashes as above. They are diagnostic observations, not new
productive acquisition passes. Wizchan also disagrees on MD5: API
`023KrHT15Yw35hsJU/Paxg==` versus received `trT2lvNT8ShmM+pDl0m3jQ==`.
Upstream upload code suggests conditional
post-processing after size/hash capture, but the deployed revision/configuration
and exact cause remain unproved. Endchan's parser maps documented byte size
unchanged; its Content-Length was 33,534. A general LynxChan post-processing
hypothesis is not bound to Endchan's deployed fork. [Local conclusion](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w21/endchan-live03-conclusion.json),
[upstream analysis](../../../oChan-ursa-execution-20261023/post-ursa/live-sources-20261024/w21/upstream-cause/report.json).

Host-boundary tests (6 PASS on LIVE02, retained by exact continuity) and audit
tests (22 PASS on LIVE03, zero skips) are preparation checks, distinct from these
live outcomes. The separate Continue reading GUI acceptance is recorded in
[STATUS](../architecture/1.0.0/STATUS.md); its later test-only fixture corrections
do not rebind any LIVE01/LIVE03 live result. No new publish, broad API/NuGet/ABI,
full URSA, native platform or release acceptance follows from this report.

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

| Source / board / thread · post / position | Provider → actual bytes | Accepted at UTC · outcome / receipt |
| --- | --- | --- |
| Kohlchan · cyber / 1730 · 1730 / 0 | 4,845 → 4,759 | 18:16:04 · **PASS** · [receipt](../../../oChan-ursa-execution-20261023/post-ursa/source-integrity-implementation-20261024/w20/freeze09/live-once-normal-flow/evidence/kohlchan.json) |
| Wizchan · hob / 71222 · 71222 / 0 | 90,011 → 89,933 | 18:16:12 · **PASS** · [receipt](../../../oChan-ursa-execution-20261023/post-ursa/source-integrity-implementation-20261024/w20/freeze09/live-once-normal-flow/evidence/wizchan.json) |
| Endchan · operate / 30442 · 30443 / 0 | 33,552 → 33,534 | 18:16:19 · **PASS** · [receipt](../../../oChan-ursa-execution-20261023/post-ursa/source-integrity-implementation-20261024/w20/freeze09/live-once-normal-flow/evidence/endchan.json) |

Times are UTC on 2026-10-24, shown to whole seconds; receipts retain full precision.
None of these three attachments supplied provider SHA256. Their separately
measured local hashes were:

| Source | Provider MD5 → actual MD5 (Base64) | Actual SHA256 |
| --- | --- | --- |
| Kohlchan | not supplied → `Pryeq1uF0apG7mkxD9p7Cg==` | `7f664113ba4d9f4514cde8bb56f0b2efe646ae75a3e7b3deb49c04545100d941` |
| Wizchan | `023KrHT15Yw35hsJU/Paxg==` → `trT2lvNT8ShmM+pDl0m3jQ==` | `5a401d768f9846ca5b53d5268c799d7fd6b047e256411505a5fb1d85ee85f9d0` |
| Endchan | not supplied → `MTOChsbTElHWBmjM0GCHIg==` | `a16bcd2903de0570910c3749da0ed5feda999d8879a7567505f3139e87a50b32` |

Permission was revoked after acceptance. Local verified reads, Gallery selection
of the exact bound asset and HTML-export bytes then passed both before and after
opening a **fresh paused DesktopSessionHost in the same OS process**, with
rejecting HTTP transport and zero offline attempts. All three private profiles
were deleted. The separate [Freeze06 Core process proof](../../../oChan-ursa-execution-20261023/post-ursa/source-integrity-implementation-20261024/w20/freeze06/os-proof/summary.json)
passed in two actual OS processes with synthetic bytes and a fresh denying test
policy in the second; its scope excludes Desktop/SDK/live-source process restart.

Freeze09 used exactly **12 HTTP200 GETs**, four per source: one catalog, one
thread, one strict original and the same original once after permission. There
were no automatic retries, fallback originals or denied extra requests. The
source-integrity task total is **22 GETs** across the following preserved attempts;
the earlier LIVE01/LIVE03 and identity-diagnostic requests above are separate.

- [Freeze06](../../../oChan-ursa-execution-20261023/post-ursa/source-integrity-implementation-20261024/w20/freeze06/live-once/binding.json): **FAIL**, zero GETs; catalog work remained paused before network access.
- [Freeze08](../../../oChan-ursa-execution-20261023/post-ursa/source-integrity-implementation-20261024/w20/freeze08/live-once-repair/evidence/summary.json): **FAIL**, ten GETs (4 + 4 + 2); Kohlchan/Wizchan failed the final audit after blocked additional thread attempts, and Endchan stopped before an original GET.
- [Freeze09](../../../oChan-ursa-execution-20261023/post-ursa/source-integrity-implementation-20261024/w20/freeze09/live-once-normal-flow/binding.json): **PASS**, twelve GETs; [three-source summary](../../../oChan-ursa-execution-20261023/post-ursa/source-integrity-implementation-20261024/w20/freeze09/live-once-normal-flow/evidence/summary.json).

Freeze09 source digest is
`61cb0dbe51c8e8d8a6904bc6555e11e5d3e19af5ee45f3728d026de45383ca73`.
Its binding SHA256 is
`911a4ad45589f1669faeda3359996652fafd9acc774a82537fb392252164edaf`;
summary SHA256 is
`f3b12d8b42dc08311109309e5c6f85bdad12aec3058f21b17b80729cf7439388`.
These sibling receipts are local evidence outside Git delivery. Provider SHA256,
local integrity and transfer limits remain strict; deployed EXIF/post-processing
causes remain unproved. This bounded follow-up establishes no provider-wide,
new six-source, native-platform or release acceptance.
