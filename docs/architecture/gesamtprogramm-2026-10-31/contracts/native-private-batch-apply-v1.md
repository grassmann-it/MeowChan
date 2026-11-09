# Native private batch Apply v1 — MeowChan 1.0.3

**Status: normative product contract for the private receiving lane of
MeowChan 1.0.3.** This document defines the private, selected-only batch that
may accompany an own MeowChan transfer. It is an architectural contract and
release gate; it is not evidence that the current worktree already implements
or passes every step described here.

The public `chan-v4` single-thread Apply contract remains authoritative and
separate: [`native-v4-apply-v2.md`](native-v4-apply-v2.md). This document does
not widen public v4, does not turn a private sidecar into public catalog data,
and does not create permission to apply a package merely because the package
contains private data.

## 1. Scope and terms

This contract covers an explicit receiving choice for the following private
categories only:

1. **Thread favorites** (`ThreadFavorites`);
2. **Media favorites** (`MediaFavorites`); and
3. **Post bookmarks and notes** (`PostBookmarksAndNotes`).

The category order is normative and is used for planning, journaling, receipt
reconciliation, and recovery. A category that is not selected is not read,
planned, journaled, or applied. A selected-but-empty category is distinct from
an unselected category and still receives a fence and a `NoChange` step.
Private tags, ratings, reader groups, watch state, grants, credentials,
provider state, and arbitrary profile settings are outside v1 and must be
rejected or remain unoffered rather than being silently folded into one of the
three categories.

Definitions:

- **Public package**: the validated public transfer envelope/catalog and its
  immutable staged payload. Its public identity is governed by the v4
  representation and import contracts.
- **Private component**: the separately framed, selected-only private metadata
  component (`meowchan-private-v1` under the existing package component
  contract). Its bytes are never interpreted as public posts, attachments, or
  originals.
- **Private offer**: a content-free description that private categories are
  present and which bounded categories could be selected. It contains no
  private values, notes, absolute paths, credentials, or target-store images.
- **Owner-issued consent**: a receiving decision created by the owner of the
  target profile through the active import session. It is an authorization
  decision, not a property inferred from package presence or from the sender's
  export selection.
- **Package fence**: immutable identity of the staged package, catalog, private
  component, and public resource scope.
- **Target fence**: the target profile/catalog/resource/store instances and
  revisions captured during planning and required to remain unchanged until
  the relevant step is admitted.
- **Operation fence**: the unique operation identity, session generation,
  profile incarnation, selected categories/resources, and canonical content
  plan to which all evidence and receipts are bound.
- **Coordinator**: the existing profile-/owner-aware exclusive coordinator
  which serializes the aggregate operation and owns its lifecycle. A plan,
  DTO, journal, receipt, or store batch is never a substitute for coordinator
  admission.

The contract assumes an immutable, caller-owned stage. Apply never reopens the
original source path and never acquires network data, credentials, or missing
private values.

## 2. Separation from public single-thread v4

The two lanes compose at the user workflow boundary but not at their authority
or data boundary:

```text
public v4 validation/inventory/plan
    + optional private offer/consent/selected plan
    → one owner-issued operation
    → public additive Apply
    → ordered private store batch
    → content-free completion/partial/recovery receipt
```

The public lane remains governed by `native-v4-apply-v2.md`:

- public v4 Apply is exactly one public thread/resource;
- public `New`/`Merge`, 404 Merge, target protection, representation rules,
  and public receipts remain unchanged;
- public preview-only data is never promoted to an original; and
- private values never enter the public catalog, public media manifest, public
  conflict alternative, or public v4 receipt.

A private batch may be attached to the same user-confirmed transfer operation,
but it is a separate selected projection with separate fences and separate
store evidence. If the public part succeeds and the private part cannot be
proven, the result must say so; it must not report an undifferentiated success.
A private failure does not justify rolling back or weakening a committed public
additive merge, and a public failure does not authorize a private-only apply
unless a separately planned operation explicitly supports that mode.

