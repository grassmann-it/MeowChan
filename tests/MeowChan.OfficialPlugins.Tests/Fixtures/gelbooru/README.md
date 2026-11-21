# Gelbooru family fixtures

These files are synthetic and hand-written for the offline Gelbooru/Safebooru
plugin tests. They contain no copied real post data: ids, tags, hashes and
addresses are invented (the md5 values are simple patterns, "source" values use
the reserved `.invalid` top-level domain). No test or fixture contacts a booru
site. They mirror only the public field names of the Gelbooru engine's
`index.php?page=dapi&s=post&q=index&json=1` response shape as recalled from its
public documentation; they have not been compared with a live response.

| File | Purpose |
| --- | --- |
| `search-normal.json` | wrapper with three posts, descending ids: HTML-escaped tags, offset timestamps, sample equal to original, deleted webm without size |
| `search-empty.json` | wrapper without a `post` member |
| `search-cursor.json` | two posts below a cursor |
| `search-unordered.json` | ascending ids |
| `search-capped.json` | provider reports `limit` 2 while three were requested |
| `post-single-object.json` | `post` as one object, numeric strings, off-host and non-https media addresses |
| `safebooru-search.json` | bare array with directory/image/hash and no addresses |
| `safebooru-bad-directory.json` | path traversal in `directory` |
| `html-challenge.html`, `malformed-json.json`, `wrong-shape.json`, `too-many-posts.json`, `post-bad-md5.json` | failure shapes |
