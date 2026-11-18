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
A stated hex MD5 is validated for format (a malformed value still fails the whole
observation) but is not passed on: see "Integrity" below.
`closed`, `endless` and `is_closed` are not treated as proven archive/end signals.

The approved NW-01 contract preserves numeric `size` as `ProviderSizeHint` with
its original JSON numeric spelling, provider label `Кб` and explicitly
`Unspecified` precision. `ExpectedSize` remains null: no exact byte length or
acceptance range is invented. Experimental maturity is informational. New-source
default-off activation is handled by the shared delivery/activation integration,
not by Describe.

## Integrity

`ExpectedMd5` is always null (since 11.11.2026). Three live stability runs showed that for
current 2ch files both the `md5` field and the Кб `size` describe a different file than the
bytes 2ch serves; 2ch apparently re-encodes after upload. The MD5 is therefore not an
integrity expectation. The stability contract (`docs/plugins/source-stability.md`, S2)
treats 2ch like 7chan: `integrityBasis: "none"`, meaning the HTTP `Content-Length` equals
the received bytes, the file decodes, and it reopens offline after a restart. The host's
strict experimental lock for 2ch is unchanged; with no expectation there is nothing to
grant or to mismatch.

Consequences for archives made before this change: the attachment revision key hashes the
reported metadata, so the first re-check of an already archived thread gives each
attachment a new key. Each original is downloaded **once more** on that first re-check
(into the same asset, no duplicate tile, nothing superseded); favorites and ratings are
keyed by bytes and are unaffected; private media tags are carried over to the new key by
the host (`MediaTagKeyCarryOver`) when the bytes are identical. The old tag assignment is
kept next to the new one, so the tag manager counts distinct files, not assignments, and
removing a tag from a file removes it under every key of that file (it cannot come back).
A stop between the media commit and the tag write is repaired by a background pass after
the next start (tagged threads only) and by the next capture of the thread. Threads that
are no longer re-checked keep their old keys and stay readable unchanged.

Stickers (`sticker` set, path `/stickers/{pack}/{name}`) are served outside the
board's `src` tree and carry an empty `md5`. They become attachments on `.hk` with
no checksum expectation; every other file still needs a board `src` path, and its
`md5` field, when present, must be well-formed (an empty or malformed one fails) even
though it is no longer compared.

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
synthetic cases** on 2026-10-25 (the MD5 mapping in that run was replaced by the Integrity section above), with no skips or errors. The focused run had two
nullable warnings in existing test assertions; plugin projects emitted none.
`makaba-uboachan-focused-tests/binding03.json` outside the checkout binds source,
TRX and log bytes. The earlier46-case run covered the temporary fail-closed
boundary and is retained only as historical evidence.
No automatic board list, preview retrieval, posting/login, archive fallback or
Delta API is supplied. Productive original/reader/gallery/offline-restart
acceptance and native package delivery belong to NW-07.