A public 404/`not-found` root remains an additive `Merge` against the exact
local resource. It does not resurrect or recreate a root and does not by itself
make a private mark eligible. Private marks require an exact target resource or
post/media identity and otherwise become a durable `MissingTarget`/review fact.

## 3. Consent and admission

### 3.1 Default-off and independent selection

Receiving private data is **off by default** for every import, including a
re-import of a package previously used for public content. Sender selection is
not receiver consent. A previous consent is not reusable for a different
package, target, session generation, scope, or category selection.

The dialog must show the private offer separately from public counts and must
make each category independently selectable. The default selection is empty.
A user-visible confirmation must state that selected favorites/bookmarks/notes
will be additively considered for the target profile and that existing target
values are protected. The UI must not disguise private consent as a generic
"import all" or as a favorite-media choice.

### 3.2 Owner-issued, package-bound consent

Consent is valid only when all of the following hold:

1. It was issued by the active owner of the receiving target profile through
   the current import session; no imported consent, sender assertion, package
   flag, or caller-constructed DTO is sufficient.
2. It names the exact target profile incarnation and target instance to which it
   applies. A different profile, copied profile directory, or changed profile
   incarnation invalidates it.
3. It binds the exact staged package fingerprint and private-component
   fingerprint, the public catalog fingerprint, exact resource/thread scope,
   selected category set, and the import session generation.
4. It is issued after the package and offer have passed bounded validation and
   before the private plan is created. Consent cannot authorize malformed,
   stale, rootless, cross-resource, or unsupported content.
5. It has not been revoked by session cancellation, generation change, target
   drift, package replacement, or a completed/failed operation. An expired or
   stale decision fails closed; the user must make a fresh choice.

The consent record is an authorization fact with a bounded identifier and
fingerprints. It must not persist private note text, full store images,
credentials, or executable handles. A package-bound consent is not transferable
to another target or operation. An operation may carry a derived consent
binding, but the consent itself is never a substitute for the operation,
coordinator, target fences, or store readback.

### 3.3 Admission failures

The private lane returns a typed non-success outcome without mutating a target
when any of these is true:

- private data is present but no explicit receiving selection exists;
- consent is absent, imported, stale, foreign-owner, self-constructed, or bound
  to different package/catalog/scope/generation bytes;
- a selected category is not offered, is malformed, exceeds existing bounds, or
  contains an identity outside the public scope;
- a public resource/thread root, target profile, or required target fence is
  missing or ambiguous; or
- coordinator/profile/store admission is unavailable.

The offer and rejection result may expose bounded counts and typed reasons, but
never private notes, paths, credentials, or raw exception text.

## 4. Operation, package, and target fences

All three fence families are part of planning and are checked again at Apply.
A hash is an identity/freshness fence, not authentication or permission.

### 4.1 Operation fence

The operation fence contains at least:

- a newly generated operation ID;
- the canonical selected content-plan hash and request hash;
- the receiving profile incarnation and owner/coordinator identity;
- the import session generation;
- exact ordered public resource/thread scope;
- exact ordered category selection, including selected-empty categories; and
- the operation phase and journal head used for reconciliation.

The operation ID is single-use for a logical request. Reusing it with another
package, target, scope, category set, consent, or expected-after image is a
mismatch, never a convenient retry. A second Apply call for the same live
operation joins/reconciles the existing task; it does not start a second
executor.

### 4.2 Package fence

The package fence contains:

- exact staged package length and SHA-256;
- exact validated public catalog length/hash and schema/version;
- exact private component length/hash and private schema/version;
- exact public resource/thread identities and their deterministic order;
- source-stage ownership/incarnation and immutable stage identity; and
- the selected private projection identity, without storing unselected values.

The original source path, URL, media hash, filename, or provider ID cannot
replace the package/component fence. If any staged bytes, catalog, private
component, scope, or selected projection changes, the old plan and consent are
stale and must not be rebound to a newly discovered file.

### 4.3 Target fence

The target fence is captured under the coordinator's read/lease boundary and
revalidated immediately before each write boundary. It includes, as applicable:

