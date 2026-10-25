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

**Dependent mapping still open:** this source has proven MD5 and SHA-1 values in
the same field in the same thread. Valid 16-byte Base64 values map to ExpectedMd5.
Until NW-01 integrates a dedicated SHA-1 expectation across the SDK/host/store,
a 20-byte digest explicitly fails the whole observation with
`unsupported_integrity_contract`. Malformed or unknown digest lengths fail with
`invalid_payload`. Nothing is mislabeled as MD5/SHA256 or silently discarded.
This is staged parser work, not complete source/media acceptance. Experimental
maturity and default activation remain coordinated with NW-07.

## Evidence and limits

NW-00 confirmed 305 posts/69 originals in `/yn/res/513.json`, mixed MD5/SHA-1
against two small originals, and a second thread with two attachments in one
post. A new bounded metadata-only preflight on2026-10-25 confirms `/yn/` HTML200,
that multi-attachment thread's JSON200 and a second `/ot/` catalog200. Clean
receipts are in `Evidence/metadata-preflight-20261025.json`; no raw post content
or originals are shipped as fixtures. Python observations do not establish
productive HostHttp/download/archive/offline acceptance.

Synthetic tests cover slot identities, empty names, mixed digest forms,
malformed/unknown digests, metadata budgets, conditional requests, exact404,
unexpected endpoints, host-owned deferrals and cancellation. Tests have not yet
been executed at this staged checkpoint. No posting, board discovery, previews,
archive fallback or last50 truncation is implemented. NW-07 owns actual worker,
original/reader/gallery, restart and package delivery acceptance.
