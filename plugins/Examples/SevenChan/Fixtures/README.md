# 7chan fixtures

These are synthetic, bounded parser fixtures derived from the observed selector
and route contract. They contain no downloaded user media or personal data.
`redirects.http`, `404.html` and `media-head.http` are negative/metadata
observations, not an availability guarantee.

`thread-unquoted-ids.html` reproduces the structure of a live Kusaba X thread
(unquoted `id=300`, a sticky marker, replies wrapped in `div.reply`, and each
file linked three times: name, download icon and thumbnail). All text, names and
file names are neutral placeholders; no live post content is stored.

`thread-multifile.html` reproduces the multi-file post of a live board whose first
link per file reads `pdf (5.75MB)` (type and size, not a file name) and whose poster
file name is only in the download icon's `download` attribute. File names are
neutral placeholders.
