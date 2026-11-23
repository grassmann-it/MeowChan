# Danbooru test coverage

The tables index synthetic fixtures and test source.

The host bounds valid Retry-After values by the configurable
`Config.BooruRetryAfterCooldownHours` setting: default 24 hours, range 1–168 hours.
For 429, the wait is at least 60 seconds and at most the live configured cap;
invalid, absent, expired or shorter values keep the minimum. Calendar arithmetic
also saturates at the maximum representable date; that technical bound does not
replace the configurable cap. Lowering the live cap can shorten an existing
in-memory source fence. A 429 stops its job without retry; a fresh job can send
after the fence expires, subject to normal pacing and budgets.
`tests/test_plugin_packaging.py` checks that the index matches the test tree.

## Rules for fixtures and stubs

- Every fixture is synthetic and hand-written. No real post, user, tag history or
  credential is copied into the repository. Fixture addresses use invented ids and
  only two kinds of host: the provider's media CDN host (the plugin accepts media
  addresses only there, so the fixtures must use it) and reserved `.invalid` names.
- No test contacts a booru site. HTTP behaviour is produced by in-memory fake
  transports, a fake clock and a loopback server bound to the test process.
- Fixtures for the provider's JSON are in
  `tests/MeowChan.OfficialPlugins.Tests/Fixtures/danbooru/` (see its README for the
  per-file purpose).

## Test projects involved

| Project | Booru content |
| --- | --- |
| `tests/MeowChan.PluginSdk.Tests` | `BooruContractTests` (SDK records, validation) |
| `tests/MeowChan.OfficialPlugins.Tests` | `DanbooruSourceTests` with the fixture folder: plugin description, parsing, HTTP status mapping |
| `tests/MeowChan.PluginRuntime.Tests` | `BooruHostHttpTests` (pacing, budget, retry, cooldown, limits), `BooruSearchServiceTests`, `BooruClaimMapperTests`, `BooruRealWorkerTests` (real worker against a loopback stub), `BooruWatcherSchedulerHostTests`, `BooruWatcherRealWorkerTests`, `BooruB9AcceptanceTests` |
| `tests/MeowChan.Tests/Booru` | `DanbooruTagQueryTests`, `DanbooruTagQueryBoundaryRegressionTests`, `DanbooruWatcherDomainTests`, `BooruOriginalDownloadTests`, `BooruCatalogStoreTests`; `BooruOriginalQueueTests`, `tests/MeowChan.Tests/Booru/BooruOriginalLifecycleHardeningTests.cs`, `tests/MeowChan.Tests/Booru/BooruOriginalCrashWindowTests.cs`, `DanbooruWatcherSchedulerTests`, `BooruWatcherRestartContractTests`; plus `DanbooruQueryAdversarialRegressionTests` |
| `tests/MeowChan.DesktopTests` | `DesktopPluginSessionIntegrationTests` (delivered plugin), `BooruStatusCatalogTests` (texts), `BundledLanguagePackCompletenessTests`, `BooruCompositionHostTests`, `BooruMediaHostTests`, `BooruWorkbenchFlowTests`, `BooruLocalizationTests`, `HelpDocumentationTests` |
| `tests/test_plugin_packaging.py`, `tests/PluginDeliverySmoke` | Danbooru in the plugin package inventory |

## Integration scenarios

