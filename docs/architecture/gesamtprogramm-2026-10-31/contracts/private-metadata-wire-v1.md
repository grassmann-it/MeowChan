# Selected-only private metadata projection v1 (P02)

Technical internal implementation slice of the user-started Profile A goal, based
on P01. Not a new public catalog schema, package extension, import authority,
completed GP12/13, or user decision on a future `chan-v4`.

## Representation

A standalone passive strict UTF-8 JSON object with exactly these fields. The
`threads` array is deliberately **public transfer context**, not a private mark:
thread identities already identify the public archive scope and are always present
in this standalone sidecar. Therefore default-off means no selected private values,
not an anonymisation promise or a guarantee that no thread identity is disclosed.
A future package must bind this context to its public catalog by exact identity.

- `schema`: `meowchan-private-v1`;
- `threads`: unique exact `{source,board,thread}` identities, at most 1,000;
- `threadFavorites`: null (not selected) or an array of thread identities;
- `mediaFavorites`: null or an array of `{thread:{source,board,thread},mediaId,variant,sha256}`;
- `postBookmarksAndNotes`: null or an array of `{thread:{source,board,thread},post,note}`.

Every field is required. Null differs from selected-empty `[]`; neither absent
entries nor empty arrays are deletion/unmarking instructions. Only positive marks
exist. All entries must refer to the explicit `threads` scope, with no duplicate
thread/favorite-key/bookmark-key identity. Identity comparison is exact ordinal,
without URL inference, case folding, normalization or re-keying. Media keys preserve
the existing FavoriteAssetKey tuple; file names/lengths are target observations,
not favorite identity, and are intentionally not transferred by this slice.
Dangling post/media references are passive observations, not invented content.

No owner, lease, store instance/revision/fingerprint, profile path, grants, watch
state, provider authority, tags or arbitrary extension bag is represented. The
public thread context is not an authority or a permission grant. Existing
public `chan-v1/v2/v3` code and envelope acceptance stay unchanged. No standalone
private bytes are accepted into a package yet; later integration must bind the
projection to its public package and preserve selected-only durable storage.

## API and selection

New internal types: `NativePrivateMetadataProjection` (opaque immutable class),
`NativePrivateMetadataCodec` (pure Serialize/Inspect), and typed inspection/error.
`FromSnapshot(snapshot, owner, token)` reads only the P01 snapshot's explicit values,
checks its original lease/store fences inside the lease monitor, and fails closed
on staleness. It copies values, never captures the snapshot or its local authority.
Wire creation is not permission to publish; the eventual exporter still needs a
final P01 recheck at its publication boundary.

`Select(selection = default, token)` produces a new projection with only categories
explicitly chosen by the receiving caller; asking for a category absent from the
input is an error. Default selects nothing regardless of incoming flags. This
pure filtering step performs no store reads/writes and makes no consent claim.
No full private catalog is fed to durable content/conflict consumers in this cut.

## Limits and rejection

Before tests: at most 16 MiB encoded document, 1,000 scoped/thread-favorite entries,
50,000 media favorites, 10,000 post bookmarks; identifiers <=256 UTF-16 code units,
nonempty/nonwhitespace, no controls and valid Unicode. SHA-256 is 64 lowercase
ASCII hex. Notes <=8,000 Unicode codepoints, only tab/newline control characters,
canonical LF (CR/CRLF rejected on wire rather than silently rewriting content).
The writer/parser share validation and fail, never clip. Wire nesting <=12.

Unknown, duplicate (including escaped equivalent names), missing, wrongly typed
fields, comments, trailing commas/content, BOM, invalid UTF-8/unpaired surrogate
escapes, future schemas, out-of-scope entries and all budget violations reject.
Opaque enum/error messages contain no input strings or inner exceptions. Cancellation
retains OperationCanceledException and is checked at entry, loops and buffer writes.
JSON parsing is bounded synchronous work, not a hard wall-clock/OS isolation promise.
Serializer output is capped during writing, not after unbounded growth.

## Remaining

Package framing/integrity integration, import preview/consent, journaled selected
apply, conflict choices/discard and recovery, private tags, Desktop journeys and
native platform acceptance remain separate required work. No personal fixtures.

## P02 review clarifications

The always-present `threads` list is explicitly selected public transfer context,
not a favorite/watch/browsing-history category. `Select(default)` preserves it.
“No private categories selected” is not “no metadata” or anonymisation; an eventual
public-only exporter must omit the private component when no component is wanted.
Thread context is not allowed to become inferred consent or imported authority.

`FromSnapshot` checks owner/fences via P01, then releases that monitor before
copying or validating passive values. This is a point freshness observation, not
an atomic snapshot of future state; publication still needs the original final
recheck. The writer first counts exact canonical encoded bytes through a bounded
non-retaining counter, stopping at 16 MiB before full note normalization and
identity/duplicate scans; it then uses the independently capped output buffer.
Scratch reservations are bounded independently from actually advanced bytes.

Cancellation is cooperative, with final/field/entry checkpoints. The strict UTF-8
scan, bounded JsonDocument parse, selected-store file read/recheck, individual
bounded field encoding and final bounded byte-array copy are synchronous segments,
not interruptible OS I/O or a hard wall-clock promise. Tests must distinguish
pre-cancellation from deterministically proved in-flight cancellation.