- target profile/catalog/manifest instance and generation;
- exact public resource/thread and post/media identities used for mapping;
- the selected store's instance, schema, content revision, length, and raw
  SHA-256 before-image;
- the favorite backup fence for the two favorite stores; bookmarks have no
  invented backup fence;
- the expected-after identity for each selected category; and
- host/profile lifecycle, cleanup, writer, and shutdown fences that prevent a
  concurrent ordinary writer or teardown from entering the batch.

A target drift, lost lease, changed store instance/revision/bytes, changed
public media identity, or changed lifecycle reservation invalidates the pending
step. The operation must return `NeedsReview` or `RecoveryRequired` according
to whether publication is known; it must not merge against the new target by
last-writer-wins.

## 5. Deterministic private plan

The private plan is immutable, selected-only, and generated only after valid
consent and a fresh target snapshot. Its canonical identity includes the
operation fence, package fence, target before-fences, category order, and
selected incoming values. It retains only the selected incoming values needed
for safe additions and durable conflict alternatives; it does not retain old
target notes or complete store after-images in the journal/receipt.

Thread scope is sorted by the existing exact `ResourceId` identity rules. Store
entries use exact ordinal identity ordering. No case folding, URL-only identity,
SHA-only media rekey, provider substitution, or inferred neighboring thread is
allowed.

The planner classifies each selected value as one of:

- `Add`: an absent, exactly addressable value that can be safely added;
- `Identical`: already present with the same value; no write is required;
- `Conflict`: target and incoming values differ; target is retained and the
  incoming selected value becomes a durable, content-bounded alternative; or
- `MissingTarget`: the exact public thread/post/original media target is absent;
  no private value is applied and the reason is retained in the receipt.

The planner may not convert `Conflict` or `MissingTarget` into `Add` by using a
preview, thumbnail, filename, hash twin, 404 status, or current source fetch.
The target's existing values remain authoritative. For bookmarks, an empty note
and a non-empty note are different values; no whitespace normalization or
concatenation is performed.

## 6. Fixed store sequence and additive semantics

The coordinator executes at most one private batch for an operation, in this
fixed order:

```text
1. ThreadFavorites
2. MediaFavorites
3. PostBookmarksAndNotes
```

For every selected category the batch records `Intent`, then either a proven
`NoChange`/`Recorded` result or an explicit nonterminal/recovery result before
considering the next category. A later category must never be published before
the previous category's publication/readback evidence is durable.

### 6.1 Thread favorites

Only an absent positive favorite for an exact existing public thread may be
added. Existing marks are preserved. The batch never removes a favorite,
creates a thread/watch entry, changes a reader value, or treats a missing 404
root as an invitation to create one.

### 6.2 Media favorites

Only an absent favorite for an exact `FavoriteAssetKey` whose target current or
retained **original** can be independently resolved and verified may be added.
A preview, derived representation, poster, filename, URL, or SHA-only match is
not an original substitute. Existing favorite names, lengths, bindings, and
other target fields remain unchanged. No media is fetched during private Apply.

### 6.3 Post bookmarks and notes

An absent bookmark for an exact captured post may be added with the selected
incoming note. An identical note is `NoChange`. A different note is a durable
`Conflict`; the target note is not overwritten, deleted, concatenated, or
normalized. A missing post is `MissingTarget`, even if a neighboring post or
thread identity appears present.

### 6.4 Idempotence and target protection

The batch is additive and idempotent:

- repeating the exact operation after a verified final receipt returns a replay;
- repeating after a proven `NoChange` does not bump a store revision;
- an existing target value is never downgraded, removed, or overwritten;
- equal additions do not create duplicate rows or change unrelated ordering;
- safe additions are applied once and are recognized by exact identity and
  store marker/fence, not by a loose hash; and
- a different operation must replan against a fresh target fence.

A failed or cancelled operation never uses a compensating delete to imitate
atomicity. If a safe prefix is already durable, that fact is preserved in the
receipt/recovery chain rather than hidden.

## 7. Sequential receipt and recovery contract

The three JSON stores and the public catalog/media transaction are not assumed
to form one ACID transaction. v1 therefore promises ordered, honest sequential
semantics, not all-or-nothing cross-store rollback.

