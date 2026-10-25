# 2ch / Двач source (NW-02)

SDK-only, independently editable `Dvach.cs`; no Core/Host dependency or helper
assembly. Stable plugin ID `meowchan.source.2ch`, source ID `2ch`.

Metadata is requested from `https://2ch.hk/{board}/catalog.json` and
`https://2ch.hk/{board}/res/{thread}.json` through HostHttp. Canonical human
board/thread links use `2ch.su`; the corresponding observed `2ch.hk` redirects
are accepted aliases. Only `.hk` is declared for metadata. Originals retain the
provider's `.hk` path; both `.hk` and `.su` are declared for original media so the
host can validate the observed redirect. No plugin downloads original bytes.

The adapter reads catalog `threads[]` and full thread `threads[0].posts[]`;
post IDs are lossless decimal identities. `comment` is inert HTML. `files:null`
is unknown, while `files:[]` explicitly supplies an empty attachment observation.
Attachment IDs depend on post identity and slot, never names or redirect hosts.
Hex MD5 is validated and converted to the SDK's 16-byte Base64 expectation.
`closed`, `endless` and `is_closed` are not treated as proven archive/end signals.

**Dependent mapping still open:** Makaba's numeric `size` is a provider-unit hint,
not exact bytes. Until NW-01's raw-size-hint field is integrated, this working
adapter explicitly fails a thread containing that field with
`unsupported_integrity_contract`; it never drops the value or treats 24 as either
24 bytes or exactly 24 KiB. This is staged parser work, not a shippable source or
an acceptance PASS. Source maturity/default activation is coordinated separately.

## Evidence and limits

The NW-00 preflight recorded one matching JPEG (25,475 bytes, API size24) and
`.hk` → `.su` original redirect. A new bounded metadata/HTML preflight on
2026-10-25 confirms `.su` board and thread HTML200, `.hk` board301 to `.su`,
`/pr/` catalog/thread shapes and a second `/mu/` catalog. Clean receipts are in
`Evidence/metadata-preflight-20261025.json`; raw post bodies are not test fixtures.
These are Python HTTP observations, not runtime/plugin-worker or download tests.

Synthetic tests cover multiple attachments, null/empty files, escaping, foreign
paths/endpoints, hash conversion, status/304/cancellation/size boundaries and
lossless identities. The two SDK-only projects compile and all 46 combined Dvach/Uboachan synthetic
cases pass under the coordinated Linux test slot; no warnings or skipped cases.
This covers the staged fail-closed paths, not the pending final mappings.
No automatic board list, preview retrieval, posting/login, archive fallback or
Delta API is supplied. Productive original/reader/gallery/offline-restart
acceptance and native package delivery belong to NW-07.
