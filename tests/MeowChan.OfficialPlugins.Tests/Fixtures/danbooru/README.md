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
| `search-variants.json` | eight posts, ids 9008 down to 9001, one per media shape of the browse-to-download pipeline (below) |
| `search-page20-first.json` | a full page of 20 posts, ids 9220 down to 9201, mixed shapes; the next cursor is `9201` |
| `search-page20-second.json` | the full page that follows below that cursor, ids 9200 down to 9181 (no post shared with the first) |

## Browse-to-download fixtures (package B2)

Synthetic as above (md5 values are repeated 8-digit hex patterns). The CDN side of the
same cases, which cannot be expressed as JSON, is in `tests/Shared/BooruCdnFixtures.cs` (linked into `MeowChan.PluginRuntime.Tests`, `MeowChan.DesktopTests` and `MeowChan.DesktopSmoke`; pinned by `BooruCdnFixtureTests` and `BooruBrowseFixturesTests`)
and the UI fakes are in both `BooruUiFakes.cs` copies; see "Fixture map" below for who
uses what. Parsed and pinned by `DanbooruSourceTests.SearchVariantsFixtureCoversEveryMediaShapeOfThePostViewer`
and `SearchPageFixturesAreAFullPageOfTwentyAndAFullFollowingPage`.

Posts of `search-variants.json` (what the plugin yields after parsing):

| Post | Shape | Variants the plugin offers |
| --- | --- | --- |
| 9008 | jpg 3000x2000 with preview, sample and original | Original, Sample (address only), Preview |
| 9007 | png 800x600, `large_file_url` equals `file_url` (Danbooru's fallback for a post without a sample) | Original, Preview. The plugin drops the repeated address, so a viewer sees **no sample** here |
| 9006 | animated gif, no `large_file_url` | Original (`.gif`), Preview |
| 9005 | video (`mp4`, mp4 sample) | Original, Sample (both not images), Preview (jpg still) |
| 9004 | ugoira (`zip` original, `webm` sample) | Original, Sample (both not images), Preview (jpg still) |
| 9003 | `is_deleted` true; no original or sample address, preview kept | Original without URL, Preview |
| 9002 | `is_banned` true; nothing offered | Original without URL |
| 9001 | missing file: no address of any kind, neither flag set | Original without URL |

`search-page20-*.json` repeat four shapes (jpg with sample, png with sample equal to
the original, jpg rated `s` with sample, webp without sample) so a grid of 20 shows
both sample and no-sample tiles. They exist for the 20-hit page and "next page" paths.

### Fixture map

| Case | Where | Used by |
| --- | --- | --- |
| Posts with preview, sample and original; sample equal to original; no sample; video; ugoira; deleted, banned, missing file | `search-variants.json`; in the UI fakes `BooruUiFixtures.Variant(id, BooruFixtureKind)` | B4 (ports, `SampleUrl` projection, state derivation), B6 (grid and badges), B7 (viewer) |
| 20+ hits and a following page | `search-page20-first.json`, `search-page20-second.json`; in the UI fakes `BooruUiHarness.ServeFullPages()` | B4, B6 (grid, paging, bulk bound), B10 (smoke) |
| Tiny decodable PNG, JPEG, WebP, GIF (16x12, under 1 KiB) | `BooruTinyImages` in `tests/Shared/BooruCdnFixtures.cs`; in the UI fakes `BooruUiFixtures.Png/Jpeg/WebP/Sample` | B3 (host signature checks), B7, B10 |
| CDN 429 and 503 with `Retry-After` (preview and sample, delta seconds and HTTP date) | `BooruCdn.RateLimited/RateLimitedUntil/ServiceUnavailable/ServiceUnavailableUntil/ThrottledWithInvalidRetryAfter`; UI side `FakeBooruCdn` (60 s floor fence) | B3 (media fence), B7 and B10 (fence state) |
| Oversized body, with and without `Content-Length` | `BooruCdn.Oversized` (streamed, never materialized; the last byte of the budget is accepted today, one more is refused) | B3 (byte budget per role) |
| Wrong content type, HTML body, truncated image | `BooruCdn.WrongContentType/HtmlLabelledAsImage/Truncated/EmptyBody`. Today's preview path judges the signature, not the content type, so an image body under `text/html` is a decision for B3 | B3 |
| Redirect (3xx with `Location`), to the CDN and to a foreign host | `BooruCdn.Redirect` and `BooruCdn.FollowedRedirect` (a transport that followed it); the host must refuse both | B3 |
| Missing file (404), gone (410) | `BooruCdn.Status` | B3, B7 |
| Slow or held responses | `BooruCdn.Held` with `BooruCdn.NewGate()` (released by the test); `BooruCdnScript.MaxInFlight` counts concurrency | B3 (concurrency), B7 (stale navigation) |
| `FakeSamplePort`, `FakeBooruCdn` | `tests/MeowChan.DesktopTests/BooruUiFakes.cs`, `tests/MeowChan.DesktopSmoke/BooruUiFakes.cs` (kept identical) | B4, B6, B7, B10 |
| Composition-level CDN (real host transport over the stub) | `DanbooruStub.Cdn` in `tests/Shared/BooruCompositionFixture.cs` | B10 |

