# P04 — Receiving selection and passive private comparison

Technical continuation of Profile A G4/G5; not a reduction of GP12/13 and not an
apply command. P03 package framing and all existing private-import rejection gates
remain unchanged. Root implements this pure producer while disjoint workers test
it and prepare the subsequent recovery contract. No personal profiles or media.

## API and data boundary

New internal `NativePrivateMetadataComparison.Analyze(incoming, targetCatalog,
targetMetadata, selection = default, cancellationToken = default)` accepts bounded
passive projections. Both private projections pass the P02 codec; targetCatalog
passes the existing bounded serializer and metadata-only catalog validator
(contained claims projected to unavailable without fabricated payload measurements;
**not** new payload verification). Incoming must already have its P03 package binding checked
by its caller. The comparison itself is deliberately usable on passive projections
and must never be accepted by Apply as package/owner/store authority.

The target private projection must describe the same exact thread scope as the
incoming projection, and explicitly include every requested category. It may
contain additional offered categories; they are filtered out of the result. The
target public catalog may contain a subset of this scope (missing target threads)
but no out-of-scope thread. No case/Unicode/URL/hash-only identity inference.

The returned sealed class contains `Selection`, `SelectedIncoming`, and immutable
`ThreadFavorites`, `MediaFavorites`, `PostBookmarksAndNotes` arrays. Rows are sealed
classes with `Target` (typed key) and `Disposition`; bookmark rows additionally have
`IncomingNote`. Dispositions: `Add`, `Identical`, `Conflict`, `MissingTarget`.
`Add` is only a comparison candidate, never proof of verified bytes or permission
to mutate. No target-only entries, old target notes or unselected incoming values
are retained. Class ToString is opaque; no record-generated diagnostics with notes
or identities. No IO, clocks, callbacks, provider calls, store mutation or journaling.

## Rules

- Receiving selection is independently default-off, not inherited from sender.
  Requesting an unoffered category fails closed; selected-empty remains selected.
- Missing public target thread takes precedence even if an orphan local mark
  exists; a missing captured post takes precedence for bookmarks only. Retained
  media can qualify without a captured post and does not invent one. Deleted but captured posts count
  as present; 404 source state does not block local additive comparison.
- Thread star: exact existing public thread + mark absent => Add, present =>
  Identical; missing public thread => MissingTarget.
- Media: exact full existing FavoriteAssetKey on a contained current or retained
  target attachment => Add/Identical; otherwise MissingTarget. No SHA-only,
  preview-only, unavailable or omitted substitute. These are structural payload
  claims, not freshly verified target bytes; Apply must re-resolve and verify.
- Bookmark: exact captured post absent => MissingTarget; bookmark absent => Add;
  normalized notes equal => Identical; otherwise Conflict, including empty versus
  nonempty. Keep target, never last-writer-wins. The selected incoming note is
  available explicitly for a future inert UI/conflict candidate; old target note
  is not copied into an undo/history object.
- Package absence and empty selections never mean deletion/unmarking.
- Work/bytes bounded by existing codec/catalog limits before building indices.
  Cancellation before/through validation and loops, no partial successful result.
  Invalid inputs yield opaque InvalidDataException with no private inner exception.

## Next apply/recovery gate (not implemented by P04)

Selected-only durable incoming values (including conflicting incoming notes) are
required, not a hash-only journal. Do not journal old target notes or whole store
images. Store steps require exact before/expected-after fingerprints/revisions,
profile/catalog lifecycle fences and readback. Never claim common JSON/SQL ACID.
Unknown outcome is inspection-first, not automatic replay; already applied marks
must not reappear after a local removal. A fresh explicit import needs a fresh
comparison and confirmation. Consent is not inferred from a comparison DTO.
Until this contract is implemented/reviewed, no private Stage/Text/Preview admission
or active store write is enabled. Desktop selection integration is still open.
