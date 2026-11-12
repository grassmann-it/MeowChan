# Booru test matrix (MeowChan 1.1.0, Danbooru)

Status 12.11.2026, for the working copy of branch `goal/gesamtprogramm-profile-a` at
`3c675786` plus the B8 changes. This lists which **synthetic fixtures** and **stub or
fake tests** cover which acceptance scenario of package B9 in the 1.1.0 Booru work
plan (`docs/architecture/gesamtprogramm-2026-10-31/pakete/1.1.0-booru-arbeitsplan-2026-11-12.md`).
It is an index of what exists. **A row marked *open* has no test yet**; it names the
package that has to add one. A listed test is not an acceptance result.

## Rules for fixtures and stubs

- Every fixture is synthetic and hand-written. No real post, user, tag history or
  credential is copied into the repository. Fixture addresses use invented ids and
  only two kinds of host: the provider's media CDN host (the plugin accepts media
  addresses only there, so the fixtures must use it) and reserved `.invalid` names.
  `BooruHelpDocumentationTests` checks this for the Danbooru fixture folder.
- No test contacts a booru site. HTTP behaviour is produced by in-memory fake
  transports, a fake clock and a loopback server bound to the test process.
- Fixtures for the provider's JSON are in
  `tests/MeowChan.OfficialPlugins.Tests/Fixtures/danbooru/` (see its README for the
  per-file purpose). A live request needs separate approval in the chat and is not
  part of any release criterion.

## Test projects involved

| Project | Booru content |
| --- | --- |
| `tests/MeowChan.PluginSdk.Tests` | `BooruContractTests` (SDK records, validation) |
| `tests/MeowChan.OfficialPlugins.Tests` | `DanbooruSourceTests` with the fixture folder: plugin description, parsing, HTTP status mapping |
| `tests/MeowChan.PluginRuntime.Tests` | `BooruHostHttpTests` (pacing, budget, retry, cooldown, limits), `BooruSearchServiceTests`, `BooruClaimMapperTests`, `BooruRealWorkerTests` (real worker against a loopback stub) |
| `tests/MeowChan.Tests/Booru` | `DanbooruTagQueryTests`, `DanbooruTagQueryBoundaryRegressionTests`, `DanbooruWatcherDomainTests`, `BooruOriginalDownloadTests`, `BooruCatalogStoreTests`; plus `DanbooruQueryAdversarialRegressionTests` |
| `tests/MeowChan.DesktopTests` | `DesktopPluginSessionIntegrationTests` (delivered plugin), `BooruStatusCatalogTests` (texts), `BooruHelpDocumentationTests` (help and fixtures), `BundledLanguagePackCompletenessTests` |
| `tests/test_plugin_packaging.py`, `tests/PluginDeliverySmoke` | Danbooru in the plugin package inventory |

## Scenarios of package B9

