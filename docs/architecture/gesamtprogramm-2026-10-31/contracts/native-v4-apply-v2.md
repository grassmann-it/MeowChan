# Native v4 Apply v2 — authoritative public single-thread contract

**Status: normative contract.** This document is the authority for applying a
public `chan-v4` package through the native single-thread path. It supersedes
the implementation-slice wording in [`native-host-import-session-v1.md`](native-host-import-session-v1.md)
and [`native-v4-inventory-v1.md`](native-v4-inventory-v1.md), which remain in
the repository as historical context. It does not replace the wire-level v4
representation contract, the schema-11 contract, or the versioned v2 import
and merge reader contracts; those are prerequisites and are read together with
this document.

This is a product contract, not a claim that the current worktree, tests,
platforms, or release are complete. Nothing here authorizes changing product
code, the release plan, or an unrelated transfer path.

## 1. Terms and authority

The public Apply lane uses the existing native transfer types and the existing
coordinator, profile admission, stage, target-root, SQL, and receipt machinery.
It does not create a second importer or a second authority.

- **Resource** means one exact `ResourceId`: source, board, and thread. The
  tuple is compared using the existing identity rules; no case folding,
  provider substitution, or URL-only identity is allowed.
- **Page** means the public thread/page boundary represented by that resource.
  A non-null `pageUrl` is only descriptive provenance and must identify the
  same public thread under the existing URL grammar. A missing `pageUrl` does
  not make a resource rootless when the exact `ResourceId` is present.
- **Package fingerprint** is the fingerprint of the validated package bytes.
  **Catalog fingerprint** is the digest of the exact validated catalog bytes.
  They are input identity, not authorization.
- **Target fence** is the immutable target instance/generation and the
  resource snapshot captured for the plan. It is checked again at Apply.
- **Operation identity** is the caller-created operation ID bound to the
  package/catalog fingerprint, exact resource, explicit scope, selection, and
  target fence. An operation ID is not a grant and cannot be reused with a
  different request.
- **Coordinator** is the existing owner- and profile-aware serialization lane
  that admits the complete import or merge operation. A plan, DTO, persisted
  row, or receipt is never a substitute for a live coordinator admission.

The public lane has one authoritative sequence:

```text
validated package → private owned stage → inventory → target snapshot
→ explicit New/Merge plan → user preview → exact Apply
→ durable content + content-free receipt
```

Only the final transaction and its validated receipt establish a committed
result. Every earlier object is descriptive evidence or a bounded preparation
state.

## 2. Admission: what a public single-thread v4 package may contain

Apply admits a package only when all of the following are true:

1. The package is a valid native envelope whose catalog is exactly `chan-v4`
   and whose required features are understood. Existing envelope, catalog,
   payload, URL, HTML, Unicode, integrity, and cancellation rules remain in
   force.
2. The catalog contains **exactly one** thread root. Every post, current
   attachment, retained binding, and nested derived representation belongs to
   that one exact `ResourceId`. A package containing two roots, foreign
   resource entries, unattached posts, or cross-resource retained bindings is
   not a public single-thread package.
3. The root is public Chan material. It may describe an active, archived, or
   not-found/404 source state, and it may contain current observations,
   retained original bindings, and v4 nested derived representations. The
   source state is historical package data; it does not instruct the target to
   start watching, retrying, or to change its own state.
4. The package stays within the existing bounds. v4 Apply does not raise any
   catalog, text, entry, payload, ZIP/container, thread, post, attachment,
   manifest, or verification limit. Existing limits include the 16 MiB catalog
   cap, the 1,000-thread and 100,000-post/attachment bounds, and the combined
   attachment work budget. For this lane the thread count is one, but all
   declared current attachments, retained originals, and derived entries are
   charged before allocation and resolution. Duplicate payload hashes do not
   remove reference or work charges.
5. Every original and derived representation obeys the v4 representation
   contract. `originalByteVersion` is actual evidence for that occurrence;
   `derivedRepresentations` are structurally nested and source-bound to that
   original. A derived payload is never promoted to an original because its
   bytes, name, or hash look similar.
6. Public content is the only content admitted by this lane. A private package
   component is not silently projected into the public catalog or partially
   applied. If a future private lane is used, it must first obtain explicit,
   per-category user consent and its own reviewed private plan/contract.
   Presence of private bytes in a package is never consent.

The following public v4 observations may be carried into inventory:

