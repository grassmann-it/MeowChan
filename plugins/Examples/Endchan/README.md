# Endchan source plugin

`Endchan.cs` is the complete editable SDK-only plugin. The separately built
`MeowChan.Plugin.Endchan.dll` uses the same plugin ID `meowchan.source.endchan`
and source ID `endchan`. Use the existing plugin candidate-selection workflow
when both forms are present. No resources or private parser assembly are needed.
The project references only `MeowChan.PluginSdk`; it does not extend SDK or Core.

The example exposes thread reading, known-board catalogs, post capture and
original-media metadata. Canonical URLs use `https://endchan.net/{board}/` and
`https://endchan.net/{board}/res/{thread}.html`. Matching `.org` URLs are aliases
that normalize to `.net`; metadata and media declarations admit only `.net`.
Board IDs preserve case and accept ASCII letters, digits and underscores;
thread IDs are decimal strings without numeric conversion. Merged `+` boards,
`.gg`, `eu.*`, `www`, preview loading and global board discovery are unsupported.

Metadata uses one host-managed GET to `/{board}/catalog.json` or
`/{board}/res/{thread}.json`. The host owns pacing, retries and cancellation.
Catalogs are conservatively incomplete, accept no pagination, expose `postCount`
as replies and do not mislabel reply-only `fileCount` as total attachments.
The OP uses `threadId`, replies use `postId`; poster `id` is ignored. Ordered
`files` produce stable `<postId>-<position>` identities independently of paths
or original names. Only flat same-origin `/.media/` original paths are accepted.
Hash-looking filenames are not treated as checksums. Thumbnails are not loaded.

Missing fields remain absent; explicit null text does not fall back to another
field, and explicit empty `files` differs from omitted `files`. Partial responses
do not claim post/media deletion. Only the exact requested thread JSON endpoint's
404 yields NotFound; 410, lock flags, catalog omissions, malformed payloads and
other errors do not imply thread end. All snapshots remain Active: no verified
Endchan archive signal exists, including any unexpected `archived` property.
304 requires an accepted snapshot plus HTTP validator and retains that baseline.
Payload validators are accepted only after SDK validation of a valid snapshot.

Evidence collected on 2026-10-24 comprises eight bounded public metadata or
documentation GETs, 262335 received bytes, no retries, authentication, redirect
following or original/thumbnail download. See [observations](Evidence/observations.json)
for URLs, response hashes, distinctions between observed/documented behavior and
unknowns. `.net` as canonical is local policy: `.org` served identical thread bytes
directly, not a canonical redirect. The operator-linked fork documentation does
not establish the deployed server revision. No upstream implementation code was
copied; this file adapts this repository's Kohlchan example.

`EndchanPluginTests` and `Fixtures/endchan-*.json` contain invented fixtures,
including documented multifile shape, duplicate filenames, partial observations,
nonterminal flags and failure/validator boundaries. Their invented `size` fields
are metadata parser inputs, not real image sizes or live media evidence. Runtime
parser, SDK-free compilation and production download/archive acceptance are
separate checks; bounded live metadata alone does not establish those outcomes.

## Experimental maturity and source permission

This source declares `SourceMaturity.Experimental`. This marks limited confidence
in provider observations; it grants no access and disables no integrity checks.
Accepting provider size or MD5 discrepancies requires an explicit one-time opt-in
for this source in each profile; missing permission means false. Files matching
provider expectations work without this mismatch permission. Manage the permission
on Sources, separately from plugin installation/activation and this maturity label.
Plugin settings do not grant permission. Provider SHA256, transport completeness,
stage/local SHA256, actual byte limits and filter limits remain strict.

Provider size and any supplied checksum remain unchanged as Expected metadata.
The host owns actual downloaded length/hashes, transport limits, local verification
and any separately approved mismatch policy. Experimental does not mean an
HTTP200 body matched provider size/hash, nor that a previous failed live case is
now successful.

LIVE03 original33534 bytes vs API33552 bytes; no independent identity/no-auto-decompression diagnostic in that historical observation.

Operator-linked fork copies input size/hash into file metadata. A stripping/reencode cause is NOT established in these inspected fork modules; do not transfer general LynxChan findings.
The provider's deployed historical version/settings are not bound to this review;
the API values are not universally reclassified as upload metadata. See
`Evidence/integrity-research-20261024.json` for source paths, revision/body hashes
and exact scope. No per-format size adjustment, checksum removal, or hash inferred
from an original basename is applied.