| # | Scenario | Covered by (synthetic fixture or stub) | State |
| --- | --- | --- | --- |
| 1 | Search with several valid provider tags, no hidden rating filter | `DanbooruTagQueryTests.TwoLiteralTagsPlusFreeRatingAreAcceptedWithoutInventingAnOrderMetatag`, `DanbooruWatcherDomainTests.CanonicalQueryIsDeterministicAndNeverInjectsRatingOrPrivateTags`, `BooruSearchServiceTests.ValidDanbooruQueryUsesProviderQueryFixedLimitAndHostJobResource`, `BooruRealWorkerTests.DanbooruPluginSearchUsesLoopbackWireAndInvalidQueryDoesNotStartWorkerCall` (exact request on the loopback wire); fixture `search-normal.json` | covered up to the host; no UI test (B6) |
| 2 | Provider rating and source tags separate from own tags | `DanbooruSourceTests.TagsKeepProviderSpellingAndDocumentedCategoryOrder`, `AbsentRatingLeavesTheFlagUnsetAndBadRatingsAreMalformed`, `BooruClaimMapperTests.AbsentFieldsBecomeUnsetCoreValuesAndDoNotInventPrivateMetadata`, `BooruCatalogStoreTests.RepeatedFindingsAndPostsAreIdempotentAndProviderTagsStayInThePostClaim`, `DanbooruTagQueryTests.ParsingOnlyUsesTheExplicitStringAndHasNoPrivateTagInputs`; fixtures `post-normal.json`, `post-null-fields.json` | covered in plugin, mapping and storage; display separation is *open* (B6, B7) |
| 3 | One post link loads exactly one original, or reports a missing permission | `DanbooruSourceTests.PostRequestIsOneExactGet`, `ValidPostIdsAreAcceptedWithoutNormalizing`, `PostRestrictedFixtureKeepsAnOriginalWithoutUrlAndNeverBuildsOne`; `BooruOriginalDownloadTests.SelectsOnlyTheExplicitOriginalAndPreservesIntegrityClaims`, `DoesNotPromoteSampleOrPreviewWhenOriginalIsUnavailable`; `BooruRealWorkerTests.RealWorkerMapsSinglePost404ToNotFound`; fixtures `post-normal.json`, `post-restricted.json` | covered up to the original selection; link parsing (the DanbooruPostReference type has no test of its own yet) and queue admission are *open* (B4) |
| 4 | Same post from a search, a one-shot and two watchers is admitted once | `BooruCatalogStoreTests.RepeatedFindingsAndPostsAreIdempotentAndProviderTagsStayInThePostClaim`, `ClaimsFindingsWatchersAndAssetLinksSurviveAProfileRestart` (several findings, one post) | findings storage covered; queue-level single admission is *open* (B4, B5) |
| 5 | Watcher create, baseline, new posts, pause, check now, cooldown, restart, remove | `DanbooruWatcherDomainTests` (all: `FirstCheckEstablishesFromNowBaselineWithoutHistoricalBackfill`, `NewPostsAreEmittedOnceAndKnownPostProgressIsNumeric`, `FivePageBudgetLeavesAResumableCursorInsteadOfClaimingCompletion`, `PauseResumeAndCancelPreserveTheNextPageWithoutARequest`, `CooldownAndErrorKeepWatcherAndBlockOnlyTheRelevantCheck`, `RestartRecoveryResumesAnInterruptedPartialPageAndDoesNotDuplicatePosts`, `RemoveIsTerminalAndDoesNotEraseKnownLocalIdentity`); `BooruCatalogStoreTests.WatchersRequireCanonicalQueriesAndPersistAllRestartState`, `RemovingWatcherKeepsWatcherClaimsAndLocalPost` | domain and persistence covered; scheduler with a stub server is *open* (B5) |
| 6 | Queue pause, cancel, restart and damaged bytes | none booru-specific. The existing download tests cover the shared queue; `BooruOriginalDownloadTests.RejectsInvalidOriginalTransportAndIntegrityClaims` covers claim validation only | *open* (B4) |
| 7 | Restart without network keeps local booru media, data and gallery readable | `BooruCatalogStoreTests.ClaimsFindingsWatchersAndAssetLinksSurviveAProfileRestart` (storage level) | storage covered; media, gallery and favorites offline are *open* (B7, B9) |
| 8 | Upgrade of an old profile without booru data | `BooruCatalogStoreTests.NewBooruFileIsAnAdditiveOfflineMigrationForAnExistingProfile`, `MalformedAndFutureCatalogsFailClosed`, `OversizedCatalogFailsClosedBeforeDeserialization`, `DuplicatePostFindingAndWatcherIdentitiesFailClosed`, `SuccessfulWritesLeaveNoTemporaryCatalogFiles`; notes in `docs/releases/v1.1.0-booru-data.md` | unit level only; no real 1.0.2 profile run (B9) |
| 9 | Failure matrix, see the next table | see next table | host and plugin covered; UI states *open* (B6) |
| 10 | Packaging, localization, help text, no credentials or private tags in provider requests | `tests/test_plugin_packaging.py`, `tests/PluginDeliverySmoke/sdk_free_worker_probe.py`, `DesktopPluginSessionIntegrationTests` (Danbooru delivered, active, not a chan source), `BooruStatusCatalogTests`, `BundledLanguagePackCompletenessTests`, `BooruHelpDocumentationTests`; `BooruHostHttpTests.BooruRouteUsesExactUserAgentAndRejectsHeadBeforeTransport` and `BooruRealWorkerTests.DanbooruPluginSearchUsesLoopbackWireAndInvalidQueryDoesNotStartWorkerCall` (no `Authorization` or `Cookie` header on the wire); `DanbooruSourceTests.FailureMessagesNeverEchoTheQueryOrTagText` | covered; UI strings added by B6 need the same completeness check at the string freeze |

