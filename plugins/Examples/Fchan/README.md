# Fchan source plugin

`meowchan.source.fchan` is a bundled `MeowChan · experimental` SDK-only Wakaba
HTML adapter for the observed HTTP-only host `fchan.us`. It reads board pages and
thread pages, captures bounded post text/author/subject and exposes only original
media links found in post HTML.

## Bounded contract

- Board pages: `/{board}/index.html` for page 0 and `/{board}/{page}.html` for
  later numbered pages. There is no `catalog.html` or JSON catalog fallback.
- Threads: `/{board}/res/{thread}.html`.
- Body decoding is fixed to ISO-8859-1 (`Encoding.Latin1`); the adapter does not
  infer uncertain date/time fields.
- Original URLs must be same-host HTTP links under global `/src/`; thumbnail
  links under `/{board}/thumb/` are never promoted to originals.
- A disclaimer/interstitial without supported post markup becomes
  `user_action_required`; no `disclaimer=seen` cookie or acceptance state is set.

The HTML parser is intentionally conservative and bounded: it only accepts the
observed `thread`/`reply` IDs, `postername`/`subject` spans, blockquote bodies and
same-host `/src/` anchors. Missing fields remain missing, board catalog output is
incomplete by design, and unsupported/legacy nested markup fails instead of being
silently reinterpreted. The source never upgrades HTTP to HTTPS, follows mirrors,
posts or imports credentials.

## Evidence and limits

`Fixtures/` contains synthetic Latin-1/HTML examples, including frameset,
board, thread, missing catalog, HTTP-only and media cases.
