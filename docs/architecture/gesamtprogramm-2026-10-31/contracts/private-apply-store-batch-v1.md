# P06 — Store-owned additive batch primitive

Engineering continuation of accepted Profile A G4/G5, not a new restriction of the
user's full feature request. Implementation/review status belongs to RUNSTATE and
the source-bound test/review receipts. This document is not feature acceptance.

## Implemented boundary and APIs

Each existing store owns internal `PrepareAdditiveBatch(operationId,
contentPlanSha256, additions, cancellationToken)`, `InspectPreparedBatch(batch)`
and `CommitPreparedBatch(batch,cancellationToken)`. These are not called by the
Desktop or native import flow. All private Stage/preview/text rejection gates stay.

`PrivateStoreBatch` is a short-lived in-memory prepared value, bound to a private
per-store-instance token, not merely a caller-provided store reference. Its defensive
copy holds the full after-image required to preserve unrelated local entries. **Never
serialize this object or its bytes into journal/candidate/request/receipt/logs**:
it may contain unselected values and old target notes. Only scalar fences/marker
and counts belong in future operation metadata. No automatic apply on disposal.

Prepare takes already-selected/mapped additive values; it does not validate consent,
source containment, catalog existence or lifecycle authority. Those are prerequisites
of the future owner planner, not capabilities furnished by an `Add` comparison row.
Unselected stores must not be invoked/read by that planner. Selected-empty/equal-only
batches retain exact Before/ExpectedAfter and cause no write, migration, marker or
revision bump. Duplicate input identities are rejected.

- Thread: full ResourceId; only absent positive marks are added, no thread/watch
  creation and no deletion/unmarking of any target mark.
- Media: exact FavoriteAssetKey; additions use supplied `FavoriteEntry` length/name.
  The future planner must derive/freeze these from independently verified local
  current/retained original assets. The primitive cannot establish that proof.
  Existing-key length/name stay unchanged; no SHA-only/path/post rekey.
- Bookmark: exact ResourceId+post. Existing equal note is no-change; different text
  fails this additive primitive, never overwrites/concatenates. The future comparison
  and candidate owner retains incoming conflicts separately and admits independent
  safe additions. Incoming LF text, empty/whitespace and Unicode remain exact; CR
  repair is not allowed in the batch. No old target note enters a conflict journal.

## Deterministic image and marker

The store reads bounded strict regular files under its ProfileLease, validates the
primary and favorite backup, computes raw whole-image fences (explicit missing,
schema, instance, revision, length, lowercase SHA256), and serializes its own complete
next image. No transfer-owned replacement JSON or per-entry setter loop. Additions
are sorted by ordinal identity components; existing target order remains intact.
Input/output/entry/revision bounds are checked before publication. New instances use
the supplied locally unique operation ID if the legacy/missing document had none;
existing instances are retained. A real batch increments content revision once.

Changed schema forms:

| Store | Version and additional required fields |
| --- | --- |
| Thread favorites | Version2, InstanceId, OperationMarker |
| Media favorites | Version3, existing InstanceId/Revision, OperationMarker |
| Bookmarks | schemaVersion2, instanceId, operationMarker |

Legacy reads do not migrate. Ordinary unmarked writes keep their established schema
and serialization behavior in this slice. New schemas require a valid nonnull marker;
there is deliberately **no marker retirement/unblock API yet**.

Marker fields are `(OperationId, ContentPlanSha256, StoreKind)` (PascalCase, numeric
StoreKind), published with values in the same primary replacement. Never include
final RequestSha256: ContentPlan→marker→after bytes→Request is acyclic. Profile/store
identity and true live fences remain necessary; hashes/marker equality are neither
authentication nor permission to replay.

## Publication, inspection and per-store barrier

Commit requires the private prepared owner token and exact fresh Before/Backup.
The existing single-file publication mechanism writes the already-prepared bytes.
Bookmarks use no `.bak`, `.corrupt` or old-note history. After possible publication,
uncancelled strict readback checks exact ExpectedAfter plus matching marker. No
cancellation failure is reported after a proven publication. An uncertain IO outcome
is not proof of nonpublication; the caller must later inspect, never blindly retry.
An exact-after result is only a store fact, not a final operation receipt.

Inspection performs no mutation or cleanup: ExactBefore / ExactAfterWithMarker /
NoChange / Drifted / Unreadable. **ExactBefore is not a continuous-history proof**
for the P05 reducer. Favorite after inspection also checks the expected rotated
backup. Cross-store-instance or cross-profile prepared batches fail closed.

Ordinary Set/SetFavorite/RemoveThreads/Upsert/Remove cannot erase an unresolved
marker. This applies after restart because the primary is parsed on each mutation.
Bookmark constructor cleanup preserves pending artifacts when primary/operation
state is marked or uncertain. Private operation pending names are distinct from
ordinary pending images and block ordinary cleanup/writes until future recovery.
No automatic restore of favorite backup or note pending image.

## Required next integration (not supplied by this primitive)

- Bounded durable selected-only claim/payload/Prepared/intent/step/final receipts;
  independent operation inspection and evidence-only recovery. Receipt-first replay.
- True persistent profile/catalog/thread/manifest/host incarnation and admission
  reservation, lifecycle-safe deletion/move/shutdown, and all-store/startup barrier
  **before** normal feature writers/cleanup. Per-store marker guards alone do not
  block prepublication intents or unrelated store/host actions.
- Owner planner joins P04/P05 to real before/after images without a second digest
  implementation; target-derived media facts must be bound before content hashing.
- Explicit default-off receiving consent, selected-only durable incoming conflicts,
  resolve/discard, private tags and Desktop workflow. No silent overwrite/resurrection.
- Marker retirement only after a verified durable acknowledgement, with exact fences;
  do not clear a marker just to unblock normal mutations.

Three JSON stores, candidates, journal and archive SQL are **not common ACID**.
Intermediate/partial progress is possible, no common rollback. Flush/rename is not a
power-loss/parent-directory-fsync/all-filesystem/native-Windows/macOS proof. P05 A03-01
was fixed and independently rechecked in Review04; terminal Partial comes from a
compatible final receipt, not inspection-derived unacknowledged publication facts.
