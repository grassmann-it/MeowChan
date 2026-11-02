# Private package component — P03 implementation contract

Internal technical continuation of the selected Profile A transfer requirements.
No source authenticity, consent/apply authority or completed GP12/13 is inferred.
The public catalog and existing chan-v1/v2/v3 meanings do not change.

## Framing

Public-only output remains envelopeVersion 1, byte-compatible with its current
strict seven-field manifest. An explicitly supplied selected private projection
uses envelopeVersion 2 with exactly one additional `privateMetadata` descriptor
(length/sha256), a fixed `private.json` ZIP entry, and exactly the required features
`[contentSchema,"meowchan-private-v1"]`. No optional/unknown fields, null descriptors,
extra entries, duplicate features or private data disguised as a media payload.
The independent component is capped at 16 MiB (caller limits may only lower it).
Entry, directory, total uncompressed and exact stored-container accounting include
this component and its name/headers. Integrity inspection captures its exact bytes
and checks length/SHA256/CRC on the same caller-owned stream, without extraction.

## Semantic binding

After public catalog validation and strict P02 codec validation, private `threads`
must equal the public catalog thread set by exact ResourceId (order irrelevant).
At least one category must be explicitly selected; selected-but-empty is valid.
Each bookmark must reference a captured public post. Each media favorite's full
existing key must match a current or retained **contained** attachment with the
same thread, mediaId, variant and measured payload SHA256. Expected remote hashes,
URL similarity, case folding and synthetic posts are not substitutes. Missing or
omitted assets remain a future representation gate, not an inferred favorite.
No private values or diagnostic source text appear in errors or public catalog.

## Boundaries and ownership

Writer and validator are passive stream APIs only. A private component is not
receiving consent: receiving selection remains independently default-off. P03
adds no profile snapshot/export UI, profile store mutation, conflict/journal or
recovery. Until that next integration exists, the public-only import inventory preview, NativeTransferImportStage and the stream-based text importer reject
private-component packages and cleans its own pending file. Its already-existing
bounded ingress copy can temporarily contain source private bytes; failed cleanup
remains StageRetained, and crash retention is not silently called erased. No
unselected private component is admitted to active store/conflict variants.
Existing public export/import paths keep their original v1 behavior.

## Required checks

Public-only regression, deterministic private package roundtrip, exact byte/size
budget, strict manifest/feature/entry matrices, hash/length/CRC tamper, default-off
and selected-empty distinction, thread/post/media mismatches, current/retained
identity binding, cancellation and stream ownership, diagnostics privacy, and
private-stage rejection/cleanup. Independent review before claiming this slice.
