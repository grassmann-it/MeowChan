# Booru test matrix (MeowChan 1.1.0, Danbooru)

Documentation reconciliation, 13.11.2026, against candidate `63247916`.
The original inventory was written at `3c675786` plus B8; its B4/B5-*open* labels
were historical missing-wiring/test notes, not a description of today's source.
The tables below index **synthetic fixtures and test source**, not test execution.
“Present” or “covered” means a test exists; neither means a current acceptance PASS.
Remaining execution/audit gaps are listed separately below.

The scenarios follow package B9 of the 1.1.0 Booru work plan
(`docs/architecture/gesamtprogramm-2026-10-31/pakete/1.1.0-booru-arbeitsplan-2026-11-12.md`).
The parent is separately checking B1 correction `b82741dd`; it is not in this
candidate's ancestry. Its corrected HTTP 429 contract honors longer valid
Retry-After values without a 24-hour cap (only the representable date limit).
This documentation anticipates that contract, without claiming integration or a run.
B13 UI details in the help were read from `6e8e4437`, likewise not integrated here.
`tests/test_plugin_packaging.py` checks that cited tests, fixtures and files exist;
that check cannot establish runtime correctness.

## Rules for fixtures and stubs

- Every fixture is synthetic and hand-written. No real post, user, tag history or
  credential is copied into the repository. Fixture addresses use invented ids and
  only two kinds of host: the provider's media CDN host (the plugin accepts media
  addresses only there, so the fixtures must use it) and reserved `.invalid` names.
  The fixture-content rule still needs review: the existing help-contract tests
  do not audit every fixture for copied private data.
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
| `tests/MeowChan.PluginRuntime.Tests` | `BooruHostHttpTests` (pacing, budget, retry, cooldown, limits), `BooruSearchServiceTests`, `BooruClaimMapperTests`, `BooruRealWorkerTests` (real worker against a loopback stub), `BooruWatcherSchedulerHostTests`, `BooruWatcherRealWorkerTests`, `BooruB9AcceptanceTests` |
| `tests/MeowChan.Tests/Booru` | `DanbooruTagQueryTests`, `DanbooruTagQueryBoundaryRegressionTests`, `DanbooruWatcherDomainTests`, `BooruOriginalDownloadTests`, `BooruCatalogStoreTests`; `BooruOriginalQueueTests`, `tests/MeowChan.Tests/Booru/BooruOriginalLifecycleHardeningTests.cs`, `tests/MeowChan.Tests/Booru/BooruOriginalCrashWindowTests.cs`, `DanbooruWatcherSchedulerTests`, `BooruWatcherRestartContractTests`; plus `DanbooruQueryAdversarialRegressionTests` |
| `tests/MeowChan.DesktopTests` | `DesktopPluginSessionIntegrationTests` (delivered plugin), `BooruStatusCatalogTests` (texts), `BundledLanguagePackCompletenessTests`, `BooruCompositionHostTests`, `BooruMediaHostTests`, `BooruWorkbenchFlowTests`, `BooruLocalizationTests`, `HelpDocumentationTests` |
| `tests/test_plugin_packaging.py`, `tests/PluginDeliverySmoke` | Danbooru in the plugin package inventory |

## Scenarios of package B9

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
| 10 | Packaging, localization, help text, no credentials or private tags in provider requests | `tests/test_plugin_packaging.py`, `tests/PluginDeliverySmoke/sdk_free_worker_probe.py`, `DesktopPluginSessionIntegrationTests` (Danbooru delivered, active, not a chan source), `BooruStatusCatalogTests`, `BundledLanguagePackCompletenessTests`; `BooruHostHttpTests.BooruRouteUsesExactUserAgentAndRejectsHeadBeforeTransport` and `BooruRealWorkerTests.DanbooruPluginSearchUsesLoopbackWireAndInvalidQueryDoesNotStartWorkerCall` (no `Authorization` or `Cookie` header on the wire); `DanbooruSourceTests.FailureMessagesNeverEchoTheQueryOrTagText`; `HelpDocumentationTests`, `BooruLocalizationTests`, `docs/user-guide/danbooru.md` | help and UI-localization tests now present, not historically missing B8 work; two stale cooldown help assertions still need parent alignment (see below) |

## Failure matrix of scenario 9