### 7.1 Receipt contents

Every terminal receipt is content-free and bounded. It contains only the
operation/package/target fence identities, ordered category step states, bounded
counts, conflict/missing-target identifiers, phase, and replay indicator. It
never contains media bytes, full post bodies, private note text, absolute paths,
credentials, grants, or store after-images.

Each category step has at least:

- `NotStarted`, `Intent`, `Published`, `Recorded`, `NoChange`, `ConflictOnly`,
  `MissingTarget`, `Failed`, or `RecoveryRequired`;
- before/expected-after fence references;
- bounded additive/conflict/missing counts; and
- the durable evidence reference used to reconcile it.

`Recorded` means the store-owned write and its uncancelled exact readback are
proven. `NoChange` means the exact before-fence was inspected and no mutation
was needed. `Intent` or `Published` without final readback is not completion.

### 7.2 Terminal outcomes

- **Completed**: all selected categories are `Recorded`, `NoChange`,
  `ConflictOnly`, or `MissingTarget`, with no unknown publication and a final
  receipt. This can include zero writes.
- **Partial**: a terminal receipt explicitly records a proven durable prefix
  and a proven non-applied remainder (for example, user cancellation at a
  category boundary or a known disk-full failure before the next category).
  Partial is never inferred merely because a receipt is missing or because a
  store read failed. The receipt lists exactly which categories were completed
  and which were not.
- **RecoveryRequired**: publication or the prefix/remainder is uncertain,
  journal evidence is incomplete, the process crashed, the response was lost,
  or a marker/readback cannot establish whether a store changed. It is a
  blocker, not a success and not permission for blind replay.
- **Failed**: the operation is proven not to have published any private store
  step and no target mutation occurred. A failed result may be returned before
  any `Intent` or after a proven prepublication rejection.
- **Cancelled**: only a pre-Apply cancellation with no private write and no
  unresolved operation may be terminally labelled cancelled. Cancellation
  during Apply normally requires reconciliation first.
- **NeedsReview**: a plan/consent/target fence became stale before publication;
  it is safe to discard the plan and create a new one, but no automatic replan
  or apply is allowed.

A `Partial` receipt requires independently reconciled evidence that the prefix
was recorded and the remainder was not started or is otherwise explicitly
resolved. A recovery inspector may finalize such a receipt; it may not invent a
successful step from `Intent`, `Published`, unknown IO, or an after-image with
no matching marker.

### 7.3 Replay and restart precedence

On every Apply, startup, or recovery inspection:

1. read and validate an exact final receipt for the operation first;
2. if a matching `Completed` or `Partial` receipt exists, return its historical
   result without rereading private payloads or resurrecting data;
3. otherwise inspect the bounded claim/request/event chain and the exact store
   fences without invoking normal store constructors or cleanup; and
4. leave unresolved evidence as `RecoveryRequired` until an owner-controlled,
   independently verified reconciliation produces a terminal receipt.

A receipt for another package, request, profile incarnation, scope, target, or
operation is not a match. Missing or corrupt evidence is not empty history.
Recovery never starts a network fetch, watcher, queue, grant, or automatic
repair. A fresh import after resolution requires a new operation ID, new
consent, fresh plan, and fresh fences.

## 8. Cancel, crash, disk full, and cleanup

### 8.1 Cancel

Before Apply, cancellation invalidates the ready plan/consent for the session
and removes only the owned stage after all readers settle. No store or receipt
is written.

During Apply, cancellation is cooperative. The coordinator stops before the
next category when it can prove the previous category's result, but it does not
interrupt a store publication in a way that could leave an unclassified write.
A late cancellation cannot relabel a committed step or receipt. If the result
is not provable, the operation is `RecoveryRequired`; it is never reported as
cleanly cancelled.

### 8.2 Crash or lost response

After restart or lost response, the exact final receipt is authoritative when
present and valid. Without it, the journal and store markers are inspected in
fixed order. The process does not retry a category solely because the caller
saw an exception, timeout, or process exit. A verified prefix may later be
finalized as `Partial`; an uncertain category remains recovery-required.