| # | Scenario | Covered by (synthetic fixture or stub) | State |
| --- | --- | --- | --- |
| 1 | Search with several valid provider tags, no hidden rating filter | `DanbooruTagQueryTests.TwoLiteralTagsPlusFreeRatingAreAcceptedWithoutInventingAnOrderMetatag`, `DanbooruWatcherDomainTests.CanonicalQueryIsDeterministicAndNeverInjectsRatingOrPrivateTags`, `BooruSearchServiceTests.ValidDanbooruQueryUsesProviderQueryFixedLimitAndHostJobResource`, `BooruRealWorkerTests.DanbooruPluginSearchUsesLoopbackWireAndInvalidQueryDoesNotStartWorkerCall` (exact request on the loopback wire); fixture `search-normal.json`; `BooruWorkbenchFlowTests.SearchThenSelectThenLoadOriginal`, `BooruB9AcceptanceTests.S1_SearchPreviewAndExplicitSelectionQueueOnlyTheChosenOriginalWithoutAHiddenRatingFilter` | host, workbench-model and composition test source present; fresh native UI run open |
| 2 | Provider rating and source tags separate from own tags | `DanbooruSourceTests.TagsKeepProviderSpellingAndDocumentedCategoryOrder`, `AbsentRatingLeavesTheFlagUnsetAndBadRatingsAreMalformed`, `BooruClaimMapperTests.AbsentFieldsBecomeUnsetCoreValuesAndDoNotInventPrivateMetadata`, `BooruCatalogStoreTests.RepeatedFindingsAndPostsAreIdempotentAndProviderTagsStayInThePostClaim`, `DanbooruTagQueryTests.ParsingOnlyUsesTheExplicitStringAndHasNoPrivateTagInputs`; fixtures `post-normal.json`, `post-null-fields.json`; `BooruLocalizationTests.TheWorkbenchAndTheDetailDialogNameTheProviderRatingAndTagCategoryAlike`, `BooruMediaHostTests` | mapping, storage and display-model test source present; native display acceptance open |
| 3 | One post link loads exactly one original, or reports a missing permission | `DanbooruSourceTests.PostRequestIsOneExactGet`, `ValidPostIdsAreAcceptedWithoutNormalizing`, `PostRestrictedFixtureKeepsAnOriginalWithoutUrlAndNeverBuildsOne`; `BooruOriginalDownloadTests.SelectsOnlyTheExplicitOriginalAndPreservesIntegrityClaims`, `DoesNotPromoteSampleOrPreviewWhenOriginalIsUnavailable`; `BooruRealWorkerTests.RealWorkerMapsSinglePost404ToNotFound`; fixtures `post-normal.json`, `post-restricted.json`; `DanbooruAddressTests.PostAddressYieldsThePostIdAndDropsQueryAndFragment`, `BooruOriginalQueueTests.NormalPostDownloadsOnlyTheOriginalLinksItsAssetOnceAndStaysOutOfChanState`, `BooruB9AcceptanceTests.S3_OneShotPostLinkLoadsExactlyOneOriginalReportsMissingOnesAndCreatesNoWatcher` | B4 parsing and real-queue wiring/test source present; not a fresh run |
| 4 | Same post from a search, a one-shot and two watchers is admitted once | `BooruCatalogStoreTests.RepeatedFindingsAndPostsAreIdempotentAndProviderTagsStayInThePostClaim`, `ClaimsFindingsWatchersAndAssetLinksSurviveAProfileRestart` (several findings, one post); `BooruOriginalQueueTests.SearchOneShotAndTwoWatchersShareExactlyOneQueueJobForTheSamePostOriginal`, `BooruB9AcceptanceTests.S4_SamePostFromSearchOneShotAndTwoWatchersIsTransferredOnceWithFourFindings` | B4/B5 shared-queue admission test source present; final-freeze replay open |
| 5 | Watcher create, baseline, new posts, pause, check now, cooldown, restart, remove | `DanbooruWatcherDomainTests` (all: `FirstCheckEstablishesFromNowBaselineWithoutHistoricalBackfill`, `NewPostsAreEmittedOnceAndKnownPostProgressIsNumeric`, `FivePageBudgetLeavesAResumableCursorInsteadOfClaimingCompletion`, `PauseResumeAndCancelPreserveTheNextPageWithoutARequest`, `CooldownAndErrorKeepWatcherAndBlockOnlyTheRelevantCheck`, `RestartRecoveryResumesAnInterruptedPartialPageAndDoesNotDuplicatePosts`, `RemoveIsTerminalAndDoesNotEraseKnownLocalIdentity`); `BooruCatalogStoreTests.WatchersRequireCanonicalQueriesAndPersistAllRestartState`, `RemovingWatcherKeepsWatcherClaimsAndLocalPost`; `DanbooruWatcherSchedulerTests`, `BooruWatcherSchedulerHostTests.TwoWatchersProduceRegularButPacedRequestsWithoutParallelism`, `BooruWatcherRealWorkerTests.RealPluginWatcherBaselineThenNewPostIsPacedAndAdmittedOnce`, `BooruB9AcceptanceTests.S5_WatcherBaselineNewPostsPauseManualCheckResumeRemoveAndRestartEndToEnd` | B5 scheduler, host and loopback-worker test source present; fresh lifecycle audit still open |
| 6 | Queue pause, cancel, restart and damaged bytes | `BooruOriginalQueueTests.PauseDuringTransferInterruptsAndResumesTheSameJob`, `CorruptOrTruncatedBytesFailClosedStayPendingAndCanBeRetried`, `CancelledOriginalIsNotReAdmittedAfterARestart`, `tests/MeowChan.Tests/Booru/BooruOriginalLifecycleHardeningTests.cs`, `tests/MeowChan.Tests/Booru/BooruOriginalCrashWindowTests.cs`, `BooruB9AcceptanceTests` | B4-specific pause/cancel/restart/corruption test source present; combined lifecycle acceptance open |
| 7 | Restart without network keeps local booru media, data and gallery readable | `BooruCatalogStoreTests.ClaimsFindingsWatchersAndAssetLinksSurviveAProfileRestart` (storage level); `BooruMediaHostTests.MediaPageGalleryAndFavoritesAgreeOnTheSameDownloadedPostAcrossAnOfflineRestart`, `BooruB9AcceptanceTests.S7_OfflineRestartKeepsBooruMediaMetadataAndWatchersReadableAndFailsOnlyVisibly` | B7 offline projection and host test source present; native gallery/favorites acceptance open |
| 8 | Upgrade of an old profile without booru data | `BooruCatalogStoreTests.NewBooruFileIsAnAdditiveOfflineMigrationForAnExistingProfile`, `MalformedAndFutureCatalogsFailClosed`, `OversizedCatalogFailsClosedBeforeDeserialization`, `DuplicatePostFindingAndWatcherIdentitiesFailClosed`, `SuccessfulWritesLeaveNoTemporaryCatalogFiles`, `EveryTruncationOfAWrittenCatalogFailsClosed`, `ZeroFilledCatalogAfterPowerLossFailsClosed`, `RecordsMissingASchemaOneKeyFailClosed`, `IdleRecordsWithoutLaterAdditiveKeysStayReadable`, `CatalogsWrittenByTheCurrentWriterKeepLoadingUnderTheRequiredKeys`, `InterruptedReplacementKeepsThePreviousCatalogAndItsCursor`, `PreparedRestoreFailsClosedForACatalogMissingItsHighWaterMark`; `BooruB9AcceptanceTests.S8_ProfileWithoutBooruDataStartsOfflineWithoutRewritesAndGainsOnlyTheAdditiveCatalog`; notes in `docs/releases/v1.1.0-booru-data.md` | synthetic storage/composition test source present; no real 1.0.2 profile run (B9) |
| 9 | Failure matrix, see the next table | see next table; `BooruB9AcceptanceTests`, `BooruWorkbenchFlowTests` | host/plugin and workbench-model test source present; fresh native failure-state run open |
| 10 | Packaging, localization, help text, no credentials or private tags in provider requests | `tests/test_plugin_packaging.py`, `tests/PluginDeliverySmoke/sdk_free_worker_probe.py`, `DesktopPluginSessionIntegrationTests` (Danbooru delivered, active, not a chan source), `BooruStatusCatalogTests`, `BundledLanguagePackCompletenessTests`; `BooruHostHttpTests.BooruRouteUsesExactUserAgentAndRejectsHeadBeforeTransport` and `BooruRealWorkerTests.DanbooruPluginSearchUsesLoopbackWireAndInvalidQueryDoesNotStartWorkerCall` (no `Authorization` or `Cookie` header on the wire); `DanbooruSourceTests.FailureMessagesNeverEchoTheQueryOrTagText`; `HelpDocumentationTests`, `BooruLocalizationTests`, `docs/user-guide/danbooru.md` | help and UI-localization tests present |

