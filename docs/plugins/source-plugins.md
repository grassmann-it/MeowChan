# Write an SDK-only source

Start with [Lainchan](../../plugins/Examples/Lainchan/Lainchan.cs) or
[Kohlchan](../../plugins/Examples/Kohlchan/Kohlchan.cs). Their adjacent READMEs
record the provider evidence and unsupported assumptions. The supplied independent
[Wizchan](../../plugins/Examples/Wizchan/Wizchan.cs) and
[Endchan](../../plugins/Examples/Endchan/Endchan.cs) examples are also complete
SDK-only single files. Consult their
[Wizchan evidence](../../plugins/Examples/Wizchan/README.md) and
[Endchan evidence](../../plugins/Examples/Endchan/README.md) for exact supported
hosts, routes and limitations. API-family similarity is not provider evidence.
For a smaller synthetic example, use the [package consumer's source](../../samples/MeowChan.PluginSdk.Consumer/SyntheticPlugin.cs).

## Entry and description

Implement `IPlugin.Describe()` and `ISourcePlugin.ReadThreadAsync` /
`ReadCatalogAsync`. A drop-in assembly supplies exactly one public, nonabstract
`IPlugin` implementation with a public parameterless constructor. One entry may
implement both source and filter capabilities; describe only operations that it
actually supplies.

`Describe()` returns passive `PluginDescription`, `SourceDescription`, settings
schemas and information entries. It performs no site requests or host mutations.
Declare the exact `SdkContract.Version`, a plugin version, stable IDs, base URI,
capabilities, URL routes and HTTP hosts. A successful description means that the
contract is understood; it says nothing about current website availability.

| Identity | Purpose |
| --- | --- |
| `PluginId` | The installed plugin family across builds; not a filename |
| `SourceId` | The provider's stable source identity, used by saved resources |
| `SourceBoardId` / `SourceThreadId` | Source + board, or source + board + thread |
| `SourceMediaId` | Thread + stable media ID + variant; independent of download address |
| `PostId` and attachment `Position` | Post identity and ordered attachment slot within that post |
| Execution/configuration revisions | Host-owned exact code/resources and settings selections |

Do not change source IDs to reflect display names, language, plugin versions or
new media hosts. For the existing sources, preserve `4chan` and `8kun`. New source
IDs must be distinct. SDK identifiers are bounded; URLs, counts, hashes and
cross-resource relationships also need validation. An ID alone is not a local
filesystem path.

## Declarative URL routing

`UrlRoute` declares an exact DNS host and a path template with `{board}` and, for
thread routes, `{thread}`. A placeholder occupies one nonempty escaped segment;
a fixed prefix/suffix is permitted. Declare exactly one canonical route per
supported resource kind, then any evidenced aliases. Return the canonical thread
URL corresponding to the DTO's identity.

`AllowTrailingSlash`, `AllowTitleSegment` and `LowercaseBoardId` are explicit
options. Query/fragment data does not create a different resource. Board case is
preserved unless lowercasing is declared. Identifier constraints and a source's
post-order declaration are separate contracts. Literal route text follows
`System.Uri` normalization, including encoded unreserved characters. Whole `.` or `..` substituted path segments are invalid. Escape data
once with `Uri.EscapeDataString`; never splice untrusted IDs into path syntax.

`BoardIdentifierKind` and `ThreadIdentifierKind` are optional init properties on
`UrlRoute`; both default to `RouteIdentifierKind.PortableSegment`. Set them through
an object initializer, without changing the route constructor arguments.

| Identifier kind | Accepted extracted ID |
| --- | --- |
| `PortableSegment` | Existing opaque identity semantics, escaped as path data; no new alphabet restriction |
| `AsciiWord` | One or more ASCII letters, digits or underscores |
| `Decimal` | One or more ASCII digits 0–9, without integer conversion |

Constraints apply to the extracted identity, excluding a template's fixed prefix
or suffix. They do not authorize dot-segment traversal or change declared board
case normalization. The official source routes use AsciiWord boards and Decimal
thread IDs; new sources should declare only constraints their own evidence
supports. Decimal routing does not imply a fixed-width integer or strip leading
zeros.

The host uses this data locally. There is no `CanHandle` plugin delegate, regular
expression or synchronous worker call while admitting a URL. Test aliases,
Unicode, reserved characters, trailing slashes, malformed paths and cross-source
conflicts against the actual [router](../../MeowChan.PluginHost/Sources/PluginSourceRouter.cs)
and [SDK route contract](../../MeowChan.PluginSdk/Routing.cs). Do not invent an
alias merely because another engine accepts it.

## Metadata through HostHttp

Use `context.Http.SendAsync(new HostHttpRequest(...), cancellationToken)`.
`IHostHttp` accepts GET/HEAD and declared metadata destinations. Declare exact
hosts with `HttpHostPurpose.Metadata`, `OriginalMedia` and/or `PreviewMedia`
according to their actual purpose. A declaration does not authorize arbitrary
redirect destinations or mean that every host offers all purposes.

The allowed request headers are `Accept`, `If-None-Match` and
`If-Modified-Since`; `SourceValidators` can carry the conditional headers. The host
owns User-Agent, transport, redirect checks, shared pacing and retry budgets.
Return `HostHttpDeferred` as a source Deferred with its actual due time, and
preserve a host failure as failure. Do not add another HTTP client or retry loop.
A provider wait is not an empty catalog or an unchanged thread.

`HostHttpResponse` supplies final URI, status, selected headers, validators and a
bounded, decompressed body. HEAD/304 have no body. The current metadata limit is
8 MiB; over-limit data is rejected instead of silently truncated. Check status and
payload shape before parsing fields. Check that a supposed terminal response
belongs to the intended thread endpoint, including after redirects.

Original files are metadata in `SourceAttachment`; the host's media queue fetches
them. A source parser never chooses local target paths, writes archive data,
removes watches or downloads original bytes itself.

## Source result semantics

| Result | Required interpretation |
| --- | --- |
| `ThreadSnapshot` | Valid thread identity, canonical URL, posts and available validators; `Active` or `Archived` |
| Archived snapshot | Includes the final available posts/files before source completion; pending media can still finish |
| `ThreadUnchanged` | An accepted prior snapshot and validator/revision relationship; preserve its content |
| `ThreadNotFound` | Explicit provider-verified absence; may contain HTTP404/410 only when that endpoint's semantics were established; no invented posts |
| `ThreadDeferred` / `CatalogDeferred` | Actual retry time and reason; preserve prior state |
| `ThreadFailure` / `CatalogFailure` | Bounded diagnostic, optional HTTP status/retry time; not a terminal source signal |
| `CatalogSnapshot` | Board identity, canonical thread references, available metadata and honest completeness/pagination |
| `CatalogUnchanged` | Accepted catalog baseline; not an empty discovery result |

Do not map 403, timeouts, worker crashes, malformed JSON, metadata over-limit or
HTTP206 to thread end or a successful partial response. A deleted media URL is
not a deleted thread. Catalog omission never ends a thread. `locked`, `cyclic` or
similar flags require provider evidence before any terminal interpretation.

New validators accompany only successfully parsed, validated snapshots. A 304
without an accepted baseline is a failure. Do not persist a validator from a
failed response and then accept a later 304 against data never captured.

## Posts, partial observations and attachments

Use `SourceFieldPresence` for every supplied post field. An absent field preserves
previously captured data; a present empty string or null is distinguishable.
Missing posts are not deletion markers. `SourceThreadSnapshot` has no separate
coverage field: do not equate one successful response with proof of a complete
historical thread. Catalog `IsComplete` describes that observation only.

Declare `PostOrderKind.StableOrdinal` when your source can supply a stable,
nonnegative order hint across partial responses. Declare `DecimalPostId` for
arbitrary-length decimal IDs; do not sort them lexically or parse them into a
fixed-width integer. The index within the latest partial response is not stable
post order.

Set `SourceTextFormat.PlainText` or `Html` honestly. HTML remains inert source
content; the rendering host sanitizes it. Preserve authors/times only when
supplied. No markup executes in `Describe` or the settings UI.

Attachments carry stable media identity/variant, per-post position, original URL,
original filename and known extension, size, MIME or hashes. Several attachments
may share an original filename. Positions must be unique within a post, and
media identity must not depend on local directories or display filenames. MD5 is
base64 of 16 bytes; SHA-256 is 64 hexadecimal characters. Unknown metadata remains
unknown rather than inferred from a hash-shaped URL.

Set `Attachments` presence only when that field is supplied. Explicit deletion
markers are observations, not instructions to erase saved bytes. A deleted
attachment without `MediaId` identifies the previously captured position. Keep
unobserved files/posts intact under the host's merge contract.

Use [SdkValidation](../../MeowChan.PluginSdk/Validation.cs) against the expected
request and source description, and [SdkJson](../../MeowChan.PluginSdk/SdkJson.cs)
for discriminated result serialization in tests. JSON syntax alone does not
validate identity, URL ownership, field presence or collection limits. Pass the
cancellation token through every awaited host operation, and propagate cancellation
instead of reporting a false source failure or success.
