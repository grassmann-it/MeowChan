# P05 — Selected-only request identity and pure recovery reducer

Technical continuation of accepted Profile A G4/G5 and P01–P04. This slice supplies
**passive in-memory contracts**, not an active importer, consent, owner-issued plan,
disk journal protocol, atomic-store marker or verified durable write. Private Stage,
inventory preview and text import rejection gates stay unchanged.

## Request data boundary

`NativePrivateApplyRequest.Create` binds an operation ID to a bounded canonical
request digest. Input is P04 selected comparison plus local scalar binding/fences.
Receiving categories must agree with the selected payload; all default-off and
selected-empty distinctions are preserved. Rows must correspond one-to-one to the
selected identities and incoming notes; extra rows, invalid dispositions and
identity/value mismatches fail closed. Thread/media favorites cannot conflict as
boolean positive-only marks; bookmarks can, including empty/nonempty notes.

The immutable result retains only selected incoming values and comparison rows,
not the original package/projection, target notes, full store images, paths, source
streams or owner objects. Exact incoming note text, including conflicts and missing
targets, survives as an inert selected value. No LWW or implicit skip/delete/unmark.
Class diagnostics are opaque. Validation errors discard private inner exceptions.

Store fences are in fixed order thread favorites → media favorites → bookmarks,
exactly one per selected category, including selected-empty. Missing primary is
explicit `(false,0,null,0,0,null)`; existing primary has bounded schema/revision/
length and lowercase SHA-256. Legacy existing instance may be absent. A changed
expected-after must exist, have an instance and increment content revision once;
empty/equal/conflict/missing-only categories require unchanged before/after;
existing instance is retained. Favorite backup fence is explicit even when missing;
bookmark backup is forbidden. These are claims for a future store-owned producer,
not proof that current store serializers accept new schema fields.

`Binding` carries exact package length/hash, public-catalog hash, private-component
length/hash, local profile incarnation and opaque aggregate target-fence hash.
The latter is a future integration placeholder, **not** validated lifecycle proof.
A caller-constructed binding/request must never be accepted as mutation authority.
The eventual owner plan must bind the live lease/host/catalog/lifecycles and fresh
resolved media proof and derive favorite length/name itself. P05 does not implement
entry-level receiving subsets, decisions to overwrite/resolve, derived media values,
or deterministic expected-after store serialization.

## Canonical identity

- Strict UTF8, existing bounded P02 payload/Unicode/note validation.
- Exact ordinal component sort for thread, full media favorite and bookmark keys.
- Canonical selected P02 JSON is embedded in a domain-versioned length-delimited
  digest input; every integer is 64-bit big-endian, every UTF8/byte field is prefixed
  by its byte length. Binding, classifications, before and backup fences are included in
  `ContentPlanSha256`. The final `RequestSha256` binds this content-plan digest
  plus exact ordered expected-after fingerprints. A future co-published marker
  must use `(OperationId, ContentPlanSha256, store-kind)`, NOT the final request
  hash: the latter depends on the full after-image including its marker. This
  two-stage digest avoids a cyclic requestHash→marker→afterHash dependency.
  P05 does not yet supply a store-owned prepare/marker serializer or persist it.
- P05 does not put `OperationId` directly into either digest. `ContentPlanSha256`
  is the logical selected plan identity; `RequestSha256` is the concrete plan plus
  its exact expected-after fences. Thus equal passive scalar inputs produce equal
  digests, but a future marker containing a fresh operation ID will normally change
  the expected-after fence and therefore the final request digest. Never use digest
  equality as authentication or as permission to replay an operation. Different
  selection/fence/classification/value changes the request; no hash-only identity
  inference or authentication claim.
- Selected payload remains at most 16MiB; request digest encoding has a separate
  20MiB cap (16MiB + at most 4MiB scalar/classification overhead), enforced during
  hashing. No persistent 20MiB journal budget is asserted: no journal writer exists.

## Recovery boundary

The separate pure reducer models ordered max-three store steps, explicit phase
transitions and independent evidence observations. It performs no IO and issues
no Apply/retry/store commands. Evidence is passive, never itself a capability.
Terminal historical receipt replay must precede current target freshness checks;
ID/hash mismatch rejects. Recovery can finalize proven publication **evidence**,
never repeat a store mutation or auto-activate an unexecuted remainder. Unknown,
Drifted and Unreadable are not non-publication proofs: they remain
`RecoveryRequired`, preserving known prefix facts until independent receipt
refinement. In particular a failed read after durable intent cannot create a final
Partial that blocks a later matching Completed receipt. A proven unstarted remainder
with a published prefix remains `RecoveryRequired` until an independently supplied
terminal receipt finalizes `Partial`; historical final receipts stay immutable. After-bytes without a
matching co-published marker are not commit proof. There is no cross-JSON/SQL ACID
or automatic rollback promise.

## Still mandatory before any private Apply admission

Store-owned deterministic batch preparation and atomic value+operation-evidence
publication; participating all-writer/startup/cleanup barriers; bounded regular-file
journal/candidate persistence and independent inspection; owner/catalog/profile/
thread/manifest/host lifecycle fences; explicit receiving consent; selected-only
conflict candidates, resolve/discard and Desktop workflow. Neither P05 tests nor a
reducer transition can be cited as end-to-end/disk crash-consistency evidence.
