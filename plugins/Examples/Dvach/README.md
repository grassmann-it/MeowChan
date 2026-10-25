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

The approved NW-01 contract preserves numeric `size` as `ProviderSizeHint` with
its original JSON numeric spelling, provider label `Кб` and explicitly
`Unspecified` precision. `ExpectedSize` remains null: no exact byte length or
acceptance range is invented. The provider MD5 is still passed to the central
strict integrity check; this adapter grants no mismatch exception. Experimental
maturity is informational. New-source default-off activation is handled by the
shared delivery/activation integration, not by Describe.

## Evidence and limits

The NW-00 preflight recorded one matching JPEG (25,475 bytes, API size24) and
`.hk` → `.su` original redirect. A new bounded metadata/HTML preflight on
2026-10-25 confirms `.su` board and thread HTML200, `.hk` board301 to `.su`,
`/pr/` catalog/thread shapes and a second `/mu/` catalog. Clean receipts are in
`Evidence/metadata-preflight-20261025.json`; raw post bodies are not test fixtures.
These are Python HTTP observations, not runtime/plugin-worker or download tests.

Synthetic tests cover multiple attachments, null/empty files, escaping, foreign
paths/endpoints, hash conversion, status/304/cancellation/size boundaries and
lossless identities. The final approved size-hint/SHA1 mappings compiled and passed all **53 combined
synthetic cases** on 2026-10-25, with no skips or errors. The focused run had two
nullable warnings in existing test assertions; plugin projects emitted none.
`makaba-uboachan-focused-tests/binding03.json` outside the checkout binds source,
TRX and log bytes. The earlier46-case run covered the temporary fail-closed
boundary and is retained only as historical evidence.
No automatic board list, preview retrieval, posting/login, archive fallback or
Delta API is supplied. Productive original/reader/gallery/offline-restart
acceptance and native package delivery belong to NW-07.
