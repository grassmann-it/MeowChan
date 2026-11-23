# 7chan source adapter

`MeowChan.Plugin.SevenChan` is a bundled `MeowChan · experimental` SDK-only
adapter for the observed Kusaba/KusabaX-style HTML contract at `7chan.org`.
It reads one board catalog page and public thread HTML. The adapter never posts,
logs in, accepts cookies, downloads media itself, or treats a mirror as a silent
fallback.

## Bounded contract

- Board catalog: `/{board}/catalog.html`.
- Thread: `/{board}/res/{thread}.html`.
- Canonical source URLs are HTTPS on the exact host `7chan.org`; `http://` and
  `www.` observations are redirect evidence only and are not extra hosts.
- Catalog entries use the observed `a.cattile` and `catsubject`/`catteaser`/
  `catreplies`/`catimages` elements. Missing catalog structure fails rather
  than becoming an empty successful catalog.
- Posts use numeric `div.post` identities, `postername`, `subject`, `p.message`
  and same-host original anchors below `/{board}/src/`. Thumbnail paths are not
  promoted to originals. Post order is the observed numeric identity order.
- Dates are intentionally not normalized: the live time-zone/format contract
  was not proven. Missing dates remain missing.
- Catalog completeness is `false`; omission is never thread deletion. Only an
  exact 404 from the requested thread endpoint becomes `ThreadNotFound`.

The parser is conservative and bounded by the SDK metadata, collection and
identifier limits. A foreign/unsafe original path, duplicate post identity,
wrong opener, redirect to another endpoint, malformed HTML shape or non-200
response fails without committing a validator. HTTP 403/challenge, 429, 5xx and
partial responses remain non-terminal failures/deferred host results.

## Evidence and fixtures

`Fixtures/` contains synthetic examples for catalogs, single- and multiple-media
threads, redirects, 404s and media `HEAD` outcomes.
