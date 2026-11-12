using System.IO;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Persistence;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>B5 production mapper between the watcher aggregate and the B3 record (restart contract).</summary>
public sealed class DanbooruWatcherRecordMapperTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromRecordFailsClosedOnPartialOrInconsistentRestartState()
    {
        BooruWatcherRecord valid = ValidMidTraversal();
        // Sanity: the base record restores.
        Assert.NotNull(DanbooruWatcherRecordMapper.FromRecord(valid));

        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { CursorBeforePostId = null }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { CheckStartHighWaterMarkCaptured = false }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { ResumeState = null }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { PageSize = null }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { PageBudget = null }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { CursorBeforePostId = null, CheckStartHighWaterMark = null, CheckStartHighWaterMarkCaptured = false, ResumeState = null, PageSize = null, PageBudget = null }));
        // Identity / format / bounds.
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { CursorBeforePostId = "01" }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { CheckStartHighWaterMark = "abc" }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { NextPage = -1 }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { PagesCheckedInRun = -1 }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { PageSize = 0 }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { PageBudget = DanbooruWatcher.MaximumPageBudget + 1 }));
        // Traversal fields without a traversal.
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { NextPage = 0 }));
        // Run counter cannot exceed the traversed pages or the budget.
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { PagesCheckedInRun = 3 }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(
            valid with { PagesCheckedInRun = 5 }));
    }

    [Fact]
    public void EveryLifecycleStateRoundTripsThroughTheStoreWithIdenticalBehaviour()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.Read();
        lease.Admission.CompleteStartup();

        DanbooruWatcher w = Created("w");
        List<DanbooruWatcher> states = [w];
        w = DanbooruWatcher.BeginCheck(w, T0).Watcher; states.Add(w);
        w = DanbooruWatcher.ApplyPage(w, Page(1, true, "100"), T0).Watcher; states.Add(w);
        w = DanbooruWatcher.BeginCheck(w, T0.AddMinutes(1)).Watcher; states.Add(w);
        for (int page = 1; page <= 5; page++)
        {
            w = DanbooruWatcher.ApplyPage(w, Page(page, false, (300 - page * 10 + 1).ToString(), (300 - page * 10).ToString()), T0).Watcher;
            states.Add(w);
        }
        DanbooruWatcher limited = w;
        DanbooruWatcher resumed = DanbooruWatcher.BeginCheck(limited, T0.AddMinutes(2)).Watcher;
        states.Add(resumed);
        states.Add(DanbooruWatcher.CancelCheck(resumed).Watcher);
        states.Add(DanbooruWatcher.ApplyError(resumed, "provider_error", T0).Watcher);
        states.Add(DanbooruWatcher.ApplyDeferred(resumed, T0.AddMinutes(5), "rate_limited", T0).Watcher);
        states.Add(DanbooruWatcher.Pause(resumed).Watcher);
        states.Add(DanbooruWatcher.Pause(limited).Watcher);
        states.Add(DanbooruWatcher.Remove(limited).Watcher);

        foreach (DanbooruWatcher state in states)
        {
            store.UpsertWatcher(DanbooruWatcherRecordMapper.ToRecord(state));
            DanbooruWatcher restored = DanbooruWatcherRecordMapper.FromRecord(store.Read().Watchers.Single());
            DanbooruWatcher expected = DanbooruWatcherRecordMapper.CompactKnownPostIds(state);
            Assert.Equal(expected.State, restored.State);
            Assert.Equal(expected.ResumeState, restored.ResumeState);
            Assert.Equal(expected.HighWaterMark, restored.HighWaterMark);
            Assert.Equal(expected.CheckStartHighWaterMark, restored.CheckStartHighWaterMark);
            Assert.Equal(expected.Progress.NextPage, restored.Progress.NextPage);
            Assert.Equal(expected.Progress.PagesCheckedInRun, restored.Progress.PagesCheckedInRun);
            Assert.Equal(expected.Progress.BudgetLimited, restored.Progress.BudgetLimited);
            Assert.Equal(expected.CursorPostId, restored.CursorPostId);
            Assert.Equal(expected.KnownPostIds, restored.KnownPostIds);
            Assert.Equal(expected.LastCheckedAtUtc, restored.LastCheckedAtUtc);
            Assert.Equal(expected.CooldownUntilUtc, restored.CooldownUntilUtc);
            Assert.Equal(expected.ErrorCode, restored.ErrorCode);
            Assert.Equal(expected.BaselineEstablished, restored.BaselineEstablished);
            Assert.Equal(expected.PageSize, restored.PageSize);
            Assert.Equal(expected.PageBudget, restored.PageBudget);
        }
    }

    [Fact]
    public void CompactionDropsOnlyIdsThatCanNeverBeNewAgainAndKeepsTransitionsIdentical()
    {
        DanbooruWatcher baseline = DanbooruWatcher.ApplyPage(DanbooruWatcher.BeginCheck(Created("w"), T0).Watcher,
            Page(1, true, "100", "99", "98"), T0).Watcher;
        Assert.Equal(3, baseline.KnownPostIds.Count);
        DanbooruWatcher compact = DanbooruWatcherRecordMapper.CompactKnownPostIds(baseline);
        Assert.Empty(compact.KnownPostIds); // all at or below the high-water mark

        // Mid-traversal: ids between the check-start fence and the high-water mark are kept.
        DanbooruWatcher running = DanbooruWatcher.ApplyPage(DanbooruWatcher.BeginCheck(compact, T0).Watcher,
            Page(1, false, "120", "119"), T0).Watcher;
        DanbooruWatcher runningCompact = DanbooruWatcherRecordMapper.CompactKnownPostIds(running);
        Assert.Equal(["119", "120"], runningCompact.KnownPostIds);

        DanbooruSearchPageObservation page2 = Page(2, false, "118", "100", "99");
        DanbooruWatcherTransition full = DanbooruWatcher.ApplyPage(running, page2, T0);
        DanbooruWatcherTransition reduced = DanbooruWatcher.ApplyPage(runningCompact, page2, T0);
        Assert.Equal(full.AddedPostIds, reduced.AddedPostIds);
        Assert.Equal(full.Watcher.State, reduced.Watcher.State);
        Assert.Equal(full.Watcher.HighWaterMark, reduced.Watcher.HighWaterMark);
        Assert.Equal(DanbooruWatcherRecordMapper.CompactKnownPostIds(full.Watcher).KnownPostIds,
            DanbooruWatcherRecordMapper.CompactKnownPostIds(reduced.Watcher).KnownPostIds);
    }

    [Fact]
    public void LongLivedWatcherStaysPersistableBelowTheStoreLimit()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.Read();
        lease.Admission.CompleteStartup();
        DanbooruWatcher w = DanbooruWatcher.ApplyPage(DanbooruWatcher.BeginCheck(Created("w"), T0).Watcher,
            Page(1, true, "1"), T0).Watcher;
        long next = 2;
        for (int check = 0; check < 200; check++)
        {
            w = DanbooruWatcher.BeginCheck(w, T0.AddHours(check)).Watcher;
            string[] ids = Enumerable.Range(0, 20).Select(index => (next + 19 - index).ToString()).ToArray();
            next += 20;
            w = DanbooruWatcher.ApplyPage(w, Page(1, true, ids), T0.AddHours(check)).Watcher;
            store.UpsertWatcher(DanbooruWatcherRecordMapper.ToRecord(w));
        }
        Assert.Empty(store.Read().Watchers.Single().KnownPostIds);
        Assert.True(w.KnownPostIds.Count >= 4000); // the raw aggregate would keep growing
    }

    [Fact]
    public void LegacyRecordWithoutRestartFieldsRestoresAndForeignOrNonCanonicalRecordsFailClosed()
    {
        BooruWatcherRecord legacy = new("w", "danbooru", "cat", "Paused", T0, HighWaterMark: "5", BaselineEstablished: true,
            KnownPostIds: ["5"]);
        DanbooruWatcher restored = DanbooruWatcherRecordMapper.FromRecord(legacy);
        Assert.Equal(DanbooruWatcherState.Paused, restored.State);
        Assert.Equal(DanbooruWatcherState.Paused, restored.ResumeState);
        Assert.Equal(DanbooruWatcher.DefaultPageBudget, restored.PageBudget);

        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(legacy with { SourceId = "gelbooru" }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(legacy with { Query = "cat  dog" }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(legacy with { State = "Sleeping" }));
        Assert.Throws<InvalidDataException>(() => DanbooruWatcherRecordMapper.FromRecord(legacy with { ResumeState = "Error" }));
    }

    private static BooruWatcherRecord ValidMidTraversal() => new(
        "w1", "danbooru", "cat", "Active", T0,
        LastCheckedAtUtc: T0.AddMinutes(-1),
        HighWaterMark: "120", NextPage: 3, BaselineEstablished: true,
        KnownPostIds: ["100", "117", "118", "119", "120"],
        CursorBeforePostId: "117", CheckStartHighWaterMark: "100", CheckStartHighWaterMarkCaptured: true,
        ResumeState: "Active", PageSize: 20, PageBudget: 5, PagesCheckedInRun: 0, BudgetLimited: false);

    private static DanbooruWatcher Created(string id)
    {
        Assert.True(DanbooruWatcher.TryCreate(id, "cat", T0, out DanbooruWatcher? watcher, out _));
        return watcher!;
    }

    private static DanbooruSearchPageObservation Page(int page, bool isEnd, params string[] ids) =>
        new(DanbooruPostReference.ProviderId, "cat", page, ids, isEnd);
}
