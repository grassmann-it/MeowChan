# P07 — durable selected-only private operation journal

Engineering continuation of accepted Profile A G4/G5; not a new reduction of the
full import/merge/consent scope. This slice persists P05 request/evidence using
P06 scalar store fences. It does not call a store, establish receiving consent,
retire markers or open private import/Desktop gates. Integration status requires
source-bound tests and independent review, not this contract.

## Local representation and authority boundary

`NativePrivateApplyRequestCodec.Serialize/Deserialize` roundtrips the existing
validated immutable request. It embeds canonical selected P02 JSON, only aligned
classification integers, scalar binding/fences and both P05 hashes. Rehydrate via
`NativePrivateApplyRequest.Create` and compare both hashes; do not implement a
second request digest. No full target images, old target notes, unselected values,
paths or owner capabilities in any durable component. Exact incoming conflict
notes remain selected payload, not a hash-only substitute for G4 candidates.
The encoded request has a separate 20MiB cap, strict UTF8/no BOM, depth16 and exact
field sets/duplicate rejection. P02's own16MiB and P05's digest20MiB caps still apply.

One fixed `.meowchan-private-apply` parent under the owning ProfileLease; operation
children use exactly lowercase Guid `N`. No caller paths, recursive discovery,
links, automatic cleanup/restore or migration. The journal has no store mutation
capability. A persisted profile-incarnation claim remains passive until the real
owner/catalog/host/lifecycle planner is connected. Hashes detect mismatch, not
hostile local-file tampering/authentication. Arbitrary old/external writers are
outside cooperative guarantees and unexplained drift stops work.

Per operation:
- `claim.json`: version1, operation/request/content-plan identity, exact encoded
  request length and SHA256, receiving selection; at most4KiB.
- `request.json`: immutable selected-only request, exactly length/hash bound above.
- `event-0001.json` … `event-0032.json`: immutable evidence records, at most4KiB each,
  operation/request identity, sequence, previous raw-record SHA256, P05 operation/
  step observations. An event is either an operation/step observation or an explicit terminal receipt
  record (including RecoveryRequired and later compatible refinement). Replay only
  through P05 Reduce, starting Claimed/Planned.
- `receipt.json`: immutable final non-RecoveryRequired terminal receipt, at most4KiB,
  with operation/request/selection, phase and exact selected ordered step phases.

A bounded32-event chain covers Prepared, Applying, three ordered stores with
Intent/Published/Recorded and explicit recovery evidence without unbounded growth.
Normal observation events can consume only slots1–31; slot32 is reserved for
a receipt-record refinement, and `receipt.json` remains a separate bounded slot.
This is not an unlimited recovery-progress guarantee: reaching the applicable
bound blocks additional events, never authorizes rewriting old records. At most512 immediate operation children for new admission; inventory
returns32 scalar headers per page, no private payload. Unknown/malformed children,
gaps, over-budget or unreadable data fail closed and are preserved. Historical
receipt reads precede inventory/payload/current target access, even at capacity.

## Publication / restart rules

Claim first → exact immutable request → Prepared event → Applying event → ordered
store Intent → independent publication evidence → Recorded evidence → next step →
final receipt. This layer does not obtain store proofs: supplied observations remain
claims until the future live inspector is connected. Prepared is only admitted
when the whole request is independently read/validated. No-change and previous-step
ordering are enforced through P05. Finalization verifies a compatible receipt with
P05 and the exact already-existing ordered step history. It cannot invent Completed
from Claimed or advance steps just because a supplied receipt would be reducer-
compatible. Refinement must first be recorded in an explicit receipt-record event.
RecoveryRequired, PublishedVerified, IntentDurable and Unknown cannot be frozen in
a final file: uncertain evidence stays in the chain for compatible refinement.
Replay retains monotonic per-store evidence that an actual accepted IntentDurable
observation occurred; a receipt cannot invent that history after Unknown/Drifted/
Unreadable. Only such a previously intended store can refine to PublishedVerified
or Recorded. Proven NotStarted cannot be weakened by another receipt, and NoChange
must have been established by its own normal observation before receipt retention.
The same rules apply to live append and restart replay; no wire/schema change is
needed because the facts are derived from the bounded immutable event chain.

Every file uses own create-new pending bytes, Flush(true), create-only publication,
uncancelled exact readback after possible publication. Equal bytes allow idempotent
acknowledgement; different occupied bytes never overwrite. An uncertain acknowledgement
is reconciled at the same ID/name, never another operation. Only this invocation's
known prepublication temporary file may be removed. After publication is exactly
confirmed, the same invocation may also remove its known leftover pending source
(e.g. the Unix hardlink source). Unconfirmed post-attempt and foreign artifacts are
retained. No overwrite/backup of selected notes or target notes. Flush+rename is
not parent-dir-fsync/power-loss/all-filesystem/native platform proof.

Read/inspect never resumes stores or writes evidence. Claim without full request,
missing/corrupt middle records and unknown pending evidence are not empty/success.
Incomplete journal recovery can finalize already-proven evidence only; unexecuted
store remainder requires fresh owner plan/consent/ID. Snapshot revisions are bound
to the particular journal instance and exact last chain hash. All updates reread
and compare under the profile lease; stale/cross-owner snapshots fail closed.
A self-contained final receipt is historical replay authority: its validation
precedes claim/payload/chain reads and global sibling/capacity admission. Thus even
a removed or corrupt payload does not erase completed history. This exception is
only for valid completed receipts; incomplete operations require their complete
claim, payload and chain and never treat missing or corrupt evidence as empty.
Final receipt replay does not inspect today's target stores, original package,
request payload or current markers, preventing resurrection after later unmark/delete.

Physical directory identity is an in-process fence: ProfileLease captures the
profile directory identity, while each journal instance fences the journal root
and operation directories. Linux/macOS use the runtime PAL lstat Dev/Ino; Windows
uses volume/file IDs on a non-reparse directory handle. Ordinary leases retain
their existing compatibility; only the journal requires this physical baseline.
It is not a durable profile/catalog/host incarnation, an openat-style hostile-
process sandbox, or proof of parent-directory fsync. Hash chains detect mismatches
against known heads, not coherent hostile rewrites or a removed valid suffix when
no prior head is available; no auto-resume authority follows from chain validity.

## Remaining before product Apply

Independent real store-marker inspection after restart; persistent profile/catalog/
thread/media/host incarnation and lifecycle reservations; startup/allwriter/cleanup
barriers before ordinary constructors; actual derived target media fields; explicit
receiving consent; candidate/resolve/discard; marker retirement after durable ack;
Desktop workflow; private tags, other media roles, multithread and native QA.
Three JSON stores, journal and archive SQL are still not one ACID transaction.
