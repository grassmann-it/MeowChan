# ZZZchan source plugin

`meowchan.source.zzzchan` supplies source `zzzchan`. Its SDK-only entry point is
`MeowChan.Plugins.Examples.Zzzchan.ZzzchanPlugin`. The complete `Zzzchan.cs` can be
compiled with the delivered plugin compiler without the .NET SDK or a helper assembly.

- Board: `https://zzzchan.xyz/{board}/` (also accepts the observed `/{board}/index.html` route)
- Thread: `https://zzzchan.xyz/{board}/thread/{thread}.html`
- Metadata: `/{board}/catalog.json` and `/{board}/thread/{thread}.json`
- Originals: `https://zzzchan.xyz/file/{filename}`

The sole declared transport host is `zzzchan.xyz`, for metadata and original
media. There are no wildcard/old-domain grants and no automatic board discovery.
The source advertises thread reading, board catalogs, post capture and original
media, without previews. The host owns all downloads, retries, storage, integrity
checks and gallery suitability.

The thread opener lives at the JSON root; `replies[]` contains replies. Public
`postId` is used instead of Mongo `_id`. Each attachment has a post/slot identity,
so reusing a file in a second post retains both associations. Original filename,
MIME type, exact byte size and hexadecimal provider SHA-256 remain provider
metadata; `phash` is perceptual and never an integrity substitute. `message` is
inert HTML; only the `nomarkup` fallback is plain text. Missing fields remain
unobserved, while explicitly supplied empty file lists remain explicit.

Catalog omission, `locked`, `cyclic` and reply counts do not end/archive a thread.
An exact metadata-thread 404 produces NotFound; other HTTP errors remain failures
or host deferrals. HTTP 304 requires the caller's accepted HTTP baseline. Response
redirects to another metadata endpoint are rejected. Malformed/over-budget bodies
fail rather than silently truncating posts or files. This source does not infer
file/post deletions from absence or offer posting, login or archive fallbacks.

## Evidence and acceptance limits

Mappings were independently checked by bounded read-only JSON/HTML requests on
2026-10-25; compact request/hash receipts are in `Evidence/metadata-probes.json`.
The original NW-00 sample confirmed a SHA-256-matching PNG. A second-board
`/a/` JPEG follow-up returned 24,210 bytes and matched its provider SHA-256.
These direct HTTP probes are schema/route/media observations, not production PluginHost, download queue,
archive, offline-restart, package or platform acceptance. Regression fixtures in
`tests/MeowChan.ExamplePlugins.Tests/Fixtures/zzzchan-*.json` are synthetic,
including malformed, partial and error cases not observed live.

Activation defaults/Experimental classification belong to the common NW-07
integration decision. This adapter grants no integrity exception. Productive
source/worker/media/reader/offline and shipped-package checks remain separate.
