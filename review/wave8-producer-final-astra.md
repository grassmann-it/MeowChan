# Wave 8 — final bounded P08e producer integration review (Astra)

## Verdict

**Suitable for a coherent local producer checkpoint; no concrete remaining P0/P1/P2 defect established in the reviewed scope.** This is a static, family-scoped conclusion, **not** completion of GP12/13, private Apply approval, allwriter quiescence, or release/platform acceptance.

The owner-exclusive handoff, startup partial-activation rollback, and corrected WorkState continuation calls form one coherent slice. Keep the previously fixed continuation cores and their regression tests with this integration; do not restore the nested admitted Store calls to make the patch smaller. No additional code change is required by this review before Root's bounded checkpoint.

## Basis and boundaries

- Review timestamp (UTC): 2026-11-03T21:18:39.736621+00:00.
- Compared live bytes with `evidence/source-e071df7849ac87400fd64d122b0d0226e72359523d18593de3738a0eca3b7311.json` and `evidence/source-8127e62a63d467e4204eb35ab62c637e272dfcfb3bac18b3d0137c7f8a985ce1.json`, under `artifacts/goal-runs/2026-11-01-profile-a/`.
- **All 25 exact paths in the appendix are live = e071df = 8127**, rehashed immediately before writing. Both manifests record base `c71493baec168c16f7d0b21b134429d7f518ecbd`; that is manifest metadata, not an independently inspected current Git HEAD. No staged/unstaged classification is claimed.
- Contracts: P08b `profile-admission-kernel-v1.md`, P08c `desktop-profile-admission-v1.md`, P08d `private-store-admission-v1.md`. The P08e owner-handoff report is a bounded **proposal**, not an independently binding acceptance ledger. The architecture index's older wave3 status is not current execution evidence.
- Reads, static call-path review and SHA-256/XML inspection only. **No Git command, build, test, snapshot script, RUNSTATE/lease/pointer modification, or code/test edit. Sole write: this report.**

### Important live-tree drift, excluded from this checkpoint verdict

`Desktop/DesktopSessionHost.StartupPreview.cs` is **not** identical to either comparison manifest: live SHA-256 observed `89c401ef67ebe702cb26e04252db7b9c606ec6b5837d9d619f687634becdf892`, versus manifest `98abe2c102bf403f826e55e1424a301b11313d139b6d349a513fc9e6a8ac70dc`.

The read-only diff is a test reader seam and a `PlatformNotSupportedException` fallback for the disposable startup display cache. It does not change the reviewed producer-admission methods, but **the 8127 Desktop result must not be labelled a full live-tree result**. Keep that separate startup fix in its own evidence/integration lane. This observed drift is not a producer blocker and does not require rerunning unchanged producer code solely because an unrelated overlay exists.

## Reviewed invariants

### 1. Owner-exclusive suspend → drain → resume

`Registry.ProfileProducerAdmission.cs:235–315` acquires the owner transition semaphore, reserves Exclusive while the old coarse Ordinary epoch is still counted, and only then closes Registry ingress. Synchronous reservation refusal leaves the live epoch/ingress intact. The Desktop owner wrapper (`DesktopSessionHost.ProfileAdmission.cs:37–42`) goes directly to this lane: it does not accept an Ordinary command or hold `_commands` while waiting for earlier Ordinary commands.

Queue suspension cancels the transfer generation without setting permanent `_stopping`, suppressing owners, or deleting parked descriptors. Monitor stop/drain is outside Registry `_gate`. The owner waits for epoch executions, tracked background completion, and the final completion pass with the **old** authority before detaching and disposing that epoch. Only afterwards can the awaited Exclusive reach the supplied operation.

This is stronger than queue idle or `_workersDrained`: `DownloadQueue.cs:1218–1366` retains the captured producer use through result persistence, notifications, requeue/accounting and the worker finally. Scheduler passes retain their scope while handling expiry/transit, return due work to logical scheduled state before dropping the scope and awaiting enqueue, and dispose through `finally` (`529–648`). Parked waiters are not execution pins; shutdown retains its separate logical-transition drain.

