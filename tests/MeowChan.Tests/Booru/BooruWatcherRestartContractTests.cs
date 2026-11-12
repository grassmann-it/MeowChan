using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Persistence;

namespace MeowChan.Tests.Booru;

/// <summary>
/// B2 MUST-2 restart contract between <see cref="DanbooruWatcher"/> and <see cref="BooruWatcherRecord"/>.
/// <para>
/// Scope and boundary: these tests exercise the persistence/validation side of the contract directly.
/// There is deliberately <b>no production mapper</b> between the domain aggregate and the record yet
/// (B5 owns it). <see cref="ContractProjection"/> below is a TEST-ONLY, field-by-field projection whose
/// sole purpose is to prove that (a) every restart-critical domain field has a lossless storage
/// field, (b) every state the real domain produces is accepted by the store, and (c) a watcher rebuilt
/// from a stored record resumes instead of falsely completing. It must not be copied into production
/// as a "mapper"; the B5 mapper has to be written against the same field names and re-use these cases.
/// </para>
/// </summary>
public sealed class BooruWatcherRestartContractTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-12T12:00:00Z");

    // --- Additive compatibility ------------------------------------------------------------

    [Fact]
    public void LegacyRecordsWithoutRestartFieldsStayReadable()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string legacy = """
            {"schemaVersion":1,"posts":[],"watchers":[
              {"watcherId":"a","sourceId":"danbooru","query":"cat","state":"Active","createdAtUtc":"2026-11-12T12:00:00+00:00",
               "lastCheckedAtUtc":null,"cooldownUntilUtc":null,"errorCode":null,"highWaterMark":"100","nextPage":0,
               "baselineEstablished":true,"knownPostIds":["100"]},
              {"watcherId":"b","sourceId":"danbooru","query":"cat","state":"Paused","createdAtUtc":"2026-11-12T12:00:00+00:00",
               "lastCheckedAtUtc":null,"cooldownUntilUtc":null,"errorCode":null,"highWaterMark":null,"nextPage":0,
               "baselineEstablished":false,"knownPostIds":[]},
              {"watcherId":"c","sourceId":"danbooru","query":"cat","state":"Error","createdAtUtc":"2026-11-12T12:00:00+00:00",
               "lastCheckedAtUtc":null,"cooldownUntilUtc":null,"errorCode":"provider_error","highWaterMark":"7","nextPage":0,
               "baselineEstablished":true,"knownPostIds":["7"]}]}
            """;
        File.WriteAllText(Path.Combine(directory.Path, BooruCatalogStore.FileName), legacy);

        IReadOnlyList<BooruWatcherRecord> watchers = new BooruCatalogStore(directory.Path, lease).Read().Watchers;
        Assert.Equal(3, watchers.Count);
        foreach (BooruWatcherRecord watcher in watchers)
        {
            Assert.Equal(0, watcher.NextPage);
            Assert.Null(watcher.CursorBeforePostId);
            Assert.Null(watcher.CheckStartHighWaterMark);
            Assert.False(watcher.CheckStartHighWaterMarkCaptured);
            Assert.Null(watcher.ResumeState);
            Assert.Null(watcher.PageSize);
            Assert.Null(watcher.PageBudget);
            Assert.Equal(0, watcher.PagesCheckedInRun);
            Assert.False(watcher.BudgetLimited);
        }
    }

    [Fact]
    public void LegacyPartialRecordWithNextPageButNoRestartFieldsFailsClosed()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string legacyPartial = """
            {"schemaVersion":1,"posts":[],"watchers":[
              {"watcherId":"a","sourceId":"danbooru","query":"cat","state":"Active","createdAtUtc":"2026-11-12T12:00:00+00:00",
               "lastCheckedAtUtc":null,"cooldownUntilUtc":null,"errorCode":null,"highWaterMark":"120","nextPage":2,
               "baselineEstablished":true,"knownPostIds":["119","120"]}]}
            """;
        File.WriteAllText(Path.Combine(directory.Path, BooruCatalogStore.FileName), legacyPartial);
        Assert.Throws<InvalidDataException>(() => new BooruCatalogStore(directory.Path, lease).Read());
    }

    [Fact]
    public void RecordCarriesEveryRestartCriticalDomainFieldUnderTheSameName()
    {
        // Guard against silently dropping a restart field again (the original MUST-2 defect).
        string[] required =
        [
            nameof(BooruWatcherRecord.CursorBeforePostId), nameof(BooruWatcherRecord.CheckStartHighWaterMark),
            nameof(BooruWatcherRecord.ResumeState), nameof(BooruWatcherRecord.PageSize),
            nameof(BooruWatcherRecord.PageBudget), nameof(BooruWatcherRecord.PagesCheckedInRun),
            nameof(BooruWatcherRecord.BudgetLimited), nameof(BooruWatcherRecord.NextPage),
            nameof(BooruWatcherRecord.HighWaterMark), nameof(BooruWatcherRecord.BaselineEstablished)
        ];
        HashSet<string> recordMembers = typeof(BooruWatcherRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet();
        foreach (string name in required) Assert.Contains(name, recordMembers);
        // Matching domain members still exist, so a rename on either side fails this test.
        Type progress = typeof(DanbooruWatcherProgress);
        Type watcher = typeof(DanbooruWatcher);
        Assert.NotNull(progress.GetProperty(nameof(DanbooruWatcherProgress.CursorBeforePostId), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        foreach (string name in new[]
                 {
                     nameof(DanbooruWatcher.CheckStartHighWaterMark), nameof(DanbooruWatcher.ResumeState),
                     nameof(DanbooruWatcher.PageSize), nameof(DanbooruWatcher.PageBudget)
                 })
            Assert.NotNull(watcher.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }

    // --- Persistence round trip ------------------------------------------------------------

    [Fact]
    public void FullTraversalStateRoundTripsThroughDiskAndAProfileRestart()
    {
        using TempDirectory directory = new();
        BooruWatcherRecord running = ValidMidTraversal() with
        {
            State = "Checking", ResumeState = "Paused", PagesCheckedInRun = 2, NextPage = 3
        };
        BooruWatcherRecord budgetLimited = ValidMidTraversal() with
        {
            WatcherId = "w-budget", NextPage = 6, PagesCheckedInRun = 0, BudgetLimited = true,
            PageSize = 20, PageBudget = 5
        };
        BooruWatcherRecord emptyFence = ValidMidTraversal() with
        {
            WatcherId = "w-empty-fence", CheckStartHighWaterMark = null, CheckStartHighWaterMarkCaptured = true
        };
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruCatalogStore store = new(directory.Path, lease);
            store.UpsertWatcher(running);
            store.UpsertWatcher(budgetLimited);
            store.UpsertWatcher(emptyFence);
        }

        using ProfileLease reopened = ProfileLease.Acquire(directory.Path);
        IReadOnlyList<BooruWatcherRecord> saved = new BooruCatalogStore(directory.Path, reopened).Read().Watchers;
        AssertRestartFieldsEqual(running, saved.Single(w => w.WatcherId == running.WatcherId));
        AssertRestartFieldsEqual(budgetLimited, saved.Single(w => w.WatcherId == "w-budget"));
        AssertRestartFieldsEqual(emptyFence, saved.Single(w => w.WatcherId == "w-empty-fence"));
        Assert.True(saved.Single(w => w.WatcherId == "w-budget").BudgetLimited);
        Assert.True(saved.Single(w => w.WatcherId == "w-empty-fence").CheckStartHighWaterMarkCaptured);
        Assert.Null(saved.Single(w => w.WatcherId == "w-empty-fence").CheckStartHighWaterMark);
    }

    [Fact]
    public void RemovingAWatcherDropsTraversalProgressButKeepsKnownIdsAndLimits()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertWatcher(ValidMidTraversal());
        Assert.True(store.MarkWatcherRemoved("w1"));

        BooruWatcherRecord removed = Assert.Single(store.Read().Watchers);
        Assert.Equal("Removed", removed.State);
        Assert.Equal(0, removed.NextPage);
        Assert.Null(removed.CursorBeforePostId);
        Assert.Null(removed.CheckStartHighWaterMark);
        Assert.False(removed.CheckStartHighWaterMarkCaptured);
        Assert.Equal(0, removed.PagesCheckedInRun);
        Assert.False(removed.BudgetLimited);
        Assert.Equal(ValidMidTraversal().KnownPostIds, removed.KnownPostIds);
        Assert.Equal(20, removed.PageSize);
        Assert.Equal(5, removed.PageBudget);
    }

    // --- Fail-closed validation ------------------------------------------------------------

    public static IEnumerable<object[]> InvalidRestartRecords() => InvalidCases().Select(c => new object[] { c.Key });

    private static Dictionary<string, Func<BooruWatcherRecord, BooruWatcherRecord>> InvalidCases() => new()
    {
        // Partial traversal missing a mandatory resume field.
        ["missing-cursor"] = r => r with { CursorBeforePostId = null },
        ["missing-check-start-capture"] = r => r with { CheckStartHighWaterMark = null, CheckStartHighWaterMarkCaptured = false },
        ["missing-resume-state"] = r => r with { ResumeState = null },
        ["missing-page-size"] = r => r with { PageSize = null },
        ["missing-page-budget"] = r => r with { PageBudget = null },
        ["nothing-but-next-page"] = r => r with
        {
            CursorBeforePostId = null, CheckStartHighWaterMark = null, CheckStartHighWaterMarkCaptured = false,
            ResumeState = null, PageSize = null, PageBudget = null
        },
        // Identity / format.
        ["cursor-not-a-post-id"] = r => r with { CursorBeforePostId = "01" },
        ["cursor-overlong"] = r => r with { CursorBeforePostId = new string('9', 19) },
        ["check-start-not-a-post-id"] = r => r with { CheckStartHighWaterMark = "abc" },
        ["check-start-without-flag"] = r => r with { CheckStartHighWaterMarkCaptured = false },
        ["resume-state-unknown"] = r => r with { ResumeState = "Sleeping" },
        ["resume-state-checking"] = r => r with { ResumeState = "Checking" },
        ["resume-state-lowercase"] = r => r with { ResumeState = "active" },
        // Bounds.
        ["next-page-over-store-cap"] = r => r with { NextPage = BooruCatalogStore.MaximumWatcherPage + 1 },
        ["next-page-negative"] = r => r with { NextPage = -1 },
        ["page-size-zero"] = r => r with { PageSize = 0 },
        ["page-size-over-domain-max"] = r => r with { PageSize = DanbooruWatcher.MaximumPageSize + 1 },
        ["page-budget-zero"] = r => r with { PageBudget = 0 },
        ["page-budget-over-domain-max"] = r => r with { PageBudget = DanbooruWatcher.MaximumPageBudget + 1 },
        ["pages-checked-negative"] = r => r with { PagesCheckedInRun = -1 },
        ["pages-checked-reaches-budget"] = r => r with { State = "Checking", PagesCheckedInRun = 5, NextPage = 6 },
        ["pages-checked-exceeds-traversed-pages"] = r => r with { State = "Checking", PagesCheckedInRun = 2, NextPage = 2 },
        ["pages-checked-outside-running-check"] = r => r with { PagesCheckedInRun = 1 },
        // State consistency.
        ["active-resumes-as-paused"] = r => r with { ResumeState = "Paused" },
        ["paused-resumes-as-active"] = r => r with { State = "Paused", ResumeState = "Active" },
        ["checking-without-page"] = r => r with
        {
            State = "Checking", NextPage = 0, CursorBeforePostId = null, CheckStartHighWaterMark = null,
            CheckStartHighWaterMarkCaptured = false
        },
        ["traversal-fields-without-next-page"] = r => r with { NextPage = 0 },
        ["removed-keeps-traversal"] = r => r with { State = "Removed" },
        ["continuation-before-baseline"] = r => r with { BaselineEstablished = false },
        ["first-page-with-cursor"] = r => r with { NextPage = 1 },
        ["first-page-budget-limited"] = r => r with
        {
            NextPage = 1, CursorBeforePostId = null, BudgetLimited = true
        },
        // Cursor / fence geometry (a stored partial traversal must be able to continue downward).
        ["cursor-above-high-water"] = r => r with { CursorBeforePostId = "121" },
        ["cursor-without-high-water"] = r => r with { HighWaterMark = null, CheckStartHighWaterMark = null },
        ["check-start-above-high-water"] = r => r with { CheckStartHighWaterMark = "121" },
        ["cursor-at-check-start"] = r => r with { CursorBeforePostId = "100" },
        ["cursor-below-check-start"] = r => r with { CursorBeforePostId = "99" },
        // Budget limit.
        ["budget-limited-before-budget-spent"] = r => r with { BudgetLimited = true, NextPage = 3 },
        ["budget-limited-while-checking"] = r => r with
        {
            BudgetLimited = true, State = "Checking", NextPage = 6, PagesCheckedInRun = 0
        },
        ["budget-limited-with-run-counter"] = r => r with
        {
            BudgetLimited = true, NextPage = 6, PagesCheckedInRun = 1, State = "Checking"
        },
        ["budget-limited-resume-mismatch"] = r => r with { BudgetLimited = true, NextPage = 6, ResumeState = "Paused" }
    };

    [Theory]
    [MemberData(nameof(InvalidRestartRecords))]
    public void InvalidRestartRecordsAreRejectedOnWriteAndOnRead(string caseName)
    {
        BooruWatcherRecord invalid = InvalidCases()[caseName](ValidMidTraversal());

        using (TempDirectory directory = new())
        {
            using ProfileLease lease = ProfileLease.Acquire(directory.Path);
            BooruCatalogStore store = new(directory.Path, lease);
            Assert.Throws<ArgumentException>(() => store.UpsertWatcher(invalid));
            Assert.Empty(store.Read().Watchers);
            Assert.False(File.Exists(store.FilePath));
        }

        using (TempDirectory directory = new())
        {
            using ProfileLease lease = ProfileLease.Acquire(directory.Path);
            File.WriteAllText(Path.Combine(directory.Path, BooruCatalogStore.FileName),
                Json(new BooruCatalogSnapshot(1, [], [invalid])));
            Assert.Throws<InvalidDataException>(() => new BooruCatalogStore(directory.Path, lease).Read());
        }
    }

    [Fact]
    public void TheValidBaseRecordItselfIsAccepted()
    {
        // Sanity: the mutations above start from a valid record, so every rejection is the mutation's.
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertWatcher(ValidMidTraversal());
        AssertRestartFieldsEqual(ValidMidTraversal(), Assert.Single(store.Read().Watchers));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(BooruCatalogStore.MaximumWatcherPage)]
    public void CursorContinuationsBeyondTheFormerSixPageCapArePersistedAndRestored(int nextPage)
    {
        // Pages after the first are requested below the cursor, so the page ordinal of a long catch-up
        // grows across checks; the store keeps it like any other in-flight traversal.
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        BooruWatcherRecord deep = ValidMidTraversal() with { NextPage = nextPage, BudgetLimited = true };
        store.UpsertWatcher(deep);
        BooruWatcherRecord stored = Assert.Single(new BooruCatalogStore(directory.Path, lease).Read().Watchers);
        AssertRestartFieldsEqual(deep, stored);
        DanbooruWatcher restored = DanbooruWatcherRecordMapper.FromRecord(stored);
        Assert.Equal(nextPage, restored.Progress.NextPage);
        Assert.Equal("117", restored.CursorPostId);
    }

    // --- Real domain states vs. the store (test-only projection) --------------------------

    [Fact]
    public void EveryStateTheRealDomainProducesIsAcceptedByTheStore()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);

        int stored = 0;
        void Persist(DanbooruWatcher watcher)
        {
            BooruWatcherRecord record = ContractProjection.ToRecord(watcher);
            store.UpsertWatcher(record);
            AssertRestartFieldsEqual(record, Assert.Single(store.Read().Watchers, w => w.WatcherId == watcher.WatcherId));
            stored++;
        }

        // Baseline, catch-up with budget exhaustion across runs, cancel, error, cooldown, pause, remove.
        DanbooruWatcher w = Created("w-sweep");
        Persist(w);
        w = DanbooruWatcher.BeginCheck(w, Now).Watcher; Persist(w);                        // Checking, page 1, nothing applied
        w = DanbooruWatcher.ApplyPage(w, Page(1, true, "100"), Now).Watcher; Persist(w);  // baseline done
        w = DanbooruWatcher.BeginCheck(w, Now.AddMinutes(1)).Watcher; Persist(w);
        for (int page = 1; page <= 5; page++)
        {
            w = DanbooruWatcher.ApplyPage(w, Page(page, false, (300 - page * 10 + 1).ToString(), (300 - page * 10).ToString()), Now).Watcher;
            Persist(w);                                                                    // pages 1..4 partial, 5th budget-limited
        }
        Assert.True(w.Progress.BudgetLimited);
        Assert.Equal(6, w.Progress.NextPage);
        DanbooruWatcher limited = w;

        DanbooruWatcher resumed = DanbooruWatcher.BeginCheck(limited, Now.AddMinutes(2)).Watcher; Persist(resumed);
        Persist(DanbooruWatcher.CancelCheck(resumed).Watcher);
        Persist(DanbooruWatcher.ApplyError(resumed, "provider_error", Now).Watcher);
        Persist(DanbooruWatcher.ApplyDeferred(resumed, Now.AddMinutes(5), "rate_limited", Now).Watcher);
        Persist(DanbooruWatcher.Pause(resumed).Watcher);                                   // Paused mid-traversal
        Persist(DanbooruWatcher.Pause(limited).Watcher);
        Persist(DanbooruWatcher.RecoverAfterRestart(resumed));
        Persist(DanbooruWatcher.Remove(limited).Watcher);
        Assert.True(stored >= 14);
    }

    [Fact]
    public void WatcherWithAnEmptyBaselineFenceRestoresWithoutFalseCompletion()
    {
        // Empty baseline => HighWaterMark null => a captured but null check-start fence.
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DanbooruWatcher w = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(Created("w-empty"), Now).Watcher, Page(1, true), Now).Watcher;
        w = DanbooruWatcher.BeginCheck(w, Now.AddMinutes(1)).Watcher;
        w = DanbooruWatcher.ApplyPage(w, Page(1, false, "9", "8"), Now).Watcher;
        Assert.Equal(DanbooruWatcherState.Checking, w.State);
        Assert.Null(w.CheckStartHighWaterMark);

        store.UpsertWatcher(ContractProjection.ToRecord(w));
        DanbooruWatcher restored = ContractProjection.Restore(Created("w-empty"), Assert.Single(store.Read().Watchers));
        DanbooruWatcher recovered = DanbooruWatcher.RecoverAfterRestart(restored);
        DanbooruWatcherTransition next = DanbooruWatcher.BeginCheck(recovered, Now.AddMinutes(2));
        Assert.Equal(2, next.RequestedPage);
        Assert.Equal("8", next.RequestedCursorBeforePostId);
        Assert.Equal(DanbooruWatcherState.Checking,
            DanbooruWatcher.ApplyPage(next.Watcher, Page(2, false, "7", "6"), Now).Watcher.State);
    }

    // The scenario of review MUST-2 (WatcherRebuiltFromPersistedFieldsOnlyDoesNotFalselyCompleteAPartialTraversal),
    // restated directly against the storage boundary. The boundary-review test file is owned elsewhere
    // and stays skipped until the domain MUST-1 fix lands; this test asserts only the persistence half.
    [Fact]
    public void WatcherRebuiltFromAStoredRecordResumesInsteadOfFalselyCompleting()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DanbooruWatcher w = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(Created("w-restart"), Now).Watcher, Page(1, true, "100"), Now).Watcher;
        w = DanbooruWatcher.BeginCheck(w, Now.AddMinutes(1)).Watcher;
        w = DanbooruWatcher.ApplyPage(w, Page(1, false, "120", "119"), Now).Watcher;

        store.UpsertWatcher(ContractProjection.ToRecord(w));
        DanbooruWatcher restored = ContractProjection.Restore(Created("w-restart"), Assert.Single(store.Read().Watchers));
        DanbooruWatcher recovered = DanbooruWatcher.RecoverAfterRestart(restored);

        // The rebuilt watcher still has its fence and cursor ...
        Assert.Equal("100", recovered.CheckStartHighWaterMark);
        Assert.Equal("119", recovered.CursorPostId);
        DanbooruWatcherTransition next = DanbooruWatcher.BeginCheck(recovered, Now.AddMinutes(2));
        Assert.Equal(2, next.RequestedPage);
        Assert.Equal("100", next.Watcher.CheckStartHighWaterMark);
        // ... and a mid-catch-up page 2 keeps the check running instead of declaring completion.
        DanbooruWatcher afterPage2 = DanbooruWatcher.ApplyPage(next.Watcher, Page(2, false, "118", "117"), Now).Watcher;
        Assert.Equal(DanbooruWatcherState.Checking, afterPage2.State);
        Assert.Equal("117", afterPage2.CursorPostId);
        Assert.Equal(3, afterPage2.Progress.NextPage);
    }

    [Fact]
    public void PausedDuringACheckStillResumesPausedAfterRestart()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DanbooruWatcher w = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(Created("w-paused"), Now).Watcher, Page(1, true, "100"), Now).Watcher;
        w = DanbooruWatcher.Pause(w).Watcher;
        w = DanbooruWatcher.BeginCheck(w, Now.AddMinutes(1), manual: true).Watcher;
        w = DanbooruWatcher.ApplyPage(w, Page(1, false, "120", "119"), Now).Watcher;
        Assert.Equal(DanbooruWatcherState.Checking, w.State);
        Assert.Equal(DanbooruWatcherState.Paused, w.ResumeState);

        store.UpsertWatcher(ContractProjection.ToRecord(w));
        DanbooruWatcher restored = ContractProjection.Restore(Created("w-paused"), Assert.Single(store.Read().Watchers));
        Assert.Equal(DanbooruWatcherState.Paused, DanbooruWatcher.RecoverAfterRestart(restored).State);
    }

    [Fact]
    public void BudgetLimitedProgressSurvivesRestartAndIsClearedByTheNextRun()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DanbooruWatcher w = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(Created("w-budget"), Now).Watcher, Page(1, true, "100"), Now).Watcher;
        w = DanbooruWatcher.BeginCheck(w, Now.AddMinutes(1)).Watcher;
        for (int page = 1; page <= 5; page++)
            w = DanbooruWatcher.ApplyPage(w, Page(page, false, (300 - page * 10 + 1).ToString(), (300 - page * 10).ToString()), Now).Watcher;
        Assert.True(w.Progress.BudgetLimited);

        store.UpsertWatcher(ContractProjection.ToRecord(w));
        BooruWatcherRecord saved = Assert.Single(store.Read().Watchers);
        Assert.True(saved.BudgetLimited);
        Assert.Equal(6, saved.NextPage);
        DanbooruWatcher restored = ContractProjection.Restore(Created("w-budget"), saved);
        Assert.True(restored.Progress.BudgetLimited);
        DanbooruWatcherTransition next = DanbooruWatcher.BeginCheck(restored, Now.AddMinutes(2));
        Assert.Equal(6, next.RequestedPage);
        Assert.False(next.Watcher.Progress.BudgetLimited);
    }

    // --- Helpers -------------------------------------------------------------------------------

    /// <summary>A valid in-flight traversal: Active, page 3 requested, cursor above fence below high water.</summary>
    private static BooruWatcherRecord ValidMidTraversal() => new(
        "w1", "danbooru", "cat", "Active", Now,
        LastCheckedAtUtc: Now.AddMinutes(-1),
        HighWaterMark: "120", NextPage: 3, BaselineEstablished: true,
        KnownPostIds: ["100", "117", "118", "119", "120"],
        CursorBeforePostId: "117", CheckStartHighWaterMark: "100", CheckStartHighWaterMarkCaptured: true,
        ResumeState: "Active", PageSize: 20, PageBudget: 5, PagesCheckedInRun: 0, BudgetLimited: false);

    private static void AssertRestartFieldsEqual(BooruWatcherRecord expected, BooruWatcherRecord actual)
    {
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.HighWaterMark, actual.HighWaterMark);
        Assert.Equal(expected.NextPage, actual.NextPage);
        Assert.Equal(expected.BaselineEstablished, actual.BaselineEstablished);
        Assert.Equal(expected.KnownPostIds, actual.KnownPostIds);
        Assert.Equal(expected.CursorBeforePostId, actual.CursorBeforePostId);
        Assert.Equal(expected.CheckStartHighWaterMark, actual.CheckStartHighWaterMark);
        Assert.Equal(expected.CheckStartHighWaterMarkCaptured, actual.CheckStartHighWaterMarkCaptured);
        Assert.Equal(expected.ResumeState, actual.ResumeState);
        Assert.Equal(expected.PageSize, actual.PageSize);
        Assert.Equal(expected.PageBudget, actual.PageBudget);
        Assert.Equal(expected.PagesCheckedInRun, actual.PagesCheckedInRun);
        Assert.Equal(expected.BudgetLimited, actual.BudgetLimited);
    }

    private static DanbooruWatcher Created(string id)
    {
        Assert.True(DanbooruWatcher.TryCreate(id, "cat", Now, out DanbooruWatcher? watcher, out _));
        return watcher!;
    }

    private static DanbooruSearchPageObservation Page(int page, bool isEnd, params string[] ids) =>
        new(DanbooruPostReference.ProviderId, "cat", page, ids, isEnd);

    private static string Json(BooruCatalogSnapshot snapshot)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return JsonSerializer.Serialize(snapshot, options);
    }

    /// <summary>
    /// TEST-ONLY field-by-field projection. It is NOT the production mapper (which does not exist yet and
    /// is owned by B5); it documents the 1:1 field correspondence the real mapper must honour and lets the
    /// tests above feed genuine domain states through the storage boundary.
    /// </summary>
    private static class ContractProjection
    {
        internal static BooruWatcherRecord ToRecord(DanbooruWatcher w) => new(
            w.WatcherId, DanbooruWatcher.ProviderId, w.CanonicalQuery, w.State.ToString(), w.CreatedAtUtc,
            w.LastCheckedAtUtc, w.CooldownUntilUtc, w.ErrorCode, w.HighWaterMark, w.Progress.NextPage,
            w.BaselineEstablished, w.KnownPostIds,
            CursorBeforePostId: w.Progress.CursorBeforePostId,
            CheckStartHighWaterMark: w.CheckStartHighWaterMark,
            // A fence exists exactly while a traversal is in flight, even when its value is null.
            CheckStartHighWaterMarkCaptured: w.Progress.NextPage > 0,
            ResumeState: w.ResumeState.ToString(),
            PageSize: w.PageSize,
            PageBudget: w.PageBudget,
            PagesCheckedInRun: w.Progress.PagesCheckedInRun,
            BudgetLimited: w.Progress.BudgetLimited);

        /// <summary>Applies a stored record onto a freshly created watcher with the same identity.</summary>
        internal static DanbooruWatcher Restore(DanbooruWatcher fresh, BooruWatcherRecord r)
        {
            Assert.Equal(fresh.WatcherId, r.WatcherId);
            Assert.Equal(fresh.CanonicalQuery, r.Query);
            return fresh with
            {
                State = Enum.Parse<DanbooruWatcherState>(r.State),
                LastCheckedAtUtc = r.LastCheckedAtUtc,
                CooldownUntilUtc = r.CooldownUntilUtc,
                ErrorCode = r.ErrorCode,
                HighWaterMark = r.HighWaterMark,
                BaselineEstablished = r.BaselineEstablished,
                KnownPostIds = r.KnownPostIds,
                Progress = new DanbooruWatcherProgress(r.NextPage, r.PagesCheckedInRun, r.BudgetLimited)
                {
                    CursorBeforePostId = r.CursorBeforePostId
                },
                CheckStartHighWaterMark = r.CheckStartHighWaterMark,
                ResumeState = r.ResumeState is null ? DanbooruWatcherState.Active : Enum.Parse<DanbooruWatcherState>(r.ResumeState),
                PageSize = r.PageSize ?? DanbooruWatcher.DefaultPageSize,
                PageBudget = r.PageBudget ?? DanbooruWatcher.DefaultPageBudget
            };
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meowchan-booru-restart-" + Guid.NewGuid().ToString("N"));
        internal TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
