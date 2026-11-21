# e621 / e926 fixtures

Synthetic, hand-written files for the offline e621 plugin tests. They contain no
copied real post data: ids, tags, hashes and addresses are invented (md5 values are
simple patterns, "sources" use the reserved `.invalid` domain, media addresses use the
real CDN host name only because the plugin accepts nothing else). No test or fixture
contacts a booru site.

They mirror only the public field names of e621's `posts.json` / `posts/<id>.json`
answers (`{"posts":[...]}` and `{"post":{...}}`): `id`, `created_at`, `rating`,
`score.total`, `fav_count`, `tags.{general,artist,copyright,character,species,meta,lore,invalid}`,
`sources`, `flags.deleted`, `file.{url,ext,size,md5,width,height}`, `sample.{has,url}`
and `preview.url`. The API shape was written from the public API description and is
**not yet pinned against a live response**.

| File | Purpose |
| --- | --- |
| `search-normal.json` | three posts, highest id first; one without a sample |
| `search-empty.json` | empty page |
| `search-cursor.json` | two posts below a `page=b1000` cursor |
| `search-unordered.json` | ascending ids, breaking the declared descending order |
| `search-e926.json` | two posts for the e926 source |
| `post-normal.json` | complete single post (all seven tag lists) |
| `post-restricted.json` | `file.url`, `sample.url`, `preview.url` null (login/blacklist restricted) |
| `post-deleted.json` | `flags.deleted` true |
| `post-non-cdn-url.json` | original address outside the declared media host |
| `post-bad-md5.json` | malformed md5 |
| `post-unknown-fields.json` | unknown fields and tag lists are ignored |
| `post-wrong-shape.json` | single post answered as an array |
| `error-envelope.json` | `{"success":false}` error body |
| `malformed-json.json` / `html-challenge.html` | truncated JSON / HTML challenge page |
