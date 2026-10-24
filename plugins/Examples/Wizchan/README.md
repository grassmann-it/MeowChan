# Wizchan source plugin

Complete editable SDK-only `Wizchan.cs`; entry type `MeowChan.Plugins.Examples.Wizchan.WizchanPlugin`, plugin ID `meowchan.source.wizchan`, source ID `wizchan`. Project `MeowChan.Plugin.Wizchan.csproj` builds `MeowChan.Plugin.Wizchan.dll`; delivery includes the prebuilt assembly and editable source. Both share the plugin ID and use the existing explicit candidate-selection policy. No settings or external helper files are required. The SDK is not promised stable in 1.x.

## Supported observations

Canonical inputs: `https://wizchan.org/{board}/` and `https://wizchan.org/{board}/res/{thread}.html`; homepage-linked `/{board}/index.html` is a board alias. Only exact `wizchan.org` is declared, no www or mirror. Query/fragment routing belongs to the SDK host. Board case is preserved; the board is escaped once as one segment. Thread IDs are decimal strings, without integer conversion; leading-zero IDs are rejected by the parser. Explicit-board catalog JSON is `/{board}/catalog.json`, thread JSON `/{board}/res/{thread}.json`. No global board discovery or preview capability.

Catalog pages are flattened with conservative `IsComplete=false` and no cursor/page request. Missing catalog entries do not end watches. Posts retain decimal identity/order, inert HTML and field presence: absent is different from empty/null. Missing posts or attachments never delete saved history. `omitted_posts` is not a completeness or deletion signal. There is no separate thread-coverage field in SDK1.0.

Original metadata uses `/{board}/src/{tim}{ext}`, opaque provider token (JSON string or number; null/boolean values are invalid), filename plus extension, expected size and base64 MD5. Primary file slot is0; `extra_files` slots start at1 even with no primary. Media identity `{postId}-{position}` is independent of original name/URL. Upstream reordering/replacement cannot be distinguished beyond its ordered slots. Embeds and external reverse-image-search links are not original media; no extra host grants are inferred. The host downloads originals; this plugin never reads image bytes.

Each read makes one host HTTP call. Host pacing/retry/cancellation apply. Valid200 bodies must parse and pass request-specific SDK validation before validators are published.304 requires an accepted snapshot and HTTP ETag/Last-Modified; the accepted validators are returned, not the unaccepted response validators. Only404 from the exact requested thread endpoint becomes `ThreadNotFound`. Catalog404,410,403,429,5xx,206 and malformed/oversized bodies remain failures or preserve host Deferred/Failure. `locked`, `cyclical`, arbitrary `archived` and catalog absence are not verified end flags. Successful snapshots stay Active.

## Evidence, provenance and limits

`Evidence/` records nine bounded public GETs on2026-10-24 (256KiB each, no redirects/cookies/retries/media). Exact UTC/URL/status/bodyhash and redacted shapes are retained, not post bodies, authors or original filenames. Eight initial requests were followed by one explicitly authorized board-directory check. Live catalog200 (99 entries), small three-post thread200, threadHTML200, board-directory200, ETag304 and missing-thread404 were observed. Original paths are from HTML, not acquired image bytes. `index.html` is linked by the homepage; the slash route was fetched directly. Conditional catalog304, actual deletion/archive transition and live multifile were not observed.

Homepage links the official engine repository <https://github.com/fallenPineapple/NPFchan>. Its API source documents first-file plus `extra_files` and base64 MD5; the current deployed revision is unknown. Selected short API lines and license summary are research evidence. No PHP/template/JavaScript code is incorporated: this independent C# adapter adapts this repository's Lainchan example. NPFchan LICENSE.md grants permissive use conditional on retaining copyright/permission notices for copied substantial portions; future upstream code imports require their own notices review.

Synthetic fixtures `wizchan-*.json` and `WizchanPluginTests` cover request/DTO/HTTP boundaries; they are not live captures. Provider-specific partial semantics retain saved bytes under the host merge contract. Parser tests, SDK-free compiler/worker checks, production download/archive/offline restart and package delivery require separate evidence for these exact plugin bytes. No live original-media, complete board inventory, native platform or full release acceptance is claimed by metadata research.

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

API90011 bytes and MD5 differed from served89933 bytes and MD5; identity/no-auto-decompression returned identical served bytes.

Upload size/hash can survive optional image transformation; actual deployed revision/configuration and exact object cause remain unproven.
The provider's deployed historical version/settings are not bound to this review;
the API values are not universally reclassified as upload metadata. See
`Evidence/integrity-research-20261024.json` for source paths, revision/body hashes
and exact scope. No per-format size adjustment, checksum removal, or hash inferred
from an original basename is applied.