- contained original or derived bytes, after the existing measured
  length/hash/structure checks;
- explicitly omitted or unavailable original/derived observations, with no
  invented payload or availability claim;
- retained original bindings, including a retained-only or evidence-only
  resource, without inventing a captured post;
- public post fields and relationships that are absent, present-empty, or
  present-null under the existing presence semantics; and
- a public 404/not-found root that is still anchored to the exact resource.

`unknown`, `failed`, or `cancelled` observations remain honest inventory facts,
but cannot be used as contained bytes or as evidence that an Apply succeeded.
They do not authorize a network retry or a replacement download. An invalid,
changed, corrupt, ambiguous, or unbound v4 representation fails closed; it is
not downgraded to a preview or omitted merely to make the package applicable.

### 2.1 Page and resource boundary

The root is the unit of admission and merge. A `pageUrl` may be null where the
source does not provide one, but the package must still have one exact
`ResourceId`. Conversely, a page URL cannot establish a resource by itself.
The following are therefore outside the lane:

- a package with no exact resource root (a **rootless** package);
- a post, attachment, or private record whose resource differs from the root;
- an item copied from a neighboring page, board, source, or thread without an
  explicit public catalog identity; and
- a package that uses a media hash, filename, URL, or provider media ID as a
  replacement for the resource/post/position/revision identity.

The page/resource boundary is not an instruction to fetch the page. Apply is
offline against the validated package and the local target. Public URLs are
passive provenance only and never credentials, signed access, or an acquisition
permit.

## 3. Explicit New and Merge scope

Every Apply request declares exactly one scope before planning:

```text
Scope = New | Merge
```

The scope is part of the request hash and cannot be inferred, silently changed,
or recovered from `skip-existing` behavior.

### 3.1 `New`

`New` is valid only when the target has no root for the exact package
`ResourceId` and no existing target instance that would make the resource
present. The operation creates the one public root and its safe public content
additively. It may include retained/evidence records permitted by the v4
contract, but it must not fabricate posts, capture revisions, or current
originals from them.

If a matching target root appears between inventory, preview, and Apply, the
plan is stale. Apply does not silently turn `New` into `Merge`; it returns a
review/stale outcome and leaves the target unchanged. A previously committed
identical operation is checked by receipt identity first and is reported as a
replay, not as a second New.

### 3.2 `Merge`

`Merge` is valid only against an existing target root for the exact resource,
including a locally retained `not-found`/404 root. It is an additive union of
public observations and safe media content. It may add absent posts, absent
slots, missing public fields, retained bindings, and valid derived
representations. An existing target is not considered empty merely because the
source was later 404 or because its current page has no posts.

Merge preserves the target's existing originals, preview/derived references,
favorites, ratings, notes, tags, reader values, source state, capture
preferences, and other target-owned values unless a separately consented
contract explicitly covers a category. A source observation that differs at an
occupied slot, revision, byte version, deleted marker, or scalar field is a
conflict or alternative, not a replacement. Safe additions may be committed
alongside a durable conflict alternative; if the conflict mechanism is not
available, the plan remains review-required and no conflicting mutation is
performed.

A 404 merge is therefore still `Merge`, not `New`, `skip-existing`, or a
resurrection. The target's existing root and local state remain authoritative
for what is already present.

## 4. Inventory semantics

Inventory is the first post-validation result and is read-only with respect to
the target.

### 4.1 Capture and validation

The host copies the selected package once into a private, random, session-owned
child under the caller's already trusted work parent. The source handle is
closed after the validated copy. Inventory and later Apply use that immutable
owned stage; they do not reopen the original source path and are not affected by
its later replacement or deletion.

Inventory must:

- validate the complete envelope/catalog and all referenced payloads under the
  existing bounded readers;
- bind all entries to the one `ResourceId` and one public page/resource scope;
- retain exact package/catalog fingerprints and measured byte evidence;
- count current, retained, original, derived, contained, omitted, unavailable,
  and unsupported observations without conflating them; and
- report New/Merge eligibility facts and sanitized, typed rejection reasons.

Inventory must not open the target write path, mutate SQL or media roots, alter
private stores, contact a source, or create a receipt. Its DTOs are immutable,
path-free, and passive. They are not executable plans, coordinator admissions,
credentials, grants, or proof that Apply will still be possible.

### 4.2 Inventory failure and bounds