`Registry.Track` (`1000–1030`) registers the placeholder before dispatch, captures the specific epoch, and cleans up both the use and placeholder on dispatch/completion failure. Removal callbacks reject old epochs rather than borrowing the current one. Monitor lifetime removal uses compare-exchange (`ProfileProducerAdmission.cs:191–200`), so a late duplicate stop continuation cannot release a successor's use.

After Exclusive disposal, a **fresh** Ordinary epoch is installed before activation. Work cache invalidation/rebuild, durable queue revalidation, callback rebinding (including paused watches), and dispatch reopening happen in that order (`82–138`). User pause remains separate from internal suspension. A competing Exclusive that wins the fresh-epoch race leaves a null-epoch suspended family that the owner lane can retry; it does not bypass the kernel or throw away work. Permanent stop flags prevent late installation/reopening.

### 2. Partial activation failure

Startup installs Ordinary only after releasing Startup and completing startup admission. Activation runs outside Host `_lifecycle`, and Host Ready publication is guarded by the lifecycle state (`DesktopSessionHost.cs:160–222`). A failure after one monitor has started closes local ingress and invokes `BeginShutdown` **before** potentially blocking queue/monitor rollback (`Registry.ProfileProducerAdmission.cs:113–136`). Thus another direct Store writer cannot slip into the rollback interval.

A monitor's use is retained until its actual `StopMonitoringAsync` succeeds. The epoch is not prematurely disposed on partial failure. Host startup failure then calls Registry Stop; normal shutdown drains admission before disposing owners/releasing the OS lease. If a descendant fails to drain, retaining ownership/failing shutdown is safer than announcing quiescence. This review does **not** promise bounded completion for an uncooperative provider.

Owner-resume failure deliberately differs from startup failure: it leaves a suspended retained family without permanently sealing the whole Ready profile. A later owner attempt or permanent Stop must settle that retained epoch. This is not permission to continue private Apply after a failed resume.

### 3. Canceled Exclusive

Static paths checked against `ProfileAdmission.cs:113–131,251–309` and Registry owner `235–315`:

- Cancellation before transition entry/reservation does not create an owned grant or close live producer ingress.
- Cancellation during suspension does **not** abort final persistence/drain halfway. The reservation is eventually observed; successful quiescence may resume with a new epoch if still Ready.
- A token granted before cancellation remains an owned token. Registry assigns it before checking cancellation and always disposes it in `finally`; a pre-operation canceled request does not call the operation.
- Once the operation has started, the request token is not a forced revocation mechanism. The operation is awaited; cleanup/disposal/resume follow its actual completion.
- Drain failure cancels and observes the pending reservation while retaining the old epoch. Shutdown rejects a pending waiter, and stop suppresses resume. A competing reservation at resume is a surfaced retryable refusal, not successful reopening.

**Evidence limit:** the inspected producer tests cover parked-caller cancellation and resume/stop races; these must not be advertised as dedicated tests of cancellation of the owner-exclusive request itself. Kernel cancellation tests plus this static owner-path review are not a replacement for a deterministic end-to-end owner-cancel matrix.

### 4. Nested admission pins and dirty continuation

`WorkStateAccess.cs:83–240` accepts one outer use and invokes the SQLite **continuation cores** directly for media, intent, state and end. In particular the three c714-era regression paths no longer call `TryCommitMediaAdmitted` / `CommitIntentUnderFence(..., token)` / `CommitStateUnderFence(..., token)` inside the already accepted use. The earlier End correction also remains present.

The cores (`WorkStateStore.cs:201–365`) still validate/CAS/commit under the live lease monitor. They skip a second token acceptance, **not** the lease or transaction checks. The external core callsite search found only WorkStateAccess's four intended calls; public/admitted wrappers retain their entry gates. The instance observer is inside `RunWithToken` and before the delegate, with the enclosing finally releasing the pin even if the observer throws.

Ambiguous-result confirmation and `MarkDirty` remain within the outer accepted call. Successful media/intent and non-null state/end results mark dirty; failed/null results do not falsely claim a commit. The Registry final old-epoch completion pass plus full cache invalidation at resume preserves the continuation obligation rather than using a tokenless late writer. There is no static regression found in this marking path.

**Evidence limit:** the six continuation regression cases assert persisted rows and release/Exclusive/drain behavior, not `IsDirty`/`TakeDirty`. Dirty-after-release remains a static finding here, not a newly established dedicated runtime assertion.