| Case | Covered by | Fixture or stub |
| --- | --- | --- |
| empty result | `DanbooruSourceTests.SearchEmptyFixtureIsAnEndSnapshotNotAFailure` | `search-empty.json` |
| restricted post (no original address) | `PostRestrictedFixtureKeepsAnOriginalWithoutUrlAndNeverBuildsOne` | `post-restricted.json` |
| deleted or banned post | `PostDeletedBannedFixtureCarriesBothFlagsAndValues` | `post-deleted-banned.json` |
| redirect (3xx) | `BooruHostHttpTests.RedirectIsTerminalAndDoesNotFollowLocation`, `UnconditionalNotModifiedStaysATerminalThreeHundredWithoutRetry` (a 304 only answers a conditional request: `ConditionalNotModifiedIsAnEmptyResponseAndReadsNoBody`); `PluginHostBooruRoutingTests.ChanSourceKeepsItsPolicyAndRedirectsBesideTheZeroRedirectBooruRoute` (chan sources keep their redirects); `DanbooruSourceTests` status table (301) | fake transport |
| 401 and 403 | `DanbooruSourceTests` (`access-denied` for both, HTML body on 403) | fake host response |
| 404 and 410 | `DanbooruSourceTests.PostNotFoundIsOnlyAVerified404`; `BooruRealWorkerTests.RealWorkerMapsSinglePost404ToNotFound` | fake host response, loopback stub |
| 429 and cooldown | `BooruHostHttpTests.TooManyRequestsStartsSourceCooldownAndDoesNotTouchTransportAgain` (minimum cooldown); longer valid Retry-After must not be shortened by the host to 24 hours after B1 correction `b82741dd` (parent integration/run pending); `BooruRealWorkerTests.RealWorkerReceivesProvider429AsDeferredAndSecondCallDoesNotHitStub`; `DanbooruSourceTests.RetryAfterHttpDateSetsTheDueTime`, `RetryAfterInThePastIsDueNowAndFarFutureIsCapped` (plugin-only parser fixture, not the corrected host cooldown contract), `HostDeferredPassesThroughUnchanged` | fake transport and clock, loopback stub; current host far-future assertions must be reconciled with B1, not treated as acceptance of the old cap |
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
use the `booru.ui.*` key prefix and are not covered by this catalog; their separate
checks are in `BooruLocalizationTests` and `BundledLanguagePackCompletenessTests`.
Presence/completeness is not a native, screen-reader or translation-quality PASS.

## Current lifecycle audit and acceptance gaps (13.11.2026)

Static wiring is present: `Desktop/Booru/BooruSessionComposition.cs` composes search,
original admission over the shared queue, watcher host and UI ports. The following
regression sources narrow the old B4/B5 gaps; they do not close final acceptance:

- The partial `BooruOriginalQueueTests` sources in
  `tests/MeowChan.Tests/Booru/BooruOriginalCrashWindowTests.cs` and
  `tests/MeowChan.Tests/Booru/BooruOriginalLifecycleHardeningTests.cs` exercise queue
  accept/acknowledgement, explicit retry, durable intent, stop, admission
  cancellation and failed acknowledgement. No current-freeze execution
  of those crash/cancel/recovery boundaries was performed in this documentation pass.
- `DanbooruWatcherSchedulerTests.StopCancelsAndDrainsADirectCheckBeforeReturning`,
  `AStepThatPickedWorkBeforeStopSendsNothingAfterIt`,
  `BooruWorkerInvalidationTests`, and
  `BooruCompositionHostTests.ShutdownStopsTheWatchersBeforeTheQueueAndTheRegistry`
  exist. A fresh integrated audit/run of late responses, pause/remove, plugin
  invalidation and profile shutdown across scheduler, host and queue is still open;
  source presence alone establishes neither quiescence nor absence of races.
- B1 terminal-status handling, job lifetime across pages/worker waits, and long
  429 cooldowns in `b82741dd` need parent integration and deterministic regression
  runs together with the watcher/admission lifecycle. Old capped host tests in the
  candidate are not evidence for the corrected long-cooldown contract.
- `HelpDocumentationTests.DocumentedMetadataHttpBudgetsMatchTransportConstants`
  still requires a “24-hour cap”; `GermanCatalogDefaultsAndCooldownBoundsMatchTheSameCode`
  still requires “höchstens 24 Stunden”. Both refer to the old MaximumCooldown
  constant. The parent must align these assertions with B1 and the corrected help;
  this three-document patch does not change test code or claim a green help suite.
- Fresh native Booru workbench, offline media/gallery/favorites and accessibility
  acceptance, plus the real-old-profile upgrade run noted in scenario 8, remain
  unverified here. No build, restore, .NET test, native smoke or live-provider
  request was performed. Central final-freeze validation remains the parent's job.
