# MeowChan.PluginSdk

Public .NET 10 source and image-filter contracts for MeowChan. The SDK is not
stable in 1.x; minor releases may change its public API. The SDK version is
independent of the product and the private worker protocol. The current SDK
version is `1.1.0`; the host accepts exactly the declared versions `1.0.0` and
`1.1.0`, and no other cross-version compatibility is promised. 1.1.0 adds booru
post search (below) and `PluginDescription.DeclarationStatus`; both are
additive, so a 1.0.0 plugin keeps working unchanged. A `Blocked` declaration
is metadata only and is accepted with either SDK version. Source-build fingerprints must include the SDK and
reference hashes.

A drop-in assembly has exactly one public, concrete `IPlugin` implementation
with a public parameterless constructor. `Describe()` returns passive data and
does not fetch sites or mutate host state. `ISourcePlugin` and
`IImageFilterPlugin` are independent capabilities. `IPluginSettingsValidator`
optionally supplies domain errors after the host's simple schema validation.

`PluginCallContext` supplies immutable active configuration and host-service
interfaces. Host HTTP is bounded metadata GET/HEAD with declared destinations,
redirect validation, allowed headers and shared pacing. The host owns media
acquisition, target paths, capture/merge, watches, retries, storage and grants.
Deferred results carry actual due times and do not mean an empty snapshot.

Research-only source bundles may use `PluginDeclarationStatus.Blocked` when a
source is intentionally shipped as metadata without a verified executable
route. Blocked declarations must contain neither sources nor filters; they are
accepted for startup and presentation but never create a host adapter. This is
not a way to bypass capability validation or to ship a guessed endpoint.

Source IDs, plugin IDs, resource IDs and execution/configuration revisions are
separate values. Routes use exact hosts and `{board}`/`{thread}` path segments;
aliases, trailing slash, board case and optional inert title segments are
explicit. Returned thread URLs must be canonical for the supplied identity.
No provider callback, regex or synchronous IPC is needed for URL ownership.
Literal route text follows `System.Uri` normalization, including percent-encoded
unreserved characters. `AllowTrailingSlash` accepts both slash forms, even when
the template ends in `/`; an optional title goes before that trailing slash.
An identity cannot form a whole `.` or `..` path segment after substitution.
Dot identities remain usable with a literal prefix/suffix that preserves the
segment, and other opaque identity characters are escaped as data.

Posts use explicit field-presence flags: absence preserves previously captured
fields; a present empty/null value is distinguishable. Absent posts and catalog
entries never imply deletion or source end. Attachments carry stable identity,
variant and per-post position, support multiple files, and contain no paths.
A deletion marker without media identity refers to the previously captured
position. `StableOrdinal` sources keep their post order stable across partial
snapshots; `DecimalPostId` orders arbitrary-length decimal IDs without integer
conversion. Plain text and HTML are explicit formats; rendered HTML remains
subject to host sanitization.

Thread results distinguish `Snapshot`, `Unchanged`, `NotFound`, `Deferred` and
`Failure`. An archived snapshot contains the final available posts; absence can
have no snapshot. Unchanged requires an accepted baseline. 403, malformed JSON,
timeout and worker failure are not thread-end signals.

## Booru post search (SDK 1.1.0)

`IBooruSourcePlugin` searches booru posts and reads one post. A source declares
`SourceCapabilities.PostSearch` with SDK `1.1.0`, at least one `Metadata` host
and no board/thread routes. It is never combined with `ThreadRead`,
`BoardCatalog` or `PostCapture`; booru posts are their own objects and never
fake boards or threads.

- Identity: `BooruPostId(SourceId, PostId)` is opaque and provider-defined; the
  same `PostId` on two sources stays two posts. A source that declares
  `PostOrderKind.DecimalPostId` promises more (see Cursor).
- Query: `BooruSearchRequest.Query` is the already host-validated provider query
  and never contains private tags. `Page` is 1-based (1..1000), `Limit` 1..100.
  A page echoes source, query and page, holds at most `Limit` posts with unique
  ids of the requested source, and `IsEnd` only says the provider reported no
  further page, not that the site was fully observed. An empty page with
  `IsEnd = false` means the provider listed nothing here without reporting the
  end; it carries no cursor.
