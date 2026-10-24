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
