# Settings, image filters and grants

Use the maintained [ImageDimensionsFilter](../../plugins/Examples/ImageDimensionsFilter/ImageDimensionsFilter.cs)
as the filter example and the [synthetic SDK plugin](../../samples/MeowChan.PluginSdk.Consumer/SyntheticPlugin.cs)
for a small plugin-level setting. Neither example needs Core or desktop types.
The separate source/filter integration wave adds [file rules, SHA-256 blocklist,
format/animation and PNG text-key presence filters](local-media-filters.md).
They do not add filter chaining or tags.
OCR/Tesseract is deferred; it is not a shipped 1.0 filter or automatic download.

## Passive settings and information

A `SettingsSchema` has its own version and stable keys. `SettingDefinition`
supports Text, Integer, Boolean, Enum and Uri, with label/help, default, required
status, numeric/length bounds and allowed enum options. Defaults are typed
`SettingValue` objects. Supply only the value payload corresponding to its kind.
An empty schema is valid; do not add settings solely to populate a form.

| Scope | Values | When they apply |
| --- | --- | --- |
| `Plugin` | Runtime settings for the plugin | Next app start |
| `Source` | Settings for a declared source | Next app start |
| `FilterRule` | Parameters for the effective media rule | New decisions after the rule is saved |

`PluginCallContext.Configuration` contains the active configuration revision,
plugin settings and a settings snapshot for every declared source, including an
explicit empty snapshot where appropriate. Filter parameters instead arrive in
`ImageFilterRequest.RuleSettings`. These revisions are not a plugin version or
source identity.

The host checks schema types, bounds and required fields. Implement
`IPluginSettingsValidator.ValidateSettingsAsync` for additional domain rules;
return `FieldValidationError` values with field key, code and useful bounded
message. Pass cancellation through asynchronous validation. Keep validation free
of incidental source acquisition or archive changes.

On schema changes, retain compatible values and apply valid additive defaults.
Incompatible fields require correction while their old values are retained; do
not quietly reset a user's configuration. Active, draft and saved-next-start
values remain distinct. A rollback selects its matching valid configuration.
Settings are not a secret vault: no password/credential contract is provided by
these plain-value schemas.

`InformationEntry` supplies passive Text, Value or List content. Use `AsOf` and
`SourceId` where a displayed observation needs a time or source context. A UI
refresh must not turn into a site request. Do not embed executable UI, HTML
scripts, custom controls or callbacks in descriptions/settings.

## Implement a filter

Implement `IImageFilterPlugin.EvaluateAsync`, describe each `FilterDescription`
with a stable key, required `FilterDataAccess`, rule schema and supported MIME
types. The dimensions example uses plugin ID `meowchan.example.image-dimensions`
and filter key `dimensions`, with `CandidateMetadata | OriginalImage` access.
Its rule fields are minimum width/height, orientation, optional aspect ratio and
ratio tolerance. Ratios use a decimal point.

An `ImageFilterRequest` includes the source/thread/media/post identity, attachment
position, input and media revisions, exact staged original size/SHA-256, effective
rule settings/revision, build/configuration/grant revisions and an asset token.
Treat these as one correlated request. Provider-reported dimensions or size do
not replace the actual staged bytes.

Read bytes only through `context.Images.ReadImageAsync(assetToken, offset, count,
cancellationToken)`. A request reads at most 64 KiB. Validate the returned offset,
length and end condition; a short/truncated response is not a successful image
inspection. The token is bound to this live invocation, plugin, revisions and
grant. It is not a filesystem path, an archive identifier or a credential to keep
for another call. Do not enumerate/read other saved images.

The host first applies cheap capture/metadata rules, then stages an admitted
original through the existing download queue. A text-only capture does not fetch
an image for a filter. A matching result lets the host commit **the same verified
bytes**, subject to current generation, rule and grant checks; the filter never
starts a second original download or writes the media store itself.

| Outcome | Meaning |
| --- | --- |
| `Match` | This rule matched the inspected bytes; storage is still the host's responsibility |
| `NoMatch` | A deliberate rule exclusion |
| `Unavailable` | Required capability/format/plugin dependency cannot evaluate this input |
| `Timeout` | Evaluation did not finish within its budget |
| `Error` | Technical failure, invalid bytes or limit violation |

Technical omissions are distinct from NoMatch and do not count as saved or
successfully complete media. Previously captured posts and stored files remain
available. Ordinary polling does not start endless retries of failed filter
work. Eligible explicit repair/retry follows the host's retained deadlines and
rule decisions.

Current limits include 32 MiB original bytes, 16 Mi pixels and 64-KiB reads. The
stage coordinator admits at most two original checks and accounts for up to
512 MiB of stage plus filter-worker temporary data. A host may choose stricter
limits. Reject oversized/unsupported/unclear data with a reason; never silently
truncate input and return Match. The dimensions example validates JPEG/PNG
headers without decoding pixels, launching a helper or copying the original.

## Give only the intended scope access

A declared filter capability does not grant image access. The host stores grants
by **plugin ID + filter key + exact source ID**, optionally narrowed to a board
and stable rule identity. Required access can include candidate metadata and that
candidate's original image. There is no implicit all-sources grant, archive-write
permission or permission to edit rules.

In Media rules, the **Image filter** controls choose inheritance, no filter or an
active plugin. Use **Grant required access for this scope** for the selected
source/optional board/rule. **Revoke access** removes that scope's access. The UI
separately reports missing access and an unavailable plugin; one does not imply
the other.

Revisions change when access is revoked/regranted. The host rejects stale tokens
and results after revocation, cancellation or relevant rule/build/configuration
changes, and checks again before committing bytes. Existing grants do not expand
to new sources, filters or additional data access introduced by an update.

These are controls over host-provided services. The local C# process still runs
with user permissions; it is not an OS sandbox. For resource-backed helper
processes, follow [resources and lifecycle](hosting-and-resources.md#resources-and-helper-processes).