### 5. Session/policy writes and Desktop plumbing

`SessionStore.SaveAsync` owns a local token through the awaited task. `SaveAdmittedAsync` explicitly borrows a command/producer-owned reservation: its immediate validation is **not an async pin**; the outer owner retains the token through dispatch, while `RunStoreWrite` pins the synchronous lease wait/publication/cleanup. The reviewed callers await it before releasing their operation/epoch. Do not detach that task or dispose its borrowed token after the preflight.

`BoardAdmissionStore.SaveAdmitted` pins serialization, atomic publication and in-memory policy replacement under the lease. Host command/control dispatch retains accepted tokens through mutation/projection/cleanup. Board-policy reactivation and nested Add, archive suppression/removal/resume and WorkState/session writes propagate that same authority, not a new Ordinary admission after Exclusive has become pending.

## Evidence that can be reused — not rerun here

Read back `reconciliation-wave8-baseline-01.json` and the two referenced full-suite receipts/TRX/logs. Their recorded receipt/log/TRX SHA-256 values match the local files:

| Existing run | Bound source | Observed result |
| --- | --- | --- |
| `wave8-curated-core-all-01` | 8127e62a… | exit 0; 4460 passed; 4462 total (2 existing skips) |
| `wave8-curated-desktop-all-01` | 8127e62a… | exit 0; 2137 passed; 2145 total (8 existing skips) |

The Desktop run's own receipt records the matching updated Core DLL `8687e671…`, not the older Desktop-copy DLL listed incidentally in the preceding Core run's broad output inventory. No stale Desktop-copy entry is promoted into execution evidence for the Core test.

The scoped producer/WorkState/test hashes match that source and e071df. These results therefore remain relevant prior regression evidence for the unchanged producer slice; they are not new tests of every live overlay. The earlier wave7 fixed/negative continuation analysis remains useful causal evidence. `wave7-continuation-focused-02` and `wave7-core-final-build-01` remain excluded by the incremental-restore correction; do not resurrect them as fixed-source evidence or a fresh product failure.

## Remaining gates and exact safe integration recommendation

**Concrete P0/P1/P2 in this bounded static review: none established.** The following are explicit limits/follow-ups, not invented product defects or reasons to block this local checkpoint:

1. Keep Registry/Queue epoch ownership, Session/Board admitted writes, Desktop startup/command plumbing and the WorkState core corrections coherent. Root may checkpoint the producer family and matching tests together on top of the existing continuation fixes. Do not land only Store wrappers while excluding the owner drain/resume path.
2. Preserve the scoped source identity above and the prior execution receipts. Do not stage unrelated startup-preview, curated-transfer, URL-boundary, plugin/UI or protected user changes merely because they share the live checkout. This review did not inspect Git staging; Root must select its own commit contents.
3. Before promoting the internal owner lane into a public/product coordinator, add explicit owner-request cancellation-before/after-grant and resume-failure scenarios, and preferably dirty-after-release assertions. Existing proposal test matrices are not claimed completely satisfied by the full-suite totals.
4. Keep the coarse Ordinary epoch's limitation explicit: raw kernel Exclusive can wait indefinitely while that family is live/idle/paused; use the owner wrapper, never an Ordinary command around it. Stop/drain failure is not permission to force-release the epoch or lease.
5. Direct independent Catalog/SQL/Media/plugin/config/final-write paths, persistent owner fences, consent/private Apply/recovery/retirement and overall transfer acceptance remain separate. The StartupPreview final-write window and provider-specific shutdown behavior are not converted into allwriter safety by this patch. No native Windows/macOS claim.

**Decision: bounded producer checkpoint may proceed; GP12/13 and private Apply/allwriter acceptance remain open.**

## Exact matching scope (live = e071df = 8127)

