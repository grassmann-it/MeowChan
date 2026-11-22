# Booru source contract (P2, release 1.2.0)

Status 2026-11-22, branch `p2-multi-booru`. Design and host step for several booru
post-search sources side by side. Decisions marked *open* are proposals until Tom
confirms them; nothing here changes the Danbooru behaviour of 1.1.x.

## Starting point

- `BooruHostHttp` accepted metadata requests only for the source id `danbooru`, with
  a hard-coded path and parameter allowlist. Every other post-search source failed
  closed before the transport.
- `BooruSearchService` validated queries and post ids only for Danbooru; the
  desktop plugin session composed only the Danbooru source.
- The core catalog, original queue, watcher and UI are Danbooru-only
  (`DanbooruPostReference.ProviderId` checks in `BooruCatalog`, `BooruOriginalQueue`,
  `BooruClaimMapper`, `BooruMediaProjection`).
- The SDK has no secret setting kind; official plugins must not declare
  secret-like settings (`NoSecretLikeSettingsTests`).

## Contract: `SourceDescription.BooruProfile`

A post-search source declares one `BooruSourceProfile` (SDK 1.1.0, additive,
init-only, omitted from JSON while null, so existing declarations keep their exact
serialized form and fingerprint):

| Field | Meaning | Host rule |
| --- | --- | --- |
| Source id | the existing `SourceDescription.SourceId` | one host state (pacing, cooldown) per source id |
| `Endpoints` | exactly one `Search` and one `Post` endpoint: exact absolute path (a post endpoint may name `{post}` once) and its declared query parameters | a request must match one endpoint; undeclared, duplicate or missing required parameters are refused |
| Parameter kinds | `Fixed` (exact value), `TagQuery`, `PageNumber` (1-based), `PageIndex` (0-based), `PageOrBeforeCursor` (`n` or `b<id>`), `Limit`, `PostId` | every value is checked against its kind before the request is sent |
| `Query` (tag syntax) | `MaximumTagTerms` (1–8), the source's `rating:` values, whether `-tag` exclusions are allowed, optional cursor term prefix (for example `id:<`) | literal tags only (letters, digits, marks, `_ - .`), at most one rating term, no other meta terms, no boolean or grouped expressions; the cursor term is accepted only in the outgoing request, never from the user |
| `TagCategories` | categories the provider reports (empty = none, e.g. Gelbooru lists) | informational for the UI; mapping stays in the plugin |
| `Ratings` | provider ratings the source uses | informational for filters |
| `MaximumPage`, `MaximumLimit` | provider paging limits | the host ceilings (5 pages of 20) still apply; a profile can only lower them |
| `Authentication` | `None` only | credentials are not part of this SDK version (see below) |
| `MinimumRequestSpacingMilliseconds` | provider's requested spacing between request starts | never below the host floor of 1 s; applied per source |

`SdkValidation.ValidateBooruProfile(source)` checks a profile on its own;
description validation checks every declared profile. A decimal-ordered source
must declare how a before-id cursor is sent (`PageOrBeforeCursor` or a cursor
prefix), and not both.

### Security properties

- The profile never widens access: the metadata host must still be one of the
  source's declared and consented `HttpHosts`; the profile only narrows paths and
  parameters on those hosts. Scheme, port, user info, fragments, redirects,
  `Authorization`/`Cookie` headers and the 20-send/120-second job budget are
  unchanged.
- A post-search source without a profile, or with an invalid one, has no
  endpoint at all (fail closed), as before.
- A changed profile changes the source's serialized declaration, so bundled
  candidate migration treats it like any other declaration change (no silent
  narrowing shortcut); consent review is unchanged.
- **Danbooru keeps its host-owned profile.** For the source id `danbooru` the host
  always enforces its built-in profile (`BooruEndpointPolicy.Danbooru`) and its
  own `DanbooruTagQuery` syntax; a declared Danbooru profile is ignored. This keeps
  1.1.x behaviour byte-for-byte and needs no change of the Danbooru plugin
  declaration (and therefore no re-review for users).

## Host implementation (this branch)

- `MeowChan.PluginSdk/BooruSourceProfile.cs`, `SourceDescription.BooruProfile`,
  `SdkValidation.ValidateBooruProfile` (new public API in `PublicAPI.Unshipped.txt`).
- `MeowChan.PluginHost/Sources/Booru/BooruEndpointPolicy.cs`: profile resolution,
  endpoint and parameter checks, generic tag-syntax check, per-source spacing.
- `BooruHostHttp`: allowlist and pacing from the resolved profile; per-source
  cooldown and send gate as before.
- `BooruSearchService`: per-source query normalization, page and limit ceilings
  from the profile, canonical decimal post ids and cursors for decimal sources,
  `capability` failure for a source without a usable profile.
- Desktop `PluginSession`: every post-search source of every active plugin is
  composed (`BooruSources`); each plugin's sources share one booru host services
  instance (one job registry, one transport; pacing and cooldown per source).
  Only Danbooru has the desktop composition (catalog, watcher, pages); `Booru`
  still returns the Danbooru binding.

## Not in this step (follow-up P2b)

- Core catalog, original queue, watcher, gallery projection and claim mapper are
  still Danbooru-only. Other sources therefore run host-side (search and post read
  through their worker) but have no page, catalog storage or downloads yet.
- Source picker in the Booru page, per-source search tabs, per-source capability
  matrix rows (`source-capabilities.md`) once the UI exists.
- Shared pacing for sibling sources of one provider (e621/e926) is not modelled;
  each source id is paced on its own.

## Credentials (open decision for Tom)

The SDK has no secret setting kind and official plugins must not declare
secret-like settings. Gelbooru-engine sites accept `user_id` and `api_key` only as
query parameters. Options:

1. **Public only (current state, proposed for 1.2.0):** `Authentication = None`;
   sources that need an account stay unavailable. Safebooru, e621 and e926 work
   anonymously; Gelbooru works only if its public API answers without a key
   (to be verified live).
2. **Host-owned credential, never plugin-visible:** a new SDK access mode such as
   `QueryApiKey(userParameter, keyParameter)`; the user enters the values in a host
   dialog, the host stores them in the OS credential store (Secret Service /
   Keychain / Credential Manager, no plaintext file, not in profiles, exports or
   diagnostics), appends them itself after the allowlist check and strips them from
   logs, failure messages and history. The plugin never sees them.
3. **Plugin setting (Gelbooru branch as written):** rejected by the current rule;
   values would be ordinary plaintext settings visible to plugin code.

Questions: is option 2 wanted for 1.2.0 or later; which OS store and fallback when
none is available (refuse, or session-only memory); whether a credential may also
lift provider limits (more pages) or only enable access.
