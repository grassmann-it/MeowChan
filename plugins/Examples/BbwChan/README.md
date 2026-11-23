# BBW-Chan source adapter

`MeowChan.Plugin.BbwChan` is a bundled `MeowChan · experimental` SDK-only
adapter for the observed LynxChan JSON contract at `bbw-chan.link`. It has its
own source identity and host declaration even though its JSON family is shared
with other LynxChan examples.

## Bounded contract

- Catalog: `/{board}/catalog.json`.
- Numbered board page: `/{board}/1.json`, with request page `0` mapped to the
  documented first page and later requests using their positive page number.
- Thread: `/{board}/res/{thread}.json`; the canonical browser URL exposed to
  MeowChan remains `/{board}/res/{thread}.html`.
- Original media must be a same-host path under `/.media/`; thumbnail values are
  not promoted. Stable media IDs are derived from the provider post identity and
  attachment position, not a local path or filename.
- Catalog entries may omit files. Thread JSON is the authority for the files
  present in a thread observation.
- `pageCount` can provide `NextPage`; snapshots remain incomplete because a
  catalog/page observation does not prove that omitted threads ended.
- Thread state is kept `Active`; no archive/completion interpretation is made
  without a proven BBW-Chan field contract. Explicit post/file deletion markers
  are preserved as observations and never erase local bytes.

Only `bbw-chan.link` is configured. The historical `.nl` domain and the current
`.xyz` observation are not aliases or fallback hosts. There is no posting,
login, overboard, webring or preview support. Non-200 statuses, redirects,
malformed JSON, unsafe media paths, invalid identities and over-limit collections
fail or defer through the SDK contract; they never become an empty or terminal
snapshot.

## Evidence and fixtures

`Fixtures/` contains synthetic JSON/HTTP examples for index, catalog,
pagination, thread files, redirects, 404 and media `HEAD` behavior.