| Path | SHA-256 |
| --- | --- |
| `libmeowchan/Registry.cs` | `8d4db202cd158ab21d1c287cb9e93a2a34f97d3fa9088c71a66072793c375781` |
| `libmeowchan/Registry.ProfileProducerAdmission.cs` | `26212d3415e632e7d0855eb77e905d522b76016dd18c6b83d67bbb1d28769fc7` |
| `libmeowchan/Registry.WorkCache.cs` | `7931b1364e7de2dcb43775449fff5cf95fa2adeb8a4df42ac7fbeb62e1733101` |
| `libmeowchan/Registry.BoardAdmission.cs` | `c45c91a28403523bae1a912a6ef4bcd821d30bdc1174d344e02dec3a5fd39506` |
| `libmeowchan/Registry.ArchiveDeletion.cs` | `0021bc652ec5b7bab474f208fd23fe0fcbd3c92493a1e7c5b80d7de1bf5cb95e` |
| `libmeowchan/Downloader/DownloadQueue.cs` | `98995370e3a45e72fbff3ad52cb933d0e9dbce34542ba72f2f8a1a43ee6f34bc` |
| `libmeowchan/Downloader/DownloadQueue.ProfileProducerAdmission.cs` | `f1edca331eba26e78fd590b57bd1c94ee63eed6c2f391795504da244429650b8` |
| `libmeowchan/Downloader/DownloadQueue.WorkTransitions.cs` | `21d0514e08fde5aeec83d4bae217bdf8b7bd8a7f5dd41647969eb0401957909a` |
| `libmeowchan/Persistence/ProfileProducerAdmission.cs` | `ad163c2d15ebf92c1f41d1f53b53d9839e2a588097404805033e316f5144ff4b` |
| `libmeowchan/Persistence/WorkStateAccess.cs` | `b1ba759751996f153ed7df37fc90d517585e7cb813b45cf1f1aeffd3dacb65a4` |
| `libmeowchan/Persistence/WorkStateStore.cs` | `ee157577437acb382698075144ec5f1991fb01cc3bc5220bef1ab01f105a8627` |
| `libmeowchan/Persistence/SessionStore.cs` | `c780caf43ce47f4f682c90de409610bde9190dac9dedf7504d842dfa26d85824` |
| `libmeowchan/Admission/BoardAdmissionStore.cs` | `f1913bae2c745f16b97ade34c71ad0d08ac56e53ccf7ceea8d66946546dc872c` |
| `Desktop/DesktopSessionHost.cs` | `af29d663316ecd4c4492e9807dbe2745ec1dec70e41601d149d578e96e073173` |
| `Desktop/DesktopSessionHost.ProfileAdmission.cs` | `1d5c9d88fab0c161baeeb37f4f8afd7c3e9e964dade0b69ef4b767e1e64df1d2` |
| `Desktop/DesktopSessionHost.Commands.cs` | `1f1ccac9a6432d13cec1fa143baa0272d15a70169ec1b874bd55ffbb2a5ff6c4` |
| `Desktop/DesktopSessionHost.BoardAdmission.cs` | `15b52752caea379f88e865f0f7daf5f9a6fb423a5aa95efd24a3636c3470faa5` |
| `Desktop/DesktopSessionHost.ArchiveDeletion.cs` | `82c23381a5127284df593e5a21219f9459d2d1496343602f25596fafab163b08` |
| `libmeowchan/Persistence/ProfileAdmission.cs` | `113a1064e6ae1a6c3d71c634b5e7a31af5b6769520827b46127627de5da98b09` |
| `libmeowchan/Persistence/ProfileLease.cs` | `11050d10167df2e4b93ef008562ddd9f56af06020563b24a02d71cd1cd0dd5b5` |
| `tests/MeowChan.Tests/ProfileProducerLifecycleTests.cs` | `a8235420e3306e703e0879e57dff7196f3cf8053f7b9978c04bbf2faae165a31` |
| `tests/MeowChan.DesktopTests/ProfileProducerDesktopAdmissionTests.cs` | `dded4ae1cb8b0da1e14e122dd7470b03d8c9a90b72a755846c1d67489324566f` |
| `tests/MeowChan.DesktopTests/ProfileProducerPartialActivationFailureTests.cs` | `24fd2cd7438682f4aee7825ca7bf3c7c9bf46d6254f8fbcf4df3db282cb20ab7` |
| `tests/MeowChan.Tests/WorkStateProducerAdmissionTests.cs` | `ef836a847e925c74b30e742d1bba0dc326fad7b1214f1efb37a556253bf7561a` |
| `tests/MeowChan.Tests/WorkStateContinuationReleaseRegressionTests.cs` | `3eaeb2ff52b6e088e9b1865b20a4a35010ab6fe64ec0ef86be653966a485ca0e` |
