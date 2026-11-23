# CapybaraChan source plugin

`meowchan.source.capybarachan` is a bundled `MeowChan · experimental` SDK-only
adapter for the observed LynxChan 2.9.0 JSON contract at `capybarachan.org`.
It supports board catalog reads, numbered board-page reads, thread reads, post
capture and original-media metadata. The host owns redirects, pacing, retries,
media downloads and integrity handling.

## Bounded contract

- Catalog: `/{board}/catalog.json` (JSON array).
- Numbered board page: `/{board}/{page}.json`; request page `0` maps to the
  observed `/1.json` page.
- Thread: `/{board}/res/{thread}.json`.
- Original media: only a provider-supplied `path` below `/.media/` is accepted;
  the adapter never derives a path from a hash, filename or thumbnail.
- `index.json` and the HTML board menu are not used for automatic board discovery;
  empty `topBoards`/`latest*` fields are not treated as an outage.

The adapter preserves explicit missing-vs-empty fields, decimal identities,
provider sizes and MIME/original-name hints, and rejects foreign hosts, unsafe
media paths, malformed JSON, oversized collections, unexpected redirects and
unverified HTTP terminal statuses. `deleted: true` is carried only as an explicit
post/file deletion marker. No login, posting, webring, overboard, preview or
credential/cookie behavior is present.

## Evidence and limits

`Fixtures/` contains synthetic contract examples covering catalog, pagination,
thread shapes, redirects, 404s and media `HEAD` outcomes.
