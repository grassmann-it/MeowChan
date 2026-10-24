# Kohlchan source plugin

`Kohlchan.cs` is a complete editable single-file LynxChan example. It references
only .NET and `MeowChan.PluginSdk`; there is no Core, UI, host implementation or
helper-source requirement. MeowChan.PluginSdk is not stable in 1.x; minor releases
may change its public API.

- Entry type: `MeowChan.Plugins.Examples.Kohlchan.KohlchanPlugin`
- Plugin ID: `meowchan.source.kohlchan`; source ID: `kohlchan`
- Build project: `MeowChan.Plugin.Kohlchan.csproj`
- Release output: `bin/Release/net10.0/MeowChan.Plugin.Kohlchan.dll`

The application packaging should ship the built assembly and editable `.cs`
file. Host installation, compilation, activation and fallback are separate
integration responsibilities. No settings are invented for the example.

## API mapping

Canonical routes are `https://kohlchan.net/{board}/` and
`https://kohlchan.net/{board}/res/{thread}.html`, as observed in the homepage.
Metadata routes are `/{board}/catalog.json` and `/{board}/res/{thread}.json`.
Only this HTTPS host is declared, with metadata and original-media purposes.
Board IDs retain case and are escaped as one URI segment. No aliases, global
board listing or preview capability are offered. Every operation performs one
HostHttp call; all pacing, retries, cancellation and original transfers are host
responsibilities.

Unlike the Lainchan shape, the thread object is itself the opening post, with
`threadId` and `boardUri`; only replies are in `posts` with `postId`. The plugin
validates the returned thread/board identity. Post order uses decimal IDs.
`markdown` is HTML when present; otherwise `message` is plain text. Missing fields
remain absent, explicit empty/null values remain present, and missing posts or
files never imply deletion. `files: []` is an explicit empty attachment
observation. `trash` is not inferred to mean content deletion.

Each ordered file array item supplies an original `/.media/<file>` path and
optional original name, size and MIME. Same-origin single-file paths are required;
external URLs, nested/traversing paths, queries and fragments are rejected. File
extension comes from the original URL path, never a local target path. The
observed hash-shaped URL token is not assumed to be a verified content hash.
Media identity is `{postId}-{position}`, variant `original`, independent of URL
and original filename. Slots may represent replacement; upstream file reordering
cannot be inferred from the documented payload.

The catalog is a flat array. `postCount` is replies; `fileCount` counts reply
files and is not silently relabelled a total. Coverage is conservatively false,
with no pagination cursor. The plugin rejects page/cursor requests because the
catalog endpoint returns the available array in one body. Omitted entries do
not terminate watches.

`archived: true` is the documented LynxChan terminal snapshot and still carries
all supplied final posts/files. `locked`, `autoSage`, `cyclic` and `trash` are
separate flags, not end signals. The archived transition is synthetic-fixture
verified, not a live observed transition. An exact thread-endpoint 404 becomes
NotFound even without a baseline. Catalog 404, 410 and other non-200 statuses are
failures; HTTP 206 or malformed JSON is not accepted as a partial snapshot.
304 requires an accepted snapshot and its HTTP validator, returns that validator,
and never emits an empty snapshot. Parsing/validation failures cannot commit new
validators. HostHttp Deferred/Failure are preserved without local retry loops.

## Public evidence, 2026-10-22

All requests were GETs, bounded to 256 KiB; no cookies, redirects, retries,
authentication, bypass attempts or media downloads. Stored records contain URLs,
UTC timestamps, statuses, selected headers, read-byte counts, SHA-256 hashes,
structural keys and a few media path examples, never real post texts/names or
original filenames. Hashes of unretained bodies cannot independently reconstruct
the source. A limit+1 byte sentinel marks truncation.

- https://kohlchan.net/index.html returned 200, linked normal thread/board routes
  and `http://gitgud.io/LynxChan/LynxChan`, and displayed LynxChan 2.8.5.
- https://raw.githubusercontent.com/skyssolutions/lynxchan/master/doc/Json.txt
  returned 200. This is the archived fork already cited in the project research,
  **not** proof of Kohlchan's deployed revision. Selected API excerpts retained.
- https://kohlchan.net/int/res/28988287.json was selected from the homepage and
  returned 200 (1,309 bytes), the documented OP-object shape, `archived: false`
  and one same-origin original media path. If-Modified-Since then returned 304.
- https://kohlchan.net/cyber/res/0.json returned 404 for a synthetic missing ID.
  This is endpoint-absence evidence, not evidence of a historic thread deletion.
- `/cyber/catalog.json` and `/bus/catalog.json` returned JSON HTTP 200 but exceeded
  the 256-KiB evidence limit. Their truncated bytes were **not** parsed as accepted
  catalogs. Catalog structure is documentation-/fixture-supported; complete live
  catalog parsing remains unconfirmed. No substitute source was introduced.

`Evidence/` contains the exact records. The explicit source information in
Describe calls out the catalog limit. Live thread access is confirmed at the
limited metadata level; this does not claim full product support. Multiple files,
archived snapshot and catalog contents are tested with clearly synthetic data.
Compiler → Worker → Registry → Queue → Store → restart/reader/gallery/export,
real media acquisition and release-package delivery remain separate acceptance
work; these tests do not mark R10-13 complete.

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

API4845 bytes vs served4759 bytes; identity diagnosis confirmed the same representation. No API MD5 expectation.

Conditional in-place EXIF stripping with retained upload size is an evidenced upstream mechanism, not a proven cause of this object.
The provider's deployed historical version/settings are not bound to this review;
the API values are not universally reclassified as upload metadata. See
`Evidence/integrity-research-20261024.json` for source paths, revision/body hashes
and exact scope. No per-format size adjustment, checksum removal, or hash inferred
from an original basename is applied.
