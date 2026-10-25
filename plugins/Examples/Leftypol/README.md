# Leftypol source plugin

`Leftypol.cs` is a complete editable SDK-only adapter. Its entry type is
`MeowChan.Plugins.Examples.Leftypol.LeftypolPlugin`, plugin ID
`meowchan.source.leftypol`, source ID `leftypol`, assembly
`MeowChan.Plugin.Leftypol.dll`. The only project reference is
`MeowChan.PluginSdk`; there are no runtime settings, sidecar helpers, third-party
parser libraries or native dependencies. Describe is passive and does no I/O.

## Mapping and boundaries

Canonical inputs are `https://leftypol.org/{board}/` and
`https://leftypol.org/{board}/res/{thread}.html`. Metadata endpoints are
`/{board}/catalog.json` and `/{board}/res/{thread}.json`. Exact `leftypol.org` is
the only declared Metadata/OriginalMedia host; mirrors, www aliases, overboards,
last50 pages, previews, posting and global board discovery are not supported.
Board identity retains its case and is escaped once. Thread and post IDs stay
arbitrary-length decimal strings; no fixed-width numeric conversion is used.

Catalog page arrays are flattened conservatively with `IsComplete=false` and no
cursor/page request. `images` is not presented as a verified total attachment
count. Missing catalog entries never finish watches. A valid thread returns Active;
locked/cyclical, embeds, omitted posts and unknown archive flags are not finality
or deletion evidence. No provider tombstone schema has been verified. Missing
posts/fields are not deletion markers; explicit null/empty text retains its
presence information. Present empty `files: []` remains distinguishable from an
absent file observation. A malformed/deleted file record without its required
identity/path fails the read instead of inventing a live file or deletion marker.

Leftypol attachments come from `files[]`, not flat 4chan fields. Each file's
opaque `id` is its stable media identity; array index supplies only the current
per-post Position. `tim` may have suffixes and is never converted to an integer
or used to guess the URL. The exact supplied `file_path` must match the observed
same-board `/{board}/src/{token}` layout on the declared origin. Foreign,
thumbnail, query, encoded-separator and traversal paths fail atomically. The
plugin never grants a host based on an arbitrary metadata URL. `thumb_path`
can describe a WebP thumbnail for an original PNG and is never used as original
media. Original filename plus `ext`, fsize, supplied base64 MD5 and MIME remain
provider metadata. When the provider retains its file ID, local identity survives filename/path changes
and array reordering; no promise is made about future provider ID changes.
Duplicate file IDs in one post are rejected.

Every read uses one HostHttp GET, with central pacing, redirect, cancellation and
body limits. No image bytes, archives, local paths, watches or retry timers are
managed by the adapter. Metadata must parse and pass request-specific SDK
validation before its validators are returned. A 304 needs the accepted snapshot
and a real HTTP validator. Only 404 from the exact requested thread endpoint is
NotFound. Catalog404, 403/410/429/5xx/206, malformed/oversized bodies and foreign
final URLs are failures; host Deferred and Failure remain those exact results.

## Integrity and pending delivery policy

Provider MD5/size are retained unchanged, including the known live mismatch.
The strict host can therefore reject a same-size but different-MD5 original.
The plugin never replaces ExpectedMd5 with a computed value, drops a mismatch,
or grants an exception. Actual file length/SHA256, limits, permission, archival
and offline verification belong to the host.

The initial adapter implementation does not choose a new activation/default or
Experimental policy: NW-01/NW-07 own those decisions. Any optional provider-size/
MD5 exception must be explicitly approved and implemented centrally; activating
or describing this plugin alone does not grant it. These new plugin bytes have
not yet received production host/queue, permission/revocation, archive/offline or
package acceptance.

## Evidence and planned checks

`Evidence/metadata-shapes-20261025.json` records four additional bounded public
GETs: tech thread JSON and HTML, siberia catalog and a small siberia thread.
All four returned200. The record retains exact URLs, times, response hashes and
redacted mapping fields, without post bodies, authors or original filenames.
The two boards confirm files[]/id/file_path/thumb_path, opaque suffixes and JPG
in addition to the prior PNG/WebP observations. The first two urllib requests
used the default redirect handler and stayed at the requested final URL; the
last two disabled redirects explicitly. These are metadata/schema probes, not
product HostHttp/media acceptance. This implementation made no original GETs.

The prior NW-00 observations retain their distinct boundary: the same PNG served
7,533 bytes twice while metadata supplied MD5 `9DIpWh+vpsvo1F0+7wLFzQ==` and the
file measured `inypgQekyZoAAp3XpQYTtg==`; the separate WebP's25,312 bytes and MD5
matched. The cause of the PNG mismatch remains unproven. The parser regression
preserves the mismatching provider value but does not claim it tested queue
rejection. NW-07 must verify strict rejection and any explicitly approved
exception through the real host.

Synthetic `leftypol-{thread,catalog,partial}.json` and `LeftypolPluginTests` cover
multi-file identity/position, current/original paths, inert Unicode HTML, partial
fields, null/empty observations, malformed deletion records, conditional reads,
HTTP outcomes, host failures/cancellation and budgets. A shared test-project
reference, SDK-free editable compilation, real worker, productive archive and
fresh offline process remain integration work until separately evidenced.

The adapter independently adapts this repository's existing SDK example patterns.
No external PHP/JavaScript/engine parser code was copied.

## Local verification of this adapter

On 2026-10-25 an isolated verification project using the repository's existing
Microsoft.NET.Test.Sdk17.12.0 / xUnit2.9.2 / runner2.8.2 versions built this plugin
and ran the exact dedicated `LeftypolPluginTests.cs`: **31 passed,0 failed,0
skipped**, with no warnings on the final run. Restore/build/test were serialized
under `/tmp/meowchan-ui-1.0-build.lock`. The temporary project only links that
scoped test file and these fixtures; the normal shared ExamplePlugins project
still requires its delivery-owned ProjectReference. Local logs/TRX remain under
`artifacts/leftypol-verification/` in the implementation worktree. This is neither
an SDK-free compiler/worker proof nor a host download/archive/package PASS.
