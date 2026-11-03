# Wave 8 — NativeTransferCuratedProjection boundary tests

## Scope and evidence status

Only the following files were written in this follow-up:

- `tests/MeowChan.Tests/Transfer/NativeTransferCuratedProjectionBoundaryTests.cs`
- `review/wave8-curated-boundaries-sol.md`

**Static implementation only: no build or test was run.** No product/existing-test,
RUNSTATE, artifact snapshot, profile, or media changes; no Git writes or commits.
One accidental read-only `git status --short` scoped to these two paths was issued
before authoring; this violated the instruction not to invoke Git. No further Git
commands were used. This is not hidden by claiming there were no Git operations.

Read: existing `NativeTransferCuratedProjectionTests` synthetic fixture; current
projection; `CuratedExportSelection.Freeze` and occurrence/ledger contracts;
`NativeTransferCatalog`, serializer and validator; media integrity/revision and
asset-snapshot types; G5/G6 of `ENTSCHEIDUNGEN.md` and
`contracts/transfer-retained-bindings-v2.md`. The earlier Wave-7 review describes
an older projection; its findings are not treated as fresh findings on this code.

## New coverage

The file declares **26 cases** (4 Facts and 22 InlineData rows), not 26 executed
or passing tests:

1. Exact multi-thread scope with source-order preservation. Selected, scoped but
   unselected, and out-of-scope threads deliberately share post ID, position,
   media ID, filename, and payload hash. Equal bytes cannot select another thread.
2. Both selected threads can independently retain their originals despite shared
   payload bytes; public occurrences are not deduplicated by hash.
3. Missing scoped thread rejects even if that thread has no selected favorite.
4. Chosen occurrence absent from the public catalog rejects for current and
   retained representations, despite another thread's equal-byte attachment.
5. One favorite has two exact bound occurrences to the same asset: removing just
   one occurrence rejects even when the other remains represented (current and
   retained variants). A single successful hit is insufficient.
6. Independently re-keyed, otherwise valid public observations with different
   media ID, variant, position or post ID reject instead of matching by payload.
7. Exact chosen reference with public `Preview` role rejects. The public catalog
   is validated first, isolating the projection's original-role requirement.
8. Same thread/post/media in another source or board cannot substitute for a
   missing chosen occurrence. Resource-local keys are recomputed; no forged
   revision is used as a shortcut to trigger the failure.
9. Out-of-scope malformed input rejects before filtering: forged revision,
   inconsistent presence, mismatched parallel metadata/attachment arrays, and
   unsafe thread-page URL. These have a valid positive-control projection before
   corruption, so an unrelated bad fixture cannot satisfy the negative test.
10. Selected `Contained`, `Omitted`, and `Unavailable` stay honest for current and
    retained observations. Contained retains exact payload/history; the other two
    stay null/null without being promoted. Retained observations invent no posts.
11. Legal chan-v3 unattested contained observation with another measured payload
    hash rejects against the selected favorite, for current and retained cases.

## Fixture and assertion quality

Fixtures mirror the existing in-memory selection pattern: real `Freeze`, real
revision-key functions, one immutable manifest per resource, 64-character asset
IDs, and deterministic synthetic bytes/IDs/time. No store handle is dereferenced;
synthetic paths are inert strings, not opened or created. The optional two-
occurrence case uses one asset with two valid attachment bindings, not two fake
favorites for one reference.

For valid inputs and successful results, a test-only helper serializes the actual
catalog and inspects it with the existing validator. SHA-256, MD5, SHA-1 and lengths
are computed from the synthetic arrays. These are real measurements of those
arrays, not placeholder receipts for actual user files. This separates valid-
catalog selection failures from unrelated serializer/validator failures. Deliberate
malformed inputs instead assert projection rejection after a valid control.

Assertions check field/status/hash/history values and exact resource/order, not
record `ToString()`. The source-order case checks source serialization unchanged
and metadata-object retention. No public test method exposes an internal enum in
its signature: status theories use strings converted inside the test.

## Unresolved discovered issues / handoff

No new product defect is established by this static test-only follow-up. The
new cases are **not compiled or executed**, so compile/runtime/assertion outcomes
remain unresolved for the root integrator's authorized serial runner. No PASS,
independent review, release, or full-goal completion is asserted.

The current projection intentionally validates descriptors rather than attesting
contained media bytes/history. Preserving selected `Omitted`/`Unavailable` is not
a successful-original-export receipt. Writer/byte verification, live source-fence
rechecks and integration remain outside this task; these tests neither implement
nor prove those capabilities. Likewise, this is not a new private-metadata,
UI/import/apply/recovery, merge, preview-decoder or provider-activation path.
