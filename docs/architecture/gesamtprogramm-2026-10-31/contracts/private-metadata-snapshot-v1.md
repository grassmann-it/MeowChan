# Private metadata read snapshot v1 (P01)

Status: technical implementation slice for the active Profil-A goal run. This
contract is deliberately narrower than the native private-metadata roundtrip:
it supplies a bounded, selected-only read snapshot and does not define wire
fields, import apply, conflict candidates, tags, or a durable journal.

## Scope

`NativePrivateMetadataSnapshot` is an opaque, in-process handoff for a later
native export planner. It captures only categories explicitly requested by the
caller:

- thread favorites;
- media favorites;
- post bookmarks and notes.

Private tags are not silently represented: their persistent producer and mapping
contract are still GP30/31 work. Ratings, reader groups, watch state, grants,
credentials, absolute profile paths and provider state are out of scope.

A caller must provide an explicit bounded thread scope (at most 1,000 valid
`ResourceId`s). A value outside that scope is not exported. An empty selected
category is distinct from a category that was not selected. The snapshot retains its owner binding and scalar fences only in-process; it
stores no mutable store object and exposes no private values or paths through
diagnostic stringification. It must never be serialised as transfer authority.

## Consistency and privacy

All selected stores are read during one short synchronous `ProfileLease` critical
section. The snapshot retains a fence for every selected store and can be
rechecked against the same profile lease before any later exporter uses it. A
store changed anywhere in the selected store invalidates the snapshot; this is
conservative and avoids pretending that several JSON files form one ACID
transaction. Unselected stores are not opened and cannot block a public-only
export, even if they are malformed.

The snapshot is immutable and defensive. Callers cannot mutate the selected
entries, scope, or category presence by retaining an input collection. The
owner/lease binding is in-process authority only and is never serialised. A
snapshot from another profile lease cannot be accepted, and a disposed original
lease throws. A fresh store object under the same active lease is permitted.
Rechecking is a read-only operation; it does not repair, migrate, recover, or
write a store.

## Failure and limits

Missing selected stores read as their existing empty-store value. A present
selected store that is malformed, a link/directory/special file, invalid UTF-8,
over its bounded size, or requires recovery fails closed. Scope and entry limits fail
closed; no clipping or implicit fallback is allowed. Existing store validation
remains authoritative. The snapshot is therefore suitable for preview planning,
not a proof that a later SQL/manifest/media read will still be current.

## Deferred integration

The next producer must project these values into a separate selected-only private
projection before feeding the existing public catalog writer. It must not pass a
full catalog containing unselected notes/favorites/tags into durable variants or
conflict journals. Versioned wire data, selected-entry import, conflicts,
resolve/discard, crash recovery, and Desktop preview require separate slices and
reviews. This file does not claim those features are implemented.

## Local storage compatibility

Media favorites continue to read schema v1 without migrating it. Only an actual
mutation writes v2 with a per-file instance GUID and increasing revision; no-op
or cancelled writes leave the bytes alone. The canonical parser is also used by
the Desktop cleanup protection inventory for both primary and backup bytes. All
other protection-store schemas remain unchanged. A future or damaged backup
blocks rotation, rather than destroying recovery evidence.

## Regular-file read boundary (P01 followup)

The three selected store readers and Desktop cleanup's protection-file inventory
share `RegularProfileFile.OpenRead`. Linux/macOS x64/arm64 first inspect the final
path with normalized .NET PAL `lstat`, then use nonblocking/no-follow/close-on-exec
`open` and validate the actual opened handle with PAL `fstat`. A swap to a FIFO
cannot block waiting for a writer. A shared nonblocking advisory lock preserves
the managed exclusive-writer contract where the runtime enables/supports locking;
as with .NET FileStream, only contention rejects a read (other advisory-lock
errors are best-effort). The runtime locking-disable switch/environment setting
is respected. Windows opens the final reparse point
itself and checks disk type and attributes on that handle before reading. Other
Unix/architecture mappings fail closed rather than guessing native flags.

The stream owns only the validated handle; consumers retain their independent
byte limits, strict parsing, EOF and length recheck. This is not an atomic
transaction against arbitrary external writers, a filesystem-wide no-symlink
walk, or a hard wall-clock bound on ordinary disk/network filesystem IO.
`O_NONBLOCK` also does not promise every device driver's open is side-effect-free
or nonblocking after hostile path replacement; static special files are rejected
before opening and successfully opened nonregular objects before reading. Native
Windows/macOS runtime proof remains a separate acceptance gate.

Missing remains distinguishable from invalid or unreadable. IO/access failures
across complete store/cleanup read boundaries are path-free and contain no raw
inner exception; failures never trigger repair or become an empty selected
store. Negative FIFO tests use child-process watchdogs for the aggregate so even
a broken blocking implementation cannot hold the parent test runner's profile
monitor indefinitely. The subprocess entry test is an explicit top-level skip;
its completion is required by the 15 parent FIFO cases.
