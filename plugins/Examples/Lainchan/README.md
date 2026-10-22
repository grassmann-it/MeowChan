# Lainchan example source

`Lainchan.cs` is a complete editable single-file plugin. It references only .NET and
`MeowChan.PluginSdk`; there is no Core, UI, host implementation or helper-source
requirement. MeowChan.PluginSdk is not stable in 1.x; minor releases may change its
public API.

- Entry type: `MeowChan.Plugins.Examples.Lainchan.LainchanPlugin`
- Plugin ID: `meowchan.source.lainchan`; source ID: `lainchan`
- Build project: `MeowChan.Plugin.Lainchan.csproj`
- Release output: `bin/Release/net10.0/MeowChan.Plugin.Lainchan.dll`

The application packaging should ship the built assembly and editable `.cs` file.
These files alone do not prove Compiler → Worker → Registry → Queue → Store →
restart/reader integration. Installation, activation and fallback belong to the
host, and require their independent acceptance checks. No plugin settings are
needed.

## Supported observations

Canonical input routes are `https://lainchan.org/{board}/` and
`https://lainchan.org/{board}/res/{thread}.html`. Query and fragment handling is the
SDK host router's responsibility; no unverified host aliases or title routes are
claimed. Board IDs retain case and are escaped as a single URI segment. Explicit
board catalogs use `/{board}/catalog.json`, threads `/{board}/res/{thread}.json`.
Global board discovery is not offered. All metadata requests use `IHostHttp` once;
provider cooldown, retry, cancellation and original downloads remain host work.

The paged catalog array is flattened with canonical thread references; its
completeness is conservatively false, with no next cursor. The endpoint accepts
no paging request: its body already contains the returned pages. Missing catalog
entries never imply thread end. `com` is inert HTML. Decimal post IDs determine
order, independently of the position in a partial response. Missing fields stay
absent; explicit empty/null text stays present. Missing posts and missing
attachments never become deletion markers. A supplied empty `extra_files` array
with no primary file is an explicit empty attachment observation; the host must
retain previously captured bytes under its normal merge contract.

Primary attachments occupy position 0; extra files retain positions 1 onward,
even when the primary file is absent. Media identity is `{postId}-{position}` with
variant `original`, independent of download URL and original filename. This maps
the provider's ordered file slots; it does not claim that upstream reordering can
be distinguished from replacement. File `tim` is an opaque provider token
(including observed hyphen suffixes), and original URLs are
`/{board}/src/{tim}{ext}`. MD5 metadata is base64 as documented by the upstream
API implementation. No thumbnail capability is declared.

HTTP 304 requires the caller's accepted snapshot and a nonempty HTTP validator;
the result references that accepted validator, never an empty snapshot. New
validators travel only with parsed, SDK-valid snapshots. Only a 404 from the
exact requested thread endpoint becomes `ThreadNotFound`, including without a
baseline. Catalog 404, 410, 403, 429, 503, 206, invalid JSON and oversized bodies
remain failures (or preserve HostHttp's Deferred/Failure). `locked`, `cyclical`
and an invented `archived` property do not terminate Lainchan watches.

## Evidence and limits

`Evidence/` contains dated metadata, SHA-256 hashes and selected source excerpts.
The earlier 2026-10-22 collection was reviewed before implementation and is copied
with its original readme. The added API-code and thread-HTML requests were also
bounded to 256 KiB, no cookies, redirects, retries or media requests. No real post
bodies, author names, original filenames or image bytes were retained.

- Homepage → official repository: https://github.com/lainchan/lainchan
- Upstream API: https://raw.githubusercontent.com/lainchan/lainchan/master/inc/api.php
- Observed catalog: https://lainchan.org/sec/catalog.json
- Observed JSON thread: https://lainchan.org/sec/res/27498.json
- Media-path evidence: https://lainchan.org/sec/res/27498.html

The upstream moving branch is not proof of the deployed revision. Earlier real
ETag/304 and missing-thread 404 are evidenced; no historic deletion or live
archive transition was observed. An `omitted_posts > 0` observation stays partial;
SDK 1.0 has no separate thread-coverage field, so no completeness claim is made
for any thread snapshot. Catalog coverage and absence never delete history.

Synthetic fixtures and tests are under `tests/MeowChan.ExamplePlugins.Tests/`.
They are not live captures. Full product E2E, offline persistence/export after
restart, real media acquisition and release-package delivery remain separate
acceptance work; the limited API checks do not mark R10-04 complete.
