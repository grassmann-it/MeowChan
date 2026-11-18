# Danbooru fixtures

These files are synthetic and hand-written for the offline Danbooru plugin
tests. They contain no copied real post data: ids, tags, hashes and addresses are
invented (the md5 values are simple patterns, the "source" values use the
reserved `.invalid` top-level domain). No test or fixture contacts a booru site.

The one exception is `search-live-shape.json` (LiveResponseShape): three
general-rated posts copied unchanged from a public `posts.json?tags=blue_eyes`
response of 16 Nov 2026, kept to pin Danbooru's live field shape (nested
`media_asset`, offset timestamps, every current field). It belongs to the
"plugin-error" report of that day, whose cause was not the parser but a plugin
binary built against an older MeowChan.PluginSdk (now reported as `sdk-mismatch`).

They mirror only the public field names of Danbooru's JSON post representation
that the plugin reads: `id`, `created_at`, `rating`, `score`, `fav_count`,
`tag_string`, `tag_string_general`, `tag_string_artist`, `tag_string_copyright`,
`tag_string_character`, `tag_string_meta`, `source`, `is_deleted`, `is_banned`,
`file_url`, `large_file_url`, `preview_file_url`, `file_ext`, `file_size`,
`image_width`, `image_height` and `md5`. Other field names in
`post-unknown-fields.json` exist to prove that unknown fields are ignored.

| File | Purpose |
| --- | --- |
| `search-normal.json` | three posts, newest (highest id) first like Danbooru's default order: tag_string fallback, sample equal to original, with sample |
| `search-empty.json` | empty result page |
| `search-cursor.json` | a full page of two posts below a `page=b1000` cursor, descending ids |
| `search-unordered.json` | ascending ids, which break the declared descending order |
| `post-normal.json` | complete single post |
| `post-restricted.json` | no `file_url`, `large_file_url` or `md5` |
| `post-deleted-banned.json` | `is_deleted` and `is_banned` true |
| `post-null-fields.json` | null rating/score/source and empty strings |
| `post-unknown-fields.json` | extra scalar, object and array fields |
| `post-non-cdn-url.json` | media addresses outside `https://cdn.donmai.us/` |
| `post-bad-md5.json` | invalid md5 hex |
| `post-missing-id.json` | one post without an id inside a search array |
| `html-challenge.html` | an HTML page instead of JSON |
| `malformed-json.json` | truncated JSON |
| `wrong-shape.json` | an object where a search array is expected |
| `too-many-posts.json` | four posts (descending ids) for a request limit of three |
| `search-live-shape.json` | three real general-rated posts of a live search response (see above) |
