# Booru test matrix (MeowChan 1.1.0, Danbooru)

Documentation reconciliation, 14.11.2026, against integrated base
`63fd4190407973026d2a9ac96c479aa191325868`.
The original inventory was written at `3c675786` plus B8; its B4/B5-*open* labels
were historical missing-wiring/test notes, not a description of today's source.
The tables below index **synthetic fixtures and test source**, not test execution.
“Present” or “covered” means a test exists; neither means a current acceptance PASS.
Remaining execution/audit gaps are listed separately below.

The scenarios follow package B9 of the 1.1.0 Booru work plan
(`docs/architecture/gesamtprogramm-2026-10-31/pakete/1.1.0-booru-arbeitsplan-2026-11-12.md`).
The current B1 host bounds valid Retry-After values by the configurable
`Config.BooruRetryAfterCooldownHours` setting: default 24 hours, range 1–168 hours.
For 429, the wait is at least 60 seconds and at most the live configured cap;
invalid, absent, expired or shorter values keep the minimum. Calendar arithmetic
also saturates at the maximum representable date; that technical bound does not
replace the configurable cap. Lowering the live cap can shorten an existing
in-memory source fence. A 429 stops its job without retry; a fresh job can send
after the fence expires, subject to normal pacing and budgets. This
documentation records test source and static reconciliation, without claiming a run.
B13 UI details in the help were read from `6e8e4437`, which is an ancestor of the
integrated base; native UI acceptance remains a separate open gate.
`tests/test_plugin_packaging.py` checks that cited tests, fixtures and files exist;
that check cannot establish runtime correctness.

## Branch B9 — bounded selection and queue batches (17.11.2026)

This branch's B9 selection increment is distinct from the historical package-B9
acceptance inventory below. B10 is not part of this increment.

- Selection is **page-scoped, at most 10 posts**, using explicit checkboxes.
  Attempting an eleventh selection shows a localized rejection and leaves that
  checkbox unchecked; no selected subset is silently truncated. Previewing,
  opening a viewer, editing input and thumbnail recovery select nothing.
- “Load selected originals” snapshots only checked post IDs, in visible order.
  There is no page-wide/select-all fallback. New pages, searches and invalid
  submissions clear selection and its batch recovery; unsent edits do not.
- Batches serialize queue admissions. Counts are **per attempt**: successful queue
  handoffs (including already queued/local), failed handoffs, and unprocessed
  posts. These are not claims that file transfers finished. Downloads and later
  transfer failures remain in the existing normal queue/row lifecycle.
- Explicit batch recovery snapshots only failed and unprocessed IDs from that
  attempt, even if additional checkboxes were subsequently checked. Stored
  failures use the existing original-retry port; untouched originals use normal
  admission with the executed query/one-shot provenance. Restricted or missing
  originals fail locally, without substituting a sample or preview.
- “Stop batch after current handoff” waits for that atomic handoff to settle,
  then stops before the next post. Its success/failure is counted truthfully;
  remaining posts stay unprocessed and explicitly retryable. Already accepted
  jobs stay in the queue (individual queue cancellation remains separate).
  Independent admission cancellation also stops the batch and retains recovery.
- While a batch settles, selection, paging, search submission and conflicting
  row lifecycle commands are refused; double-clicks cannot launch another batch.
  Expiry/ticks never auto-retry metadata, images or originals. Batches are
  session-only; durable accepted jobs still belong to the existing queue.
- `BooruSelectionContractTests` covers these model contracts, the explicit UI
  wiring, all ten language packs, duplicate keys and placeholder parity.
  Scoped execution on 17.11.2026: `BooruSelectionContractTests` (10),
  `BooruPagingContractTests` (18) and `BooruRecoveryContractTests` (12):
  **40 passed, 0 failed, 0 skipped** (6.9490 seconds test time), with serialized
  compilation of the Desktop test target. The selection tests include all-pack
  duplicate-key and placeholder checks. `git diff --check` and an independent
  ten-pack JSON/duplicate-key check passed. No solution-wide build, whole-Booru
  selection, native UI smoke or broad acceptance run was performed.
- Supplementary localization-only validation of the compiled Desktop test DLL:
  `BundledLanguagePackCompletenessTests` plus the required-key and no-orphan
  `BooruLocalizationTests` methods: **14 passed, 0 failed, 0 skipped**
  (21.5205 seconds). The initial `dotnet test --no-build` invocation hit the
  30-second command limit under machine load; direct `dotnet vstest` passed
  within the same limit, with no timeout or assertion changes.

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

## Current lifecycle audit and acceptance gaps (14.11.2026)

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
- B1 terminal-status handling, job lifetime across pages/worker waits, configured
  429 cooldowns and 502/503 retry/header normalization have source coverage in the
  failure matrix above. The default-cap theory covers seven-day and Int32-maximum
  deltas and a fresh send after cap expiry; the configured-bound theory covers
  1, 24 and 168 hours. Separate cases cover a syntactically valid huge decimal,
  expired/near/far HTTP dates, live lowering of an existing fence and saturation
  near the calendar limit. Invalid-header regressions retain the 60-second (429)
  and two-second (503 retry) fallbacks. These cases need a joint deterministic
  final-freeze run with the watcher/admission lifecycle; none was executed here.
- The config regression cited above covers default, clone/copy, save/reload and
  rejection of unsafe cooldown values. `Desktop/PluginSession.cs` supplies the
  live setting to the host. This is source evidence, not a fresh integrated
  settings-to-host/UI, persistence/restart or watcher-fence acceptance run.
- `HelpDocumentationTests.DocumentedMetadataHttpBudgetsMatchTransportConstants`
  and `GermanCatalogDefaultsAndProviderCooldownContractMatchTheSameCode` still
  assert the obsolete uncapped-provider-deadline text (including the claim that
  only the technical date limit applies). **Open parent reconciliation:** align
  those assertions and the affected Help/guide text with the configurable cap,
  while retaining minimum-wait, malformed-input and technical-bound coverage.
  Merely passing those old text assertions would not validate today's cooldown
  contract. Neither those tests nor Help/guide files were changed or run here.
- Fresh native Booru workbench, offline media/gallery/favorites and accessibility
  acceptance, plus the real-old-profile upgrade run noted in scenario 8, remain
  unverified here. No build, restore, .NET test, native smoke or live-provider
  request was performed. Central final-freeze validation remains the parent's job.
