# MeowChan.PluginSdk

Public .NET 10 source and image-filter contracts for MeowChan. The SDK is not
stable in 1.x; minor releases may change its public API. The SDK version is
independent of the product and the private worker protocol. This first contract
requires the exact declared SDK version (`1.0.0`). Source-build fingerprints
must include the SDK and reference hashes.

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