A limit, identity, integrity, private-component, root, or representation failure
is a deterministic unsupported/invalid result with no target mutation. There is
no truncation, best-effort cross-page continuation, loose-file discovery, hash
aliasing, or automatic removal of the offending item. Cleanup is limited to the
owned stage and its owned children; unknown or pre-existing files are never
recursively removed.

## 5. Preview and plan semantics

After a valid inventory, the existing coordinator obtains a read-consistent
snapshot of the target resource and creates an immutable plan. The planner
chooses the explicitly requested `New` or `Merge` scope and records:

- exact package/catalog fingerprints and one resource root;
- the target instance/generation/fence and the target values needed for
  conflict classification;
- the selected public content and representation identities;
- actual additive counts (new posts, additive/enriched fields, new original or
  derived bindings, and retained/evidence additions); and
- conflict alternatives, unsupported categories, and any safe subset that may
  be committed without replacing target state.

`UpdatedPosts` in a summary means an explicitly additive field completion or
other contract-approved enrichment. It never means that a source value has won
a replacement race over a target value. Counts are deltas, not totals of all
observed target content.

Preview has no target or private-store side effect. It may read the target under
the existing read/lease boundary and may hold the private stage, but it does not
claim an operation, register a media root, write content, alter favorites or
permissions, or create a success receipt. A preview is not an Apply capability.
A preview-only result is never treated as an original, a downloaded asset, a
successful import, or a replacement for an original that is omitted,
unavailable, or conflicted.

The plan is valid only for the exact stage, fingerprints, explicit scope,
selection, coordinator owner, and target fence that produced it. A plan is not
re-planned implicitly while an Apply call is in flight.

## 6. Apply semantics

Apply is an explicit user action against a matching Ready generation. The host
must be Ready and must enter the existing profile-aware exclusive coordinator
before any target or media write. The Apply path:

1. reads an already committed receipt for the exact operation/request first;
2. claims the operation under the existing resource/history exclusion;
3. revalidates the staged package, catalog, selected bytes, target roots, and
   target fence under their existing leases;
4. records the bounded Prepared evidence before making active content visible;
5. verifies/materializes only B-owned safe public candidates and keeps all
   conflict alternatives separate and durable;
6. performs the additive SQL/content change and the final content-free receipt
   in the same authoritative transaction; and
7. returns the persisted result, including whether it was a replay.

A second Apply for the same in-flight operation joins the actual task and does
not start a second executor. A new inspect cannot displace an applying task.
An Apply with a different package, request hash, scope, resource, or generation
is a different request and cannot reuse the old operation or stage.

The transaction may add safe New content or safe Merge deltas, but it never
performs destructive replacement, target resurrection, source-driven deletion,
private-store mutation, automatic download, or automatic conflict winner
selection. Existing values and bindings remain unless the explicit contract for
that exact category says an additive write is safe.

## 7. Receipt semantics

A successful operation produces one immutable, content-free receipt bound to:

- operation ID and request hash;
- package and catalog fingerprints;
- exact `ResourceId` and explicit `New`/`Merge` scope;
- target instance/fence identity used for the commit;
- committed status and replay indicator; and
- bounded result deltas, including posts, original/derived/retained/evidence
  additions and durable conflict count/identifiers.

The receipt contains no post bodies, media bytes, private values, absolute paths,
source credentials, provider secrets, grants, or executable handles. It is a
historical store fact, not a capability, current freshness proof, ownership
proof, or permission grant.

An exact operation/request receipt is authoritative even if the source stage or
target root is later gone: recovery reports the historical commit and does not
resurrect content. A receipt with a different package/request fingerprint is a
mismatch and cannot be used as success for the new request. A receipt read is
never permission to rerun a different request.

## 8. Cancellation, crash, and recovery semantics

The externally visible state distinguishes at least `Reading`, `Ready`,
`Applying`, `Completed`, `Failed`, `NeedsReview`, `Cancelled`, and
`RecoveryRequired`. These labels describe reconciled facts, not optimistic UI
intent.

- **Before Apply:** cancellation invalidates the Ready plan and releases only
  the session-owned stage. It does not write or create a receipt.
- **During Apply:** cancellation requests cancellation of the owned work. The
  coordinator waits for the actual operation/reconciliation. A late cancellation
  cannot relabel a committed receipt as cancelled; a token being cancelled is
  not evidence that a write did not happen.
- **Known failure before commit:** the target remains unchanged, no success
  receipt is emitted, and the result is failed/cancelled as appropriate. The
  owned stage may be cleaned only after its work has settled.
