# Uboachan source (NW-04)

SDK-only, independently editable `Uboachan.cs`; stable plugin ID
`meowchan.source.uboachan`, source ID `uboachan`. Describe is passive. Board and
thread routes use `https://uboachan.net/{board}/` and
`/{board}/res/{thread}.html`; catalog/thread metadata is obtained through
HostHttp at `catalog.json` / `res/{thread}.json`. Only `uboachan.net` is granted,
for metadata and original media; no annex/external-game hosts are included.

The adapter parses page catalogs and full `posts[]` thread observations. It
retains inert HTML, arbitrary-length decimal post IDs, primary attachments at
slot0 and `extra_files` from slot1 (including extra-only posts). `tim` is an
opaque safe token, independent of original filename. Empty original names are
allowed; filenames never determine media identity. Omitted fields do not delete
captured history, and `locked`/`cyclical` do not end a thread.

This source has proven MD5 and SHA-1 values in the same provider field in the
same thread. Following approved NW-01, each valid 16-byte Base64 value maps to
`ExpectedMd5`, and each 20-byte value to the additive `ExpectedSha1`. The original
value is retained per attachment. Malformed or unknown digest lengths fail the
whole observation with `invalid_payload`; nothing is mislabeled or discarded.
The central host/store validates these expectations. Experimental maturity is
informational; default-off activation is handled by shared integration, and this
adapter grants no mismatch exceptions.

## Evidence and limits

NW-00 confirmed 305 posts/69 originals in `/yn/res/513.json`, mixed MD5/SHA-1
against two small originals, and a second thread with two attachments in one
post. A new bounded metadata-only preflight on 2026-10-25 confirms `/yn/` HTML200,
that multi-attachment thread's JSON200 and canonical HTML200, plus a second
`/ot/` catalog200. Clean
receipts are in `Evidence/metadata-preflight-20261025.json`; no raw post content
or originals are shipped as fixtures. Python observations do not establish
productive HostHttp/download/archive/offline acceptance.

Synthetic tests cover slot identities, empty names, mixed digest forms,
malformed/unknown digests, metadata budgets, conditional requests, exact404,
unexpected endpoints, host-owned deferrals and cancellation. The final approved size-hint/SHA1 mappings compiled and passed all **53 combined
synthetic cases** on 2026-10-25, with no skips or errors. The focused run had two
nullable warnings in existing test assertions; plugin projects emitted none.
`makaba-uboachan-focused-tests/binding03.json` outside the checkout binds source,
TRX and log bytes. The earlier46-case run covered the temporary fail-closed
boundary and is retained only as historical evidence.
No posting, board discovery, previews,
archive fallback or last50 truncation is implemented. NW-07 owns actual worker,
original/reader/gallery, restart and package delivery acceptance.
