# SushiChan source plugin

`meowchan.source.sushichan` supplies source `sushichan`. Its SDK-only entry point
is `MeowChan.Plugins.Examples.SushiChan.SushiChanPlugin`. `SushiChan.cs` is the
complete editable source, without a shared parsing assembly or extra dependencies.

- Board: `https://sushigirl.cafe/{board}/`
- Thread: `https://sushigirl.cafe/{board}/res/{thread}.html`
- Metadata: `/{board}/catalog.json` and `/{board}/res/{thread}.json`
- Originals: `/{board}/src/{tim}{ext}`

`sushigirl.cafe` alone is declared for metadata and original media. The former
`.us` domain is deliberately not treated as an alias. Supported capabilities are
board catalogs, threads, captured posts and original media. Previews, posting,
login, board discovery and archive fallbacks are not advertised.

Catalog pages are flattened; returned pages do not promise exhaustive coverage.
Public decimal `no` identifies posts. The primary attachment uses slot 0 and
`extra_files` retain slots 1 and higher even when the primary is absent. Provider
file tokens are opaque. Attachment identities use post/slot rather than a user
filename, and MD5/byte size remain the original provider expectations. Explicit
`filedeleted` marks slot 0 as deleted without inventing an original URL. Missing
post/file fields do not imply a deletion. HTML remains inert source content.

Embeds do not become downloadable original files. Non-image attachments retain
their metadata instead of disappearing from an allegedly complete observation;
the central host policy decides media eligibility, and this plugin does not
promise gallery support for ZIP, PDF or EPUB. All transfers, checks, retries,
actual byte limits and archive writes remain host-owned.

An exact metadata-thread 404 produces NotFound. HTTP 304 requires an accepted
HTTP baseline; failures, deferrals, redirects to the wrong endpoint, malformed
payloads and collection/body limits retain their typed failure semantics.
`locked`, `cyclical` and catalog omission are not verified end/archive signals.

## Evidence and acceptance limits

NW-00 observed a four-attachment post and an exact-size MD5-matching WebP.
A `/kitchen/` JPEG follow-up returned 22,121 bytes and matched its provider MD5.
Bounded JSON/HTML/media follow-up observations on 2026-10-25 are summarized in
`Evidence/metadata-probes.json`. These are schema/route observations, not a
production PluginHost/queue/archive/offline-restart or package/platform PASS.
The `sushichan-*.json` test fixtures are synthetic. Negative/deletion/partial
regression cases are not claimed as additional live observations.

Activation defaults and Experimental classification are owned by NW-07. This
adapter grants no provider mismatch exception. Delivery, SDK-free worker and
productive media/reader/offline acceptance remain separate integration work.