## Failure matrix of scenario 9

| Case | Covered by | Fixture or stub |
| --- | --- | --- |
| empty result | `DanbooruSourceTests.SearchEmptyFixtureIsAnEndSnapshotNotAFailure` | `search-empty.json` |
| restricted post (no original address) | `PostRestrictedFixtureKeepsAnOriginalWithoutUrlAndNeverBuildsOne` | `post-restricted.json` |
| deleted or banned post | `PostDeletedBannedFixtureCarriesBothFlagsAndValues` | `post-deleted-banned.json` |
| redirect (3xx) | `BooruHostHttpTests.RedirectIsTerminalAndDoesNotFollowLocation`, `UnconditionalNotModifiedStaysATerminalThreeHundredWithoutRetry` (a 304 only answers a conditional request: `ConditionalNotModifiedIsAnEmptyResponseAndReadsNoBody`); `PluginHostBooruRoutingTests.ChanSourceKeepsItsPolicyAndRedirectsBesideTheZeroRedirectBooruRoute` (chan sources keep their redirects); `DanbooruSourceTests` status table (301) | fake transport |
| 401 and 403 | `DanbooruSourceTests` (`access-denied` for both, HTML body on 403) | fake host response |
| 404 and 410 | `DanbooruSourceTests.PostNotFoundIsOnlyAVerified404`; `BooruRealWorkerTests.RealWorkerMapsSinglePost404ToNotFound` | fake host response, loopback stub |
| 429 and cooldown | `BooruHostHttpTests.TooManyRequestsStartsSourceCooldownAndDoesNotTouchTransportAgain`, `InvalidRetryAfterOnTooManyRequestsFallsBackToTheSixtySecondFloor`; `BooruHostHttpTests.FarFutureRetryAfterCooldownIsCappedAtTheDefaultMaximum`, `SyntacticallyValidHugeDeltaSaturatesAtTheConfiguredCap`, `RetryAfterDateIsCappedAndExpiredDateKeepsTheMinimum`, `RetryAfterDeltaAndConfiguredCapSaturateAtTechnicalDateLimit`; `BooruHostHttpContractTests.TooManyRequestsEnforcesSixtySecondFloorAndIsNotRetried`, `RateLimitedJobNeverResumesButNewJobsRespectSourceCooldown`, `CooldownHonorsRetryAfterDateAndSpansJobsUntilItElapses`, `RetryAfterCooldownIsCappedByTheConfiguredBound`, `LoweringTheLiveCapShortensAnExistingInMemoryFence`; `BooruCatalogLimitTests.RetryAfterCooldownSettingDefaultsCopiesPersistsAndRejectsUnsafeValues`; `BooruRealWorkerTests.RealWorkerReceivesProvider429AsDeferredAndSecondCallDoesNotHitStub`; `DanbooruSourceTests.RetryAfterHttpDateSetsTheDueTime`, `RetryAfterInThePastIsDueNowAndFarFutureIsCapped` (plugin-only parser fixture, not the host cooldown contract), `HostDeferredPassesThroughUnchanged` | fake transport and clock, loopback stub; 60-second minimum, configurable 1–168-hour maximum (24-hour default), malformed-input fallback, huge valid deltas, capped/expired dates, cap expiry and live lowering; technical date saturation remains a separate boundary. Plugin-only parsing is not the host fence or a live settings/UI acceptance |
| 502 and 503 | `BooruHostHttpTests.BadGatewayRetriesOnceAfterTwoSeconds`, `SecondFiveOhThreeIsReturnedWithoutAnotherRetry`, `InvalidRetryAfterOnFiveOhThreeFallsBackToTwoSecondRetry`; `BooruHostHttpContractTests.RetryAfterLongerThanTwoSecondsIsHonoredAndZeroStillWaitsTwoSeconds`, `RetryAfterBeyondTheJobDeadlineFailsAsBudgetWithoutWaitingOrRetrying`, `FinalFiveOhThreeRetryAfterIsNormalizedToTheLiveCap` | fake transport and clock; one retry no earlier than two seconds, bounded by live Retry-After cap and job deadline; the final 503 header is normalized to the live cap before the plugin sees it |
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
use the `booru.ui.*` key prefix and are not covered by this catalog; their separate
checks are in `BooruLocalizationTests` and `BundledLanguagePackCompletenessTests`.
Presence/completeness is not a native, screen-reader or translation-quality PASS.