- **Unknown outcome, crash, or response loss:** read the exact persisted
  receipt first. If it exists and matches, report committed/replay. If it does
  not, an unfinished or malformed operation is `RecoveryRequired`, not an
  invitation to blindly retry. The next action is bounded reconciliation or an
  explicitly new inventory/plan after the old operation is resolved.
- **Prepared without a receipt:** prepared evidence never authorizes an
  unverified final commit. It remains recoverable evidence, blocks unsafe
  duplicate work as required by the existing coordinator, and is not
  auto-promoted to success.
- **Stale target or stage:** a changed target generation, changed root, changed
  package/catalog bytes, changed selected bytes, lost lease, or changed scope
  yields `NeedsReview`/stale. The old plan is discarded; no auto-replan,
  auto-apply, or rebinding to a newly found file is allowed.
- **Disk full, permission failure, or cleanup error:** old target data is not
  overwritten to recover. Only known session-owned, unreferenced temporary
  artifacts may be cleaned. Unknown, foreign, referenced, or pre-existing
  paths are retained for explicit inspection and never recursively deleted.
- **Restart:** startup detects unresolved operations and valid historical
  receipts with bounded readers. A valid receipt can prove historical success
  without the stage or network. An unresolved nonterminal operation remains a
  recovery blocker until reconciled; restart does not start a watch, queue,
  download, grant, or silent repair.

## 9. Explicitly forbidden in this contract

The following are hard admission or execution failures, not optional policy
choices:

| Forbidden behavior | Required contract behavior |
| --- | --- |
| Private metadata without explicit consent | Keep private selection default-off; reject an unconsented private component and never infer consent from package presence, source ownership, or an earlier public choice. |
| Multi-thread Apply before a coordinator exists | Admit exactly one thread/resource only. Do not fan out workers, create a second executor, or claim that independent per-thread tasks equal one atomic import. A future multi-thread lane needs its own coordinator and contract. |
| Rootless package or cross-page/resource content | Require one exact `ResourceId` root and keep every post, attachment, retained binding, and representation under it. A page URL or media hash cannot substitute for the root. |
| Preview-only as original replacement | Preserve original absence/conflict honestly. A preview or poster remains its own derived role and cannot become an original binding, playable-original claim, or target replacement. |
| Stale plan execution | Recheck stage, fingerprints, target instance/generation, root snapshots, scope, and leases. On drift, return stale/review and do not apply the old plan. |
| Automatic conflict overwrite | Preserve the target value, retain a durable alternative/conflict, and apply only independently safe additions. No source-order, latest-wins, hash-only, or `skip-existing` winner is implicit. |
| Credentials, grants, or acquisition authority | Reject credentials, signed/Bearer URLs, auth headers, secrets, profile/plugin-owner IDs, policy/mismatch grants, and executable handles. Public provenance never starts network acquisition, watch, retry, queue, or download. |

These prohibitions also apply to diagnostics, persisted plans, journals,
receipts, conflict records, and recovery code. Redacting a credential after
accepting it is not equivalent to rejecting the private input.

## 10. Compatibility and supersession boundary

The old W30a host-session document and W31 inventory document remain useful for
historical implementation scope, state names, and the sequence in which the
worktree was built. They are not authoritative for v4 Apply after this contract:

- [`native-host-import-session-v1.md`](native-host-import-session-v1.md) is the
  retained W30a session/apply slice; its blanket v4-unsupported statement is
  superseded for the admitted public single-thread v4 shape described here.
- [`native-v4-inventory-v1.md`](native-v4-inventory-v1.md) is the retained W31
  inventory/UI slice; its inventory-only boundary and blanket v4 Apply rejection
  are superseded by this contract, while its separated original/derived counts
  and passive inventory rules remain historical context where compatible.

The wire representation rules in
[`chan-transfer-representations-v4.md`](chan-transfer-representations-v4.md),
versioned persistence/recovery readers in
[`media-transfer-persisted-readers.md`](media-transfer-persisted-readers.md),
and the existing v2 import/merge and schema-11 contracts remain binding. They do
not, by themselves, authorize Desktop Apply; this document supplies that public
single-thread admission and lifecycle boundary. Private metadata, multi-thread
transfer, relocation, foreign interop, and any future grant-bearing flow need a
separate reviewed contract and must not be inferred from this one.