## Failure matrix of scenario 9

| Case | Covered by | Fixture or stub |
| --- | --- | --- |
| empty result | `DanbooruSourceTests.SearchEmptyFixtureIsAnEndSnapshotNotAFailure` | `search-empty.json` |
| restricted post (no original address) | `PostRestrictedFixtureKeepsAnOriginalWithoutUrlAndNeverBuildsOne` | `post-restricted.json` |
| deleted or banned post | `PostDeletedBannedFixtureCarriesBothFlagsAndValues` | `post-deleted-banned.json` |
| redirect (3xx) | `BooruHostHttpTests.RedirectIsTerminalAndDoesNotFollowLocation`; `DanbooruSourceTests` status table (301) | fake transport |
| 401 and 403 | `DanbooruSourceTests` (`access-denied` for both, HTML body on 403) | fake host response |
| 404 and 410 | `DanbooruSourceTests.PostNotFoundIsOnlyAVerified404`; `BooruRealWorkerTests.RealWorkerMapsSinglePost404ToNotFound` | fake host response, loopback stub |
| 429 and cooldown | `BooruHostHttpTests.TooManyRequestsStartsSourceCooldownAndDoesNotTouchTransportAgain`; `BooruRealWorkerTests.RealWorkerReceivesProvider429AsDeferredAndSecondCallDoesNotHitStub`; `DanbooruSourceTests.RetryAfterHttpDateSetsTheDueTime`, `RetryAfterInThePastIsDueNowAndFarFutureIsCapped`, `HostDeferredPassesThroughUnchanged` | fake transport and clock, loopback stub |
| 502 and 503 | `BooruHostHttpTests.BadGatewayRetriesOnceAfterTwoSeconds`, `SecondFiveOhThreeIsReturnedWithoutAnotherRetry` | fake transport and clock |
| timeout | `BooruHostHttpTests.SlowMetadataBodyIsBoundedByFifteenSecondResponseDeadline`, `TwentyFirstSendAndExpiredJobDoNotReachTransport` | fake transport and clock |
| too large answer | `BooruHostHttpTests.WireAndDecodedLimitsReturnMetadataTooLarge`, `WireLimitAlsoAppliesWhenContentLengthIsUnknown`; `DanbooruSourceTests.TooManyPostsFixtureFailsInsteadOfTruncating` | `too-many-posts.json` |
| not JSON, truncated or wrong shape | `DanbooruSourceTests.HtmlChallengeFixtureIsNotJson`, `MalformedJsonAndWrongShapeFixturesAreMalformed`, `OneBadPostMakesASearchMalformedInsteadOfLosingIt` | `html-challenge.html`, `malformed-json.json`, `wrong-shape.json`, `post-missing-id.json`, `post-bad-md5.json` |
| foreign or invalid media address | `PostNonCdnUrlFixtureOmitsEveryForeignAddress`; `BooruClaimMapperTests.PresenceMismatchAndForeignOrMismatchedUrlsAreRejected` | `post-non-cdn-url.json` |
| cancellation | `DanbooruSourceTests.CancellationTokenPropagatesToTheHost`, `CancelledCallsThrowInsteadOfReturningAResult`; `BooruHostHttpTests.CancellationDuringProviderSpacingStopsWithoutTransportCall` | fake transport |

## Where each user-visible text is checked

`BooruStatusCatalogTests` checks that every failure, postponement, watcher,
search-text, post-link and original-file code that the host, the plugin and the
watcher domain produce has a text in English, German, Spanish, French and Russian
(Spanish, French and Russian are unreviewed machine translations, see
`Desktop/Localization/Packs/README.md`). It reads the code lists from the source
files, so a new code without a text fails the test. Texts of the booru screens (B6)
use the `booru.ui.*` key prefix and are not covered by this catalog.