- Order and cursor: `SourceDescription.PostOrder` is the switch. With
  `StableOrdinal` (the default) post-search ids stay opaque, pages promise no
  order and the host never sends a cursor. `DecimalPostId` declares that every
  post id is a positive decimal without leading zeros, that a larger id was
  assigned later, that every page lists strictly descending ids and that the
  source serves cursors (this declaration is the only switch for the cursor; a
  source with decimal ids that cannot serve cursors keeps `StableOrdinal`):
  `BooruSearchRequest.BeforePostId` asks for the posts
  of the query with a lower id, newest first. A cursor request uses `Page = 1`
  (the cursor alone selects the position); the cursor is a post id the host
  received from the same source, and a cursor page lists only ids below it.
  `BooruSearchPage.NextBeforePostId` is the lowest id of the page: a
  decimal-ordered page names it whenever it is nonempty and not final, and a
  final or empty page has none. A source without decimal order neither
  receives nor offers a cursor. Validation codes: `cursor_unsupported`,
  `invalid_cursor`, `ambiguous_page`, `cursor_not_advanced`, `missing_cursor`,
  `end_with_cursor`, `invalid_order`. A cursor never appears in an issue text.
  `DecimalPostId` does not promise dense ids, creation-time order or that an
  older post never starts matching a query later.
- Claims: ratings, scores, counts, tags, sizes, dimensions and MD5 (base64 of 16
  bytes) are provider claims. `Rating` is the provider's, never the user's.
  `SourceClaim` is the provider's free-text "source" and stays inert text.
  Tags keep the exact provider spelling (at most 4096 per post).
- Media roles: `Original`, `Sample` and `Preview` are never interchangeable and
  each appears at most once. A null `Url` means not offered or restricted. URLs
  are absolute https on a declared host: `OriginalMedia` for `Original`,
  `PreviewMedia` for `Sample` and `Preview`.
- Presence: `BooruFieldPresence` separates an absent field from a null or empty
  value; a supplied value without its flag is invalid.
- Results: `Snapshot`, `Deferred` and `Failure` (plus `NotFound` for a single
  post) are explicit; none implies an empty page. `NotFound` means a verified
  absence; its optional `HttpStatusCode` is 404 or 410 when given, and a plugin
  reports any status it cannot verify as absence as a `Failure`.
- Limits: metadata goes through `IHostHttp` only. Plugins add no retry loops and
  download no media; the host owns pacing, retries and the media queue.
- Use in MeowChan 1.1.0: the app runs searches, one-shot posts and watchers
  only for the Danbooru source (source id `danbooru`, shipped as an official
  plugin). Another `PostSearch` source is validated like any plugin, but the app
  does not use it for search or watchers in this version.
- Evolution: a further positional record parameter or a new interface member
  is a binary or implementer break and needs a new SDK version with its own
  review; additive fields in 1.x come as init properties. `BooruFieldPresence.All`
  is not extended; set presence flags explicitly.

Filters receive the staged original's actual size/SHA-256, media/candidate
identity, revisions and an invocation-bound asset token. `IImageAssetReader`
returns at most 64 KiB per call. Match, NoMatch and technical omissions are
different outcomes; only the host can verify current revisions/grants and
commit those exact bytes. These contracts provide no filesystem or network
sandbox for trusted local C# code.

Use `SdkJson` for explicit result discriminators, bounded documents, required
constructor fields and optional future fields. Use `SdkValidation` against the
expected request/description before accepting any result. JSON parsing alone
does not validate identity, URL relationships, grants or resource limits.
Conflict validation reports duplicate source/plugin identities and overlapping
URL ownership; the host must resolve candidates explicitly.

The package has no Core, Avalonia, Roslyn or process-transport dependency. The
package-only example is in `samples/MeowChan.PluginSdk.Consumer`.

The [plugin author guide](../docs/plugins/README.md) covers installation, source and
filter implementation, settings/grants, optional hosting and validation. It links
the maintained Lainchan, Kohlchan and ImageDimensionsFilter source files rather
than duplicate documentation examples.

## Security

Credentials belong to the host. A plugin must not declare API keys, tokens,
passwords, cookies or session values as settings: setting values are stored as
plain text in the profile and are visible to the plugin, so they are not a
place for secrets. The official plugins are checked for this by
`tests/MeowChan.OfficialPlugins.Tests/NoSecretLikeSettingsTests.cs`.