### 8.3 Disk full, permission, and readback failure

Disk-full, permission, malformed-file, and readback failures do not justify
restoring an old target image, deleting existing favorites, overwriting a
conflict, or recursively cleaning a directory. Known invocation-owned pending
files may be cleaned only after their publication status is proven. Unknown,
foreign, pre-existing, referenced, or marker-bearing artifacts remain for
inspection. An uncertain publication yields `RecoveryRequired`, even if the
write call reported an error.

No common rollback is promised across the public catalog and the three private
stores. The user-visible receipt must distinguish a committed public result
from a private `Completed`, `Partial`, `Failed`, or `RecoveryRequired` result.

## 9. Multi-thread and coordinator boundary

This contract does **not** authorize a multi-thread Apply merely by accepting a
list of threads. A multi-thread operation is admissible only when the existing
aggregate coordinator is live and owns all of the following as one operation:

- explicit, bounded, deterministic thread selection and order;
- one operation/package/target fence covering the complete selection;
- per-thread public target protection and 404-merge semantics;
- one owner-issued private consent bound to that exact scope;
- ordered private store steps with per-thread identity and no cross-thread
  collision; and
- durable partial/recovery receipts that identify both the completed thread
  prefix and the private category prefix.

Until that coordinator and its tests are release-accepted, multi-thread private
Apply is `Unsupported`/`NeedsCoordinator`; the implementation must not fan out
independent thread tasks and call their collection a batch or atomic import.
A single-thread operation may use this private contract without a separate
multi-thread coordinator, but it still requires the normal profile-/writer-aware
coordinator and all fences above.

## 10. Allowed and forbidden behavior

### Allowed

- Offer the three private categories separately after bounded package
  inspection, with all receiving choices default-off.
- Accept only owner-issued consent bound to the exact package/component,
  catalog, target profile, session generation, scope, and selected categories.
- Add absent, exact thread favorites; add absent exact media favorites backed by
  verified target originals; add absent exact post bookmarks with selected notes.
- Report identical values as no-change and preserve conflicts/missing targets as
  durable, bounded, user-visible alternatives or receipt facts.
- Apply the fixed category order under one live coordinator and return a
  content-free `Completed`, `Partial`, `Failed`, `NeedsReview`, or
  `RecoveryRequired` receipt with exact fence references.
- Reconcile an operation after restart by receipt-first inspection and create a
  fresh operation after unresolved work is explicitly resolved.
- Combine a private result with a public additive `New`/`Merge`/404-Merge while
  keeping the two data and receipt lanes visibly distinct.

### Forbidden

- Treating package presence, sender selection, a public Apply choice, a copied
  consent object, or an imported flag as receiving consent.
- Applying private metadata by default, applying an unoffered category, or
  reading unselected private stores.
- Writing private values into public posts, catalogs, media manifests, v4
  representations, public receipts, logs, diagnostics, or conflict alternatives.
- Replacing/removing/unmarking existing favorites; overwriting, concatenating,
  or normalizing target bookmark notes; or selecting a source-side conflict
  winner automatically.
- Using a preview, thumbnail, poster, filename, URL, provider ID, SHA-only
  match, or 404 status as an original/media/post identity substitute.
- Rebinding a stale consent, plan, operation, or stage to another package,
  profile, target generation, thread scope, category set, or target instance.
- Calling `Intent`, `Published`, a prepared in-memory batch, an after-image, or
  a receipt-less journal as proof of completion.
- Blindly retrying after crash, timeout, disk-full, unknown IO, or missing
  readback; inventing `Partial` from uncertainty; or performing common rollback
  that can destroy existing target data.
- Fan-out multi-thread Apply without the aggregate coordinator and its explicit
  fences/receipts, or claiming independent per-thread operations are atomic.
- Starting network fetches, watchers, queues, grants, credential use, automatic
  cleanup, silent repair, or source retries as a side effect of private Apply.

## 11. Compatibility and transition note

This contract composes, but does not erase, the narrower implementation
contracts already in the repository:

- [`private-metadata-snapshot-v1.md`](private-metadata-snapshot-v1.md),
  [`private-metadata-wire-v1.md`](private-metadata-wire-v1.md), and
  [`private-package-component-v1.md`](private-package-component-v1.md) define
  bounded selected-only framing and remain prerequisites.
- [`private-import-comparison-v1.md`](private-import-comparison-v1.md) defines
  passive `Add`/`Identical`/`Conflict`/`MissingTarget` classification; its
  explicit statement that comparison is not Apply authority remains in force.
- [`private-apply-store-batch-v1.md`](private-apply-store-batch-v1.md) defines
  store-owned additive primitives and markers; its statement that the primitive
  is not itself a product Apply remains in force.
- [`private-apply-journal-v1.md`](private-apply-journal-v1.md),
  [`private-apply-request-recovery-v1.md`](private-apply-request-recovery-v1.md),
  [`private-store-admission-v1.md`](private-store-admission-v1.md), and
  [`private-store-restart-inspection-v1.md`](private-store-restart-inspection-v1.md)
  define bounded evidence, writer roles, and pure recovery boundaries. Their
  warnings that they do not alone authorize an active Desktop/private Apply
  remain in force until the complete chain is integrated and accepted.
- [`native-v4-apply-v2.md`](native-v4-apply-v2.md) is **not superseded**. It
  remains the sole public single-thread v4 authority. Its prohibition on
  private metadata without consent and on multi-thread Apply without a
  coordinator is clarified here, not weakened.
- The historical blanket public-v4-unsupported wording in
  `native-host-import-session-v1.md` and `native-v4-inventory-v1.md` is already
  superseded for the admitted public single-thread shape by `native-v4-apply-v2`.
  This document does not supersede their remaining private/multi-thread
  unsupported boundaries until the release gates below are actually proven.

No existing document is marked broadly `superseded` by this file: the older
private documents describe lower-level slices and deliberately retain their
non-authority boundaries. If an older private document later receives wording
that contradicts this complete batch contract, update only that specific
paragraph with a direct link to this file and a scoped `superseded for
1.0.3-private-batch` note; do not erase historical evidence.

## 12. 1.0.3 acceptance gates and open assumptions

The private lane is release-accepted only when all of the following are
verified on one frozen source state:

1. default-off dialog selection and owner-issued package-bound consent,
   including stale/session-generation/foreign-consent rejection;
2. exact package, operation, profile, catalog, target, store, and lifecycle
   fence revalidation;
3. fixed three-category order with store-owned prepared publication,
   uncancelled exact readback, idempotent replay, and no unrelated-value loss;
4. durable conflict alternatives and missing-target facts without target
   overwrite or preview/original substitution;
5. restart, cancel, crash, lost-response, disk-full, permission, and cleanup
   cases producing the receipt state required above;
6. a real aggregate coordinator before any multi-thread private Apply is
   advertised; and
7. Linux plus required Windows/macOS/package evidence, with no platform
   substitute presented as complete release proof.

The following remain explicit assumptions/open decisions rather than hidden
claims of implementation:

- The exact owner-issuance mechanism (local profile ownership proof versus a
  stronger cryptographic consent token) must be fixed by the integration slice;
  this contract requires the former at minimum and does not invent the latter.
- The three private JSON stores, journal, catalog, and media writes are not a
  shared ACID transaction. The release therefore accepts sequential
  `Partial`/`RecoveryRequired` semantics only if the receipts and restart
  inspector prove the boundary without guessing.
- Multi-thread ordering is recommended as exact `ResourceId` order, but the
  aggregate coordinator must make that order and its per-thread receipt shape
  concrete before admission. Until then it remains unsupported.
- Private tags, ratings, reader groups, watch state, grants, credentials,
  relocation, foreign imports, and private settings are not v1 categories and
  cannot be smuggled through this contract.
- The current implementation's P04/P05/P06/P07/P08 slices are evidence for
  individual boundaries only; they do not constitute the end-to-end release
  acceptance claimed by this document.
